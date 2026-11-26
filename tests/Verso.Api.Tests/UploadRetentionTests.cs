using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verso.Application;
using Verso.Application.Documents;
using Verso.Application.Executions;
using Verso.Application.Uploads;
using Verso.Domain.Ingestion;
using Verso.Infrastructure.Persistence;

namespace Verso.Api.Tests;

/// <summary>
/// The Worker's retention pass over staged uploads (ADR 0023): unused files expire (bytes cleared, metadata kept), rows with no run are
/// deleted, nothing else is touched. Time is a shifted clock, so nothing waits. Synthetic files only.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class UploadRetentionTests : IDisposable
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    private readonly MigratedDatabaseFixture _fixture;
    private readonly HttpClient _client;
    private readonly ExecutionTestKit _kit;
    private readonly TestDocumentStore _store = new();
    private readonly FakePdfPrinter _printer = new();
    private readonly RecordingExecutionJobs _jobs = new();

    public UploadRetentionTests(MigratedDatabaseFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Factory.CreateClient();
        _kit = new ExecutionTestKit(fixture.Factory, _client);
    }

    public void Dispose()
    {
        _store.Dispose();
        _client.Dispose();
    }

    private sealed class Operator : ICurrentUser
    {
        public string Id => "synthetic.operator";
        public string Name => "Synthetic Operator";
        public IReadOnlyCollection<string> Roles => [Verso.Application.Roles.Operator];
    }

    private sealed class ShiftedClock(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
    }

    private static byte[] SyntheticPdf() => Encoding.ASCII.GetBytes($"%PDF-1.7\n% synthetic statement {Guid.NewGuid():N}\n%%EOF\n");

    private async Task<T> WithDb<T>(Func<VersoDbContext, Task<T>> action)
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<VersoDbContext>());
    }

    private Task<int> PassAsync(TimeSpan clockOffset) => WithDb(db =>
        new UploadRetention(db, new ShiftedClock(clockOffset), Options.Create(new UploadOptions { Retention = Retention })).RunOnceAsync(CancellationToken.None));

    private Task<string> PublishAsync() => _kit.PublishInvoiceWorkflowAsync(null, trigger: new JsonObject
    {
        ["id"] = "trigger", ["type"] = "file-upload", ["title"] = "File upload", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 80 },
        ["config"] = new JsonObject { ["accept"] = "PDF", ["maxSize"] = "1mb", ["who"] = "staff" },
    });

    /// <summary>A real upload (row + run, committed together).</summary>
    private async Task<(Guid ExecutionId, Guid UploadId)> UploadAsync(string key)
    {
        var result = await WithDb(db =>
        {
            var user = new Operator();
            var service = new UploadService(db, new ExecutionService(db, _jobs, user, TimeProvider.System, NullLogger<ExecutionService>.Instance),
                user, TimeProvider.System, Options.Create(new UploadOptions()), NullLogger<UploadService>.Instance);
            return service.AcceptAsync(key, "statement.pdf", SyntheticPdf(), null, CancellationToken.None);
        });
        Assert.True(result.IsSuccess, result.Error?.Message);
        var executionId = result.Value!.Execution.ExecutionId;
        return (executionId, await WithDb(db => db.Uploads.Where(u => u.ExecutionId == executionId).Select(u => u.Id).SingleAsync()));
    }

    /// <summary>A staged row with no run at all, created <paramref name="age"/> ago.</summary>
    private async Task<Guid> StageOrphanAsync(TimeSpan age)
    {
        var bytes = SyntheticPdf();
        var upload = Upload.Stage(Guid.NewGuid(), "orphan.pdf", ".pdf", "application/pdf", bytes, DocumentKeys.Sha256Hex(bytes), "synthetic.operator", DateTimeOffset.UtcNow - age);
        await WithDb(async db =>
        {
            db.Uploads.Add(upload);
            return await db.SaveChangesAsync();
        });
        return upload.Id;
    }

    private Task<int> AgeAsync(Guid uploadId, TimeSpan age) => WithDb(db => db.Database.ExecuteSqlInterpolatedAsync(
        $"UPDATE [ingestion].[Uploads] SET [CreatedAt] = {DateTimeOffset.UtcNow - age} WHERE [Id] = {uploadId}"));

    private Task<Upload?> RowAsync(Guid uploadId) => WithDb(db => db.Uploads.AsNoTracking().SingleOrDefaultAsync(u => u.Id == uploadId));

    [Fact]
    public async Task Staged_older_than_retention_is_expired_and_content_cleared()
    {
        var (_, uploadId) = await UploadAsync(await PublishAsync());
        var before = (await RowAsync(uploadId))!;

        var count = await PassAsync(TimeSpan.FromHours(25));

        Assert.True(count >= 1);
        var row = (await RowAsync(uploadId))!;
        Assert.Equal(UploadStatus.Expired, row.Status);
        Assert.Null(row.Content);
        Assert.Equal(before.FileName, row.FileName);
        Assert.Equal(before.Size, row.Size);
        Assert.Equal(before.Sha256, row.Sha256);
        Assert.Equal(before.ExecutionId, row.ExecutionId);
    }

    [Fact]
    public async Task Staged_younger_is_untouched()
    {
        var (_, uploadId) = await UploadAsync(await PublishAsync());
        var orphanId = await StageOrphanAsync(TimeSpan.FromHours(1));

        await PassAsync(TimeSpan.FromHours(2));

        var row = (await RowAsync(uploadId))!;
        Assert.Equal(UploadStatus.Staged, row.Status);
        Assert.NotNull(row.Content);
        Assert.NotNull(await RowAsync(orphanId));
    }

    [Fact]
    public async Task Orphan_without_execution_is_deleted()
    {
        var orphanId = await StageOrphanAsync(TimeSpan.FromHours(25));

        var count = await PassAsync(TimeSpan.Zero);

        Assert.True(count >= 1);
        Assert.Null(await RowAsync(orphanId));
    }

    [Fact]
    public async Task Row_with_null_ExecutionId_but_an_existing_run_is_not_deleted()
    {
        // The link is saved after the run and can be lost: the run key upload:<id:N> is the truth, so the row is expired, never deleted.
        var (executionId, uploadId) = await UploadAsync(await PublishAsync());
        await WithDb(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [ingestion].[Uploads] SET [ExecutionId] = NULL WHERE [Id] = {uploadId}"));
        await AgeAsync(uploadId, TimeSpan.FromHours(25));

        await PassAsync(TimeSpan.Zero);

        var row = await RowAsync(uploadId);
        Assert.NotNull(row);
        Assert.Equal(UploadStatus.Expired, row.Status);
        Assert.Null(row.Content);
        Assert.True(await WithDb(db => db.Executions.AnyAsync(e => e.Id == executionId)));
    }

    [Fact]
    public async Task Taken_rows_keep_metadata_and_never_expire()
    {
        var (executionId, uploadId) = await UploadAsync(await PublishAsync());
        await _kit.RunEngineAsync(executionId, _printer, _store, _jobs); // the step takes the file
        Assert.Equal(UploadStatus.Taken, (await RowAsync(uploadId))!.Status);
        var before = (await RowAsync(uploadId))!;

        await PassAsync(TimeSpan.FromDays(30));

        var row = (await RowAsync(uploadId))!;
        Assert.Equal(UploadStatus.Taken, row.Status);
        Assert.Equal(before.FileName, row.FileName);
        Assert.Equal(before.Sha256, row.Sha256);
        Assert.Equal(before.ExecutionId, row.ExecutionId);
    }

    [Fact]
    public async Task Expired_run_fails_with_upload_expired()
    {
        var (executionId, uploadId) = await UploadAsync(await PublishAsync());

        await PassAsync(TimeSpan.FromHours(25));
        Assert.Equal(UploadStatus.Expired, (await RowAsync(uploadId))!.Status);
        await _kit.RunEngineAsync(executionId, _printer, _store, _jobs);

        var e = await _kit.GetExecutionAsync(executionId);
        Assert.Equal("Failed", e.GetProperty("status").GetString());
        Assert.Equal("upload.expired", e.GetProperty("failureCode").GetString());
    }

    [Fact]
    public async Task Pass_returns_count_and_is_idempotent()
    {
        var (_, uploadId) = await UploadAsync(await PublishAsync());
        var orphanId = await StageOrphanAsync(TimeSpan.Zero);
        await AgeAsync(uploadId, TimeSpan.FromHours(25));
        await AgeAsync(orphanId, TimeSpan.FromHours(25));

        var first = await PassAsync(TimeSpan.Zero);
        var second = await PassAsync(TimeSpan.Zero);

        Assert.True(first >= 2);
        Assert.Equal(0, second);
        Assert.Equal(UploadStatus.Expired, (await RowAsync(uploadId))!.Status);
        Assert.Null(await RowAsync(orphanId));
    }

    [Fact]
    public void Api_host_does_not_register_the_retention_pass()
    {
        // Retention runs in the Worker only: the API host has neither the pass nor its hosted service.
        using var scope = _fixture.Factory.Services.CreateScope();
        Assert.Null(scope.ServiceProvider.GetService<UploadRetention>());
        Assert.DoesNotContain(_fixture.Factory.Services.GetServices<IHostedService>(), h => h.GetType().Name == "UploadRetentionService");
    }
}
