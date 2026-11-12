using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Verso.Application;
using Verso.Application.Ingestion;
using Verso.Infrastructure.Ingestion;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Verso.Application.Executions;
using Verso.Infrastructure.Persistence;

namespace Verso.Api.Tests;

/// <summary>
/// tools/db/least-privilege.sql, applied to its own database: the API and the Worker run the whole MVP flow under
/// their roles (proving the grants suffice), and the write-once rules hold even for code that tries to break them.
/// Each identity is a loginless database user; every EF connection switches to it with EXECUTE AS USER.
/// (The Hangfire client keeps the test's own identity: its connections aren't EF's.)
/// </summary>
public sealed class LeastPrivilegeFixture : IAsyncLifetime
{
    public const string ApiUser = "verso_api_test";
    public const string WorkerUser = "verso_worker_test";

    private readonly TestDatabase _database = new("grants");

    internal string ConnectionString { get; private set; } = "";
    internal WebApplicationFactory<Program> Api { get; private set; } = null!;
    internal VersoApiFactory BaseFactory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _database.MigrateAsync();
        ConnectionString = new SqlConnectionStringBuilder(_database.ConnectionString) { Pooling = false }.ConnectionString; // EXECUTE AS can't return to a pool

        await using (var connection = new SqlConnection(_database.ConnectionString))
        {
            await connection.OpenAsync();
            foreach (var batch in Script().Split(["\nGO\n"], StringSplitOptions.RemoveEmptyEntries))
            {
                await Execute(connection, batch);
            }

            await Execute(connection, $"""
                CREATE USER [{ApiUser}] WITHOUT LOGIN;    ALTER ROLE verso_api ADD MEMBER [{ApiUser}];
                CREATE USER [{WorkerUser}] WITHOUT LOGIN; ALTER ROLE verso_worker ADD MEMBER [{WorkerUser}];
                """);
            await Execute(connection, Script()); // idempotent: a second run changes nothing and doesn't fail
        }

        BaseFactory = new VersoApiFactory(_database.ConnectionString);
        Api = BaseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.ConfigureDbContext<VersoDbContext>(o => o.UseSqlServer(ConnectionString).AddInterceptors(new RunAs(ApiUser)))));
    }

    public async Task DisposeAsync()
    {
        await Api.DisposeAsync();
        await BaseFactory.DisposeAsync();
        await _database.DropAsync();
    }

    internal VersoDbContext WorkerDb() =>
        new(new DbContextOptionsBuilder<VersoDbContext>().UseSqlServer(ConnectionString).AddInterceptors(new RunAs(WorkerUser)).Options);

    internal VersoDbContext DbAs(string user) =>
        new(new DbContextOptionsBuilder<VersoDbContext>().UseSqlServer(ConnectionString).AddInterceptors(new RunAs(user)).Options);

    private static string Script()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Verso.slnx")))
        {
            dir = dir.Parent;
        }

        return File.ReadAllText(Path.Combine(dir!.FullName, "tools", "db", "least-privilege.sql")).ReplaceLineEndings("\n");
    }

    private static async Task Execute(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Runs every connection of a context as the given database user.</summary>
    private sealed class RunAs(string user) : DbConnectionInterceptor
    {
        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"EXECUTE AS USER = '{user}'";
            command.ExecuteNonQuery();
        }

        public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"EXECUTE AS USER = '{user}'";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}

