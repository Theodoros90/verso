using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
/// The File upload step in the Worker (ADR 0023): the real engine copies the staged bytes into the document store under the run's
/// staging key and clears the database copy. Uploads are made with the real <see cref="UploadService"/>. Synthetic files only.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class FileUploadStepTests : IDisposable
{
    private readonly MigratedDatabaseFixture _fixture;
    private readonly HttpClient _client;
    private readonly ExecutionTestKit _kit;
    private readonly TestDocumentStore _store = new();
    private readonly FakePdfPrinter _printer = new();
    private readonly RecordingExecutionJobs _jobs = new();
    private readonly List<string> _writes = [];

    public FileUploadStepTests(MigratedDatabaseFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Factory.CreateClient();
        _kit = new ExecutionTestKit(fixture.Factory, _client);
        _store.AfterWrite = key => _writes.Add(key);
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

    private static byte[] SyntheticPdf() => Encoding.ASCII.GetBytes($"%PDF-1.7\n% synthetic statement {Guid.NewGuid():N}\n%%EOF\n");

    private static JsonObject UploadTrigger(string accept = "PDF, XML, CSV") => new()
    {
        ["id"] = "trigger", ["type"] = "file-upload", ["title"] = "File upload", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 80 },
        ["config"] = new JsonObject { ["accept"] = accept, ["maxSize"] = "1mb", ["who"] = "staff" },
    };

    private Task<string> PublishAsync(JsonNode? parseConfig = null, JsonNode? rules = null) =>
        _kit.PublishInvoiceWorkflowAsync(rules, trigger: UploadTrigger(), parseConfig: parseConfig);

    private async Task<T> WithDb<T>(Func<VersoDbContext, Task<T>> action)
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<VersoDbContext>());
    }

    /// <summary>Uploads through the real service; returns the run and the staged row's id.</summary>
    private async Task<(Guid ExecutionId, Guid UploadId)> UploadAsync(string key, string fileName, byte[] content)
    {
        var result = await WithDb(db =>
        {
            var user = new Operator();
            var service = new UploadService(db, new ExecutionService(db, _jobs, user, TimeProvider.System, NullLogger<ExecutionService>.Instance),
                user, TimeProvider.System, Options.Create(new UploadOptions()), NullLogger<UploadService>.Instance);
            return service.AcceptAsync(key, fileName, content, null, CancellationToken.None);
        });
        Assert.True(result.IsSuccess, result.Error?.Message);
        var executionId = result.Value!.Execution.ExecutionId;
        var uploadId = await WithDb(db => db.Uploads.Where(u => u.ExecutionId == executionId).Select(u => u.Id).SingleAsync());
        return (executionId, uploadId);
    }

    private Task<Upload> RowAsync(Guid uploadId) => WithDb(db => db.Uploads.AsNoTracking().SingleAsync(u => u.Id == uploadId));

    private Task RunAsync(Guid executionId) => _kit.RunEngineAsync(executionId, _printer, _store, _jobs);

    private static JsonElement TriggerStep(JsonElement execution) =>
        execution.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("nodeId").GetString() == "trigger");

    [Fact]
    public async Task Step_copies_bytes_to_staging_key_and_clears_the_row()
    {
        var key = await PublishAsync();
        var pdf = SyntheticPdf();
        var (executionId, uploadId) = await UploadAsync(key, "statement.pdf", pdf);

        await RunAsync(executionId);

        var stagingKey = DocumentKeys.Staging(executionId, "trigger", 0, ".pdf");
        Assert.Equal(pdf, await _store.ReadAsync(stagingKey, CancellationToken.None));
        Assert.Equal([stagingKey], _writes.Where(w => w.StartsWith("staging/", StringComparison.Ordinal) && w.Contains("/trigger", StringComparison.Ordinal)));
        var row = await RowAsync(uploadId);
        Assert.Equal(UploadStatus.Taken, row.Status);
        Assert.Null(row.Content);
        Assert.Equal(executionId, row.ExecutionId);
        var step = TriggerStep(await _kit.GetExecutionAsync(executionId));
        Assert.Equal("Succeeded", step.GetProperty("status").GetString());
        Assert.Equal("out", step.GetProperty("outputPort").GetString());
    }

    [Fact]
    public async Task Step_result_has_name_size_hash_and_no_bytes_or_key()
    {
        var key = await PublishAsync();
        var pdf = SyntheticPdf();
        var (executionId, uploadId) = await UploadAsync(key, "statement.pdf", pdf);

        await RunAsync(executionId);

        var result = TriggerStep(await _kit.GetExecutionAsync(executionId)).GetProperty("result");
        Assert.Equal(["fileName", "contentType", "size", "sha256"], result.EnumerateObject().Select(p => p.Name));
        Assert.Equal("statement.pdf", result.GetProperty("fileName").GetString());
        Assert.Equal("application/pdf", result.GetProperty("contentType").GetString());
        Assert.Equal(pdf.Length, result.GetProperty("size").GetInt64());
        Assert.Equal(DocumentKeys.Sha256Hex(pdf), result.GetProperty("sha256").GetString());
        var raw = result.GetRawText();
        Assert.DoesNotContain("staging", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(executionId.ToString("N"), raw, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(pdf), raw, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic statement", raw, StringComparison.Ordinal);
        Assert.Equal(UploadStatus.Taken, (await RowAsync(uploadId)).Status);
    }

    [Fact]
    public async Task Crash_after_copy_before_clear_succeeds_on_rerun_without_second_copy()
    {
        // The object is in the store but the row was never cleared (the Worker stopped between the two): no second write.
        var key = await PublishAsync();
        var pdf = SyntheticPdf();
        var (executionId, uploadId) = await UploadAsync(key, "statement.pdf", pdf);
        var stagingKey = DocumentKeys.Staging(executionId, "trigger", 0, ".pdf");
        await _store.WriteAsync(stagingKey, pdf, CancellationToken.None);

        await RunAsync(executionId);

        Assert.Single(_writes, stagingKey); // the pre-written copy only
        Assert.Equal(pdf, await _store.ReadAsync(stagingKey, CancellationToken.None));
        var row = await RowAsync(uploadId);
        Assert.Equal(UploadStatus.Taken, row.Status);
        Assert.Null(row.Content);
        Assert.Equal("Succeeded", TriggerStep(await _kit.GetExecutionAsync(executionId)).GetProperty("status").GetString());

        // The object is there and the row was already cleared (the step's own save was lost): still a success, no write.
        var key2 = await PublishAsync();
        var pdf2 = SyntheticPdf();
        var (executionId2, uploadId2) = await UploadAsync(key2, "statement.pdf", pdf2);
        var stagingKey2 = DocumentKeys.Staging(executionId2, "trigger", 0, ".pdf");
        await _store.WriteAsync(stagingKey2, pdf2, CancellationToken.None);
        await WithDb(async db =>
        {
            (await db.Uploads.SingleAsync(u => u.Id == uploadId2)).MarkTaken();
            return await db.SaveChangesAsync();
        });

        await RunAsync(executionId2);

        Assert.Single(_writes, stagingKey2);
        Assert.Equal("Succeeded", TriggerStep(await _kit.GetExecutionAsync(executionId2)).GetProperty("status").GetString());
        Assert.Equal(UploadStatus.Taken, (await RowAsync(uploadId2)).Status);
    }

    [Fact]
    public async Task Lost_link_is_repaired_when_the_step_takes_the_file()
    {
        var key = await PublishAsync();
        var (executionId, uploadId) = await UploadAsync(key, "statement.pdf", SyntheticPdf());
        await WithDb(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [ingestion].[Uploads] SET [ExecutionId] = NULL WHERE [Id] = {uploadId}"));

        await RunAsync(executionId);

        var row = await RowAsync(uploadId);
        Assert.Equal(executionId, row.ExecutionId);
        Assert.Equal(UploadStatus.Taken, row.Status);
    }

    [Fact]
    public async Task Expired_upload_fails_run_with_upload_expired_not_retried()
    {
        var key = await PublishAsync();
        var (executionId, uploadId) = await UploadAsync(key, "statement.pdf", SyntheticPdf());
        await WithDb(async db =>
        {
            (await db.Uploads.SingleAsync(u => u.Id == uploadId)).Expire(); // the retention pass found it unused
            return await db.SaveChangesAsync();
        });

        await RunAsync(executionId);

        var e = await _kit.GetExecutionAsync(executionId);
        Assert.Equal("Failed", e.GetProperty("status").GetString());
        Assert.Equal("upload.expired", e.GetProperty("failureCode").GetString());
        Assert.Empty(_jobs.Scheduled);
        Assert.Equal(1, TriggerStep(e).GetProperty("attempt").GetInt32());
        Assert.Empty(_writes);
        Assert.Equal(UploadStatus.Expired, (await RowAsync(uploadId)).Status);
    }

    [Fact]
    public async Task Taken_upload_without_a_stored_copy_fails_with_upload_expired()
    {
        var key = await PublishAsync();
        var (executionId, uploadId) = await UploadAsync(key, "statement.pdf", SyntheticPdf());
        await WithDb(async db =>
        {
            (await db.Uploads.SingleAsync(u => u.Id == uploadId)).MarkTaken(); // bytes dropped, nothing in the store
            return await db.SaveChangesAsync();
        });

        await RunAsync(executionId);

        var e = await _kit.GetExecutionAsync(executionId);
        Assert.Equal("Failed", e.GetProperty("status").GetString());
        Assert.Equal("upload.expired", e.GetProperty("failureCode").GetString());
        Assert.Empty(_jobs.Scheduled);
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task Missing_row_fails_with_upload_missing()
    {
        // A run submitted as JSON with a reference to an upload that does not exist, or an id that is not a Guid.
        var key = await PublishAsync();
        foreach (var id in new JsonNode[] { Guid.NewGuid().ToString(), "../../etc/passwd", 42 })
        {
            var executionId = await _kit.SubmitNewAsync(key, new JsonObject
            {
                ["upload"] = new JsonObject { ["id"] = id.DeepClone(), ["fileName"] = "statement.pdf", ["contentType"] = "application/pdf", ["size"] = 10, ["sha256"] = new string('a', 64) },
            });

            await RunAsync(executionId);

            var e = await _kit.GetExecutionAsync(executionId);
            Assert.Equal("Failed", e.GetProperty("status").GetString());
            Assert.Equal("upload.missing", e.GetProperty("failureCode").GetString());
            Assert.Equal(1, TriggerStep(e).GetProperty("attempt").GetInt32());
        }

        Assert.Empty(_jobs.Scheduled);
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task Upload_of_another_workflow_or_run_fails_with_upload_missing_and_is_left_alone()
    {
        var owner = await PublishAsync();
        var pdf = SyntheticPdf();
        var (_, uploadId) = await UploadAsync(owner, "statement.pdf", pdf);
        var reference = new JsonObject
        {
            ["upload"] = new JsonObject { ["id"] = uploadId.ToString(), ["fileName"] = "statement.pdf", ["contentType"] = "application/pdf", ["size"] = pdf.Length, ["sha256"] = DocumentKeys.Sha256Hex(pdf) },
        };

        // Another workflow's run points at it.
        var other = await PublishAsync();
        var foreign = await _kit.SubmitNewAsync(other, reference);
        await RunAsync(foreign);
        Assert.Equal("upload.missing", (await _kit.GetExecutionAsync(foreign)).GetProperty("failureCode").GetString());

        // A second run of the same workflow, submitted as JSON, points at it: the file belongs to the upload's own run only.
        var copycat = await _kit.SubmitNewAsync(owner, reference);
        await RunAsync(copycat);
        Assert.Equal("upload.missing", (await _kit.GetExecutionAsync(copycat)).GetProperty("failureCode").GetString());

        var row = await RowAsync(uploadId);
        Assert.Equal(UploadStatus.Staged, row.Status);
        Assert.Equal(pdf, row.Content);
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task Run_without_upload_reference_still_passes_through()
    {
        // A run submitted through POST …/executions, as before File upload was real: the step passes the data on.
        var key = await PublishAsync();
        var executionId = await _kit.SubmitNewAsync(key, ExecutionTestKit.Sample);

        await RunAsync(executionId);

        var e = await _kit.GetExecutionAsync(executionId);
        Assert.Equal("Succeeded", e.GetProperty("status").GetString());
        var step = TriggerStep(e);
        Assert.Equal("out", step.GetProperty("outputPort").GetString());
        Assert.True(!step.TryGetProperty("result", out var result) || result.ValueKind == JsonValueKind.Null);
        Assert.DoesNotContain(_writes, w => w.Contains("/trigger", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Parse_step_reads_content_text_from_xml_upload()
    {
        // The synthetic account-holders file of docs/samples/account-holders.md, uploaded instead of dropped in a folder.
        var rules = JsonNode.Parse("""{ "required": ["AccountHolder"], "types": [] }""");
        var key = await PublishAsync(JsonNode.Parse("""{ "source": "content", "format": "xml", "encoding": "utf-8", "recordPath": "AccountHolders" }"""), rules);
        var xml = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "account-holders", "Account.xml"));
        var (executionId, uploadId) = await UploadAsync(key, "Account.xml", xml);

        await RunAsync(executionId);

        var e = await _kit.GetExecutionAsync(executionId);
        var steps = e.GetProperty("steps");
        Assert.Equal("Succeeded", TriggerStep(e).GetProperty("status").GetString());
        var parse = steps.EnumerateArray().Single(s => s.GetProperty("nodeId").GetString() == "parse");
        Assert.Equal("success", parse.GetProperty("outputPort").GetString());
        Assert.Equal("xml", parse.GetProperty("result").GetProperty("format").GetString());
        var validate = steps.EnumerateArray().Single(s => s.GetProperty("nodeId").GetString() == "validate");
        Assert.Equal("valid", validate.GetProperty("outputPort").GetString()); // the parsed holders reached Validate
        Assert.Equal(xml, await _store.ReadAsync(DocumentKeys.Staging(executionId, "trigger", 0, ".xml"), CancellationToken.None));
        Assert.Equal(UploadStatus.Taken, (await RowAsync(uploadId)).Status);
    }
}