[Collection(ApiTestGroup.Name)] // keeps Hangfire hosts from configuring themselves in parallel
public sealed class LeastPrivilegeTests(LeastPrivilegeFixture grants) : IClassFixture<LeastPrivilegeFixture>, IDisposable
{
    private readonly TestDocumentStore _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task TheWholeFlow_RunsUnderTheApiAndWorkerRoles()
    {
        using var client = grants.Api.CreateClient();
        var kit = new ExecutionTestKit(grants.BaseFactory, client);

        var key = await kit.PublishInvoiceWorkflowAsync();               // API: author, publish
        var ok = await kit.SubmitNewAsync(key, ExecutionTestKit.Sample);   // API: submit
        var rejected = await kit.SubmitNewAsync(key, ExecutionTestKit.Broken);
        await RunAsWorkerAsync(ok);                                        // Worker: validate, PDF, archive, audit
        await RunAsWorkerAsync(rejected);
        await using (var db = grants.WorkerDb())
        {
            Assert.Equal(0, await new ExecutionDispatcher(db, new RecordingExecutionJobs(), TimeProvider.System).RunOnceAsync(CancellationToken.None));
        }

        var done = await kit.GetExecutionAsync(ok);                        // API: read
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal("Rejected", (await kit.GetExecutionAsync(rejected)).GetProperty("status").GetString());
        using var input = await client.GetAsync(new Uri($"/api/v1/executions/{ok}/input", UriKind.Relative)); // API: audited read
        Assert.Equal(HttpStatusCode.OK, input.StatusCode);
        using var audit = await client.GetAsync(new Uri("/api/v1/audit?pageSize=5", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        using var health = await client.GetAsync(new Uri("/api/health", UriKind.Relative));
        Assert.Contains("\"pendingMigrations\":0", await health.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }


    [Fact]
    public async Task FolderInput_RunsUnderTheApiAndWorkerRoles()
    {
        var root = Path.Combine(Path.GetTempPath(), "verso-test-folders", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "in"));
        Directory.CreateDirectory(Path.Combine(root, "out"));
        try
        {
            var folders = new LocalFolderAccess(Options.Create(new FolderAccessOptions
            {
                Locations = new()
                {
                    ["lp-in"] = new() { Name = "In", Path = Path.Combine(root, "in"), Input = true },
                    ["lp-out"] = new() { Name = "Out", Path = Path.Combine(root, "out"), Output = true },
                },
            }));
            var jobs = new RecordingExecutionJobs();
            async Task<FolderPassResult> PassAsync(Func<FolderIngestion, Task<FolderPassResult>> action)
            {
                await using var db = grants.WorkerDb();
                var executions = new ExecutionService(db, jobs, new WorkerUser(), TimeProvider.System, NullLogger<ExecutionService>.Instance);
                return await action(new FolderIngestion(db, folders, executions, new FolderIngestionOptions { SettleTime = TimeSpan.Zero }, TimeProvider.System, NullLogger<FolderIngestion>.Instance));
            }

            await PassAsync(async i => { await i.SyncLocationsAsync(CancellationToken.None); return null!; }); // Worker: report locations
            using var client = grants.Api.CreateClient();
            var kit = new ExecutionTestKit(grants.BaseFactory, client);
            var trigger = JsonNode.Parse("""
                { "id": "trigger", "type": "folder-input", "title": "Folder", "position": { "x": 0, "y": 80 },
                  "config": { "sourceLocation": "lp-in", "sourceSubfolder": "", "pattern": "*.json", "includeSubfolders": false, "interval": "every-minute",
                              "processedLocation": "lp-out", "processedSubfolder": "processed", "failedLocation": "lp-out", "failedSubfolder": "failed" } }
                """)!;
            await kit.PublishInvoiceWorkflowAsync(trigger: trigger);                                     // API: validate against locations, publish

            using var test = await client.PostAsJsonAsync(new Uri("/api/v1/folder-access-tests", UriKind.Relative),
                new { targets = new[] { new { role = "source", location = "lp-in", subfolder = "" } } });       // API: request a test
            Assert.Equal(HttpStatusCode.Accepted, test.StatusCode);
            var testId = (await ExecutionTestKit.ReadAsync(test)).GetProperty("id").GetGuid();
            await using (var db = grants.WorkerDb())
            {
                await new FolderAccessTestRunner(db, folders, TimeProvider.System).RunAsync(testId, CancellationToken.None); // Worker: run it
            }

            var sample = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "invoice", "sample.json"));
            File.WriteAllText(Path.Combine(root, "in", "a.json"), sample);
            File.WriteAllText(Path.Combine(root, "in", "copy.json"), sample);
            var busy = Path.Combine(root, "in", "busy.json");
            File.WriteAllText(busy, "{}");
            using (new FileStream(busy, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await PassAsync(i => i.RunOnceAsync(CancellationToken.None));                             // Worker: claim, submit, duplicate, release a busy file
            }

            var execution = Assert.Single(jobs.Enqueued);
            await RunAsWorkerAsync(execution);
            await PassAsync(i => i.RunOnceAsync(CancellationToken.None));                                 // Worker: move files
            await using (var db = grants.WorkerDb())
            {
                await new FolderAccessTestRunner(db, folders, new ShiftedClock(TimeSpan.FromDays(2))).CleanupAsync(CancellationToken.None); // Worker: cleanup
            }

            Assert.True(File.Exists(Path.Combine(root, "out", "processed", "a.json")));
            Assert.True(File.Exists(Path.Combine(root, "out", "failed", "copy.json")));
            var workflowId = await kit.WithDbAsync(db => db.FolderWatches.Select(w => w.WorkflowId).SingleAsync());
            using var status = await client.GetAsync(new Uri($"/api/v1/workflows/{workflowId}/folder-input", UriKind.Relative)); // API: read status
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ShiftedClock(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
    }

    [Theory]
    [InlineData(LeastPrivilegeFixture.ApiUser, "UPDATE [audit].[AuditEntries] SET [ActorName] = N'x'")]
    [InlineData(LeastPrivilegeFixture.ApiUser, "DELETE FROM [audit].[AuditEntries]")]
    [InlineData(LeastPrivilegeFixture.WorkerUser, "UPDATE [audit].[AuditEntries] SET [ActorName] = N'x'")]
    [InlineData(LeastPrivilegeFixture.WorkerUser, "DELETE FROM [audit].[AuditEntries]")]
    [InlineData(LeastPrivilegeFixture.WorkerUser, "UPDATE [documents].[ArchiveRecords] SET [FolderPath] = N'x'")]
    [InlineData(LeastPrivilegeFixture.WorkerUser, "DELETE FROM [documents].[ArchiveRecords]")]
    [InlineData(LeastPrivilegeFixture.WorkerUser, "DELETE FROM [documents].[Documents]")]
    [InlineData(LeastPrivilegeFixture.WorkerUser, "DELETE FROM [executions].[ExecutionEvents]")]
    [InlineData(LeastPrivilegeFixture.ApiUser, "INSERT INTO [documents].[ArchiveRecords] ([Id], [DocumentId], [ArchivedAt], [Retention], [FolderPath], [Metadata], [SearchText]) VALUES (NEWID(), NEWID(), SYSDATETIMEOFFSET(), '7y', N'x', N'{{}}', N'')")] // {{}}: ExecuteSqlRaw reads braces as format placeholders
    [InlineData(LeastPrivilegeFixture.ApiUser, "UPDATE [documents].[Documents] SET [FileName] = N'x'")]
    [InlineData(LeastPrivilegeFixture.ApiUser, "DELETE FROM [templates].[TemplateVersions]")]
    [InlineData(LeastPrivilegeFixture.WorkerUser, "UPDATE [templates].[TemplateVersions] SET [UpdatedBy] = N'x'")]
    [InlineData(LeastPrivilegeFixture.WorkerUser, "UPDATE [workflows].[WorkflowVersions] SET [UpdatedBy] = N'x'")]
    [InlineData(LeastPrivilegeFixture.ApiUser, "CREATE TABLE [dbo].[Sneaky] ([Id] int)")]
    [InlineData(LeastPrivilegeFixture.WorkerUser, "ALTER TABLE [audit].[AuditEntries] ADD [Sneaky] int NULL")]
    [InlineData(LeastPrivilegeFixture.ApiUser, "UPDATE [ingestion].[FolderFiles] SET [Status] = N'Completed'")]      // only the Worker changes ingestion records
    [InlineData(LeastPrivilegeFixture.ApiUser, "INSERT INTO [ingestion].[FolderLocations] ([Key], [Name], [AllowInput], [AllowOutput], [Enabled], [ReportedBy], [UpdatedAt]) VALUES ('c-drive', N'C', 1, 1, 1, N'x', SYSDATETIMEOFFSET())")] // only the Worker reports locations
    [InlineData(LeastPrivilegeFixture.WorkerUser, "DELETE FROM [ingestion].[FolderLocations]")]
    public async Task ForbiddenChanges_AreRefusedByTheDatabase(string user, string sql)
    {
        await using var db = grants.DbAs(user);

        var ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(sql));

        Assert.Contains(ex.Number, PermissionDenied);
    }

    /// <summary>SQL Server "permission denied" errors: object, column, CREATE, ALTER (missing object or permission).</summary>
    private static readonly int[] PermissionDenied = [229, 230, 262, 1088, 4902];

    private async Task RunAsWorkerAsync(Guid executionId)
    {
        await using var db = grants.WorkerDb();
        await new ExecutionEngine(db, new FakePdfPrinter(), new FakeDocxWriter(), new FakePdfCombiner(), new FakeEmailSender(), new FakePrintSpooler(), _store, new RecordingExecutionJobs(), TimeProvider.System, NullLogger<ExecutionEngine>.Instance)
            .RunAsync(executionId, CancellationToken.None);
    }
}
