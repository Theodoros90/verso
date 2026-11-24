using System.Net;
using System.Net.Http.Json;
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
/// The upload service (ADR 0023) on LocalDB with the real <see cref="ExecutionService"/>: checks, staging, the run's reference payload,
/// duplicates and the audit. Synthetic files only.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class UploadServiceTests : IDisposable
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n% synthetic statement for tests\n%%EOF\n");

    private readonly MigratedDatabaseFixture _fixture;
    private readonly HttpClient _client;
    private readonly ExecutionTestKit _kit;
    private readonly RecordingExecutionJobs _jobs = new();

    public UploadServiceTests(MigratedDatabaseFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Factory.CreateClient();
        _kit = new ExecutionTestKit(fixture.Factory, _client);
    }

    public void Dispose() => _client.Dispose();

    private sealed class Operator : ICurrentUser
    {
        public string Id => "synthetic.operator";
        public string Name => "Synthetic Operator";
        public IReadOnlyCollection<string> Roles => [Verso.Application.Roles.Operator];
    }

    private static JsonObject UploadTrigger(string accept = "PDF, XML, CSV", string maxSize = "1mb") => new()
    {
        ["id"] = "trigger", ["type"] = "file-upload", ["title"] = "File upload", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 80 },
        ["config"] = new JsonObject { ["accept"] = accept, ["maxSize"] = maxSize, ["who"] = "staff" },
    };

    /// <summary>A published workflow that starts with File upload (then the standard validate → pdf → archive steps).</summary>
    private Task<string> PublishAsync(string accept = "PDF, XML, CSV", string maxSize = "1mb") =>
        _kit.PublishInvoiceWorkflowAsync(trigger: UploadTrigger(accept, maxSize));

    private UploadService Service(VersoDbContext db, long ceiling = UploadRules.DefaultCeilingBytes)
    {
        var user = new Operator();
        return new UploadService(db, new ExecutionService(db, _jobs, user, TimeProvider.System, NullLogger<ExecutionService>.Instance),
            user, TimeProvider.System, Options.Create(new UploadOptions { CeilingBytes = ceiling }));
    }

    private Task<Result<Submission>> AcceptAsync(string key, string fileName, byte[] content, string? correlationId = null, long ceiling = UploadRules.DefaultCeilingBytes) =>
        WithDb(db => Service(db, ceiling).AcceptAsync(key, fileName, content, correlationId, CancellationToken.None));

    private async Task<T> WithDb<T>(Func<VersoDbContext, Task<T>> action)
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<VersoDbContext>());
    }

    private Task<Guid> WorkflowIdAsync(string key) => WithDb(db => db.Workflows.Where(w => w.Key == key).Select(w => w.Id).SingleAsync());

    private async Task<List<Upload>> RowsAsync(string key)
    {
        var id = await WorkflowIdAsync(key);
        return await WithDb(db => db.Uploads.AsNoTracking().Where(u => u.WorkflowId == id).ToListAsync());
    }

    private async Task<JsonElement> PayloadAsync(Guid executionId)
    {
        var json = await WithDb(db => db.Executions.Where(e => e.Id == executionId).Select(e => e.InputPayload).SingleAsync());
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private Task<List<Verso.Domain.Audit.AuditEntry>> AuditAsync(string action, string workflowKey) =>
        WithDb(db => db.AuditEntries.AsNoTracking().Where(a => a.Action == action && a.Details!.Contains(workflowKey)).ToListAsync());

    [Fact]
    public async Task Accepts_a_valid_pdf_and_starts_a_run_with_a_reference_payload()
    {
        var key = await PublishAsync();

        var result = await AcceptAsync(key, "statement.pdf", Pdf);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(result.Value!.Created);
        Assert.Equal("Queued", result.Value.Execution.Status);
        var executionId = result.Value.Execution.ExecutionId;
        Assert.Contains(executionId, _jobs.Enqueued);

        var row = Assert.Single(await RowsAsync(key));
        Assert.Equal(UploadStatus.Staged, row.Status);
        Assert.Equal(executionId, row.ExecutionId);
        Assert.Equal(Pdf, row.Content);
        Assert.Equal("statement.pdf", row.FileName);
        Assert.Equal(".pdf", row.Extension);
        Assert.Equal("application/pdf", row.ContentType);
        Assert.Equal("synthetic.operator", row.UploadedBy);
        Assert.Equal(DocumentKeys.Sha256Hex(Pdf), row.Sha256);

        var payload = await PayloadAsync(executionId);
        var upload = payload.GetProperty("upload");
        Assert.Equal(row.Id, upload.GetProperty("id").GetGuid());
        Assert.Equal("statement.pdf", upload.GetProperty("fileName").GetString());
        Assert.Equal("application/pdf", upload.GetProperty("contentType").GetString());
        Assert.Equal(Pdf.Length, upload.GetProperty("size").GetInt64());
        Assert.Equal(row.Sha256, upload.GetProperty("sha256").GetString());
        Assert.False(payload.TryGetProperty("content", out _)); // a PDF travels by reference only
        Assert.DoesNotContain("synthetic statement", payload.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Pdf), payload.GetRawText(), StringComparison.Ordinal);

        var idempotencyKey = await WithDb(db => db.Executions.Where(e => e.Id == executionId).Select(e => e.IdempotencyKey).SingleAsync());
        Assert.Equal("upload:" + row.Id.ToString("N"), idempotencyKey);
    }

    [Fact]
    public async Task Xml_payload_carries_content_text()
    {
        var key = await PublishAsync();
        const string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><holders><holder name=\"Synthetic Holder A\" /></holders>";

        var result = await AcceptAsync(key, "holders.xml", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(xml + "\r\n")]);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var payload = await PayloadAsync(result.Value!.Execution.ExecutionId);
        Assert.Equal(xml, payload.GetProperty("content").GetString()); // no byte-order mark, no trailing blank space, like Folder input
        Assert.Equal("application/xml", payload.GetProperty("upload").GetProperty("contentType").GetString());
        Assert.Equal("holders.xml", payload.GetProperty("upload").GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task Large_csv_over_payload_limit_carries_reference_only()
    {
        var key = await PublishAsync(maxSize: "10mb");
        var csv = new StringBuilder("id,name\n");
        for (var i = 0; csv.Length <= ExecutionService.MaxDataChars; i++)
        {
            csv.Append(i).Append(",Synthetic customer ").Append(i).Append('\n');
        }

        var bytes = Encoding.UTF8.GetBytes(csv.ToString());
        var result = await AcceptAsync(key, "customers.csv", bytes);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var payload = await PayloadAsync(result.Value!.Execution.ExecutionId);
        Assert.False(payload.TryGetProperty("content", out _));
        Assert.Equal(bytes.Length, payload.GetProperty("upload").GetProperty("size").GetInt64());
        Assert.Equal("text/csv", payload.GetProperty("upload").GetProperty("contentType").GetString());
        Assert.Equal(bytes, Assert.Single(await RowsAsync(key)).Content);
    }

    [Fact]
    public async Task Same_file_twice_returns_first_run_not_created()
    {
        var key = await PublishAsync();

        var first = await AcceptAsync(key, "statement.pdf", Pdf);
        var second = await AcceptAsync(key, "statement-again.pdf", Pdf);

        Assert.True(first.Value!.Created);
        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.False(second.Value!.Created);
        Assert.Equal(first.Value.Execution.ExecutionId, second.Value.Execution.ExecutionId);
        Assert.Equal("statement.pdf", Assert.Single(await RowsAsync(key)).FileName);
        var workflowId = await WorkflowIdAsync(key);
        Assert.Equal(1, await WithDb(db => db.Executions.CountAsync(e => e.WorkflowId == workflowId)));
        Assert.Single(await AuditAsync(UploadService.Actions.FileUploaded, key));
    }

    [Fact]
    public async Task Expired_duplicate_can_be_uploaded_again_as_a_new_run()
    {
        var key = await PublishAsync();
        var first = await AcceptAsync(key, "statement.pdf", Pdf);
        await WithDb(async db =>
        {
            var row = await db.Uploads.SingleAsync(u => u.ExecutionId == first.Value!.Execution.ExecutionId);
            row.Expire(); // the retention pass found it unused
            return await db.SaveChangesAsync();
        });

        var again = await AcceptAsync(key, "statement.pdf", Pdf);

        Assert.True(again.IsSuccess, again.Error?.Message);
        Assert.True(again.Value!.Created);
        Assert.NotEqual(first.Value!.Execution.ExecutionId, again.Value.Execution.ExecutionId);
        var row = Assert.Single(await RowsAsync(key)); // the expired row made way for the new one
        Assert.Equal(UploadStatus.Staged, row.Status);
        Assert.Equal(again.Value.Execution.ExecutionId, row.ExecutionId);
        Assert.Equal(Pdf, row.Content);
    }

    [Fact]
    public async Task Taken_duplicate_still_returns_the_first_run()
    {
        var key = await PublishAsync();
        var first = await AcceptAsync(key, "statement.pdf", Pdf);
        await WithDb(async db =>
        {
            (await db.Uploads.SingleAsync(u => u.ExecutionId == first.Value!.Execution.ExecutionId)).MarkTaken();
            return await db.SaveChangesAsync();
        });

        var again = await AcceptAsync(key, "statement.pdf", Pdf);

        Assert.False(again.Value!.Created);
        Assert.Equal(first.Value!.Execution.ExecutionId, again.Value.Execution.ExecutionId);
    }

    [Fact]
    public async Task Two_concurrent_identical_uploads_start_one_run()
    {
        var key = await PublishAsync();
        var content = Encoding.ASCII.GetBytes("%PDF-1.7\n% synthetic concurrent upload " + Guid.NewGuid() + "\n%%EOF\n");

        var results = await Task.WhenAll(
            Task.Run(() => AcceptAsync(key, "a.pdf", content)),
            Task.Run(() => AcceptAsync(key, "b.pdf", content)));

        Assert.All(results, r => Assert.True(r.IsSuccess, r.Error?.Message));
        Assert.Single(results, r => r.Value!.Created);
        Assert.Equal(results[0].Value!.Execution.ExecutionId, results[1].Value!.Execution.ExecutionId);
        Assert.Single(await RowsAsync(key));
        var workflowId = await WorkflowIdAsync(key);
        Assert.Equal(1, await WithDb(db => db.Executions.CountAsync(e => e.WorkflowId == workflowId)));
    }

    [Fact]
    public async Task Rejects_pdf_renamed_csv_and_audits_rule_only()
    {
        var key = await PublishAsync(accept: "PDF, CSV");

        var result = await AcceptAsync(key, "customers-secret-name.csv", Pdf);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
        Assert.Equal("upload.content_mismatch", result.Error.Code);
        Assert.Empty(await RowsAsync(key));
        var audit = Assert.Single(await AuditAsync(UploadService.Actions.FileUploadRejected, key));
        Assert.Equal("synthetic.operator", audit.ActorId);
        var details = JsonSerializer.Deserialize<JsonElement>(audit.Details!);
        Assert.Equal("upload.content_mismatch", details.GetProperty("rule").GetString());
        Assert.Equal(key, details.GetProperty("workflowKey").GetString());
        Assert.DoesNotContain("secret-name", audit.Details!, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic statement", audit.Details!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("report.exe", "upload.type_not_allowed")]
    [InlineData("empty.pdf", "upload.empty")]
    public async Task Rejects_other_rules_with_their_code(string fileName, string code)
    {
        var key = await PublishAsync();

        var result = await AcceptAsync(key, fileName, code == "upload.empty" ? [] : Pdf);

        Assert.Equal(code, result.Error!.Code);
        Assert.Empty(await RowsAsync(key));
        Assert.Single(await AuditAsync(UploadService.Actions.FileUploadRejected, key));
    }

    [Fact]
    public async Task Rejects_over_limit_by_one_byte()
    {
        var key = await PublishAsync(maxSize: "1mb");
        byte[] Sized(int size, byte fill)
        {
            var bytes = new byte[size];
            Array.Fill(bytes, fill);
            "%PDF-1.7\n"u8.CopyTo(bytes);
            return bytes;
        }

        var over = await AcceptAsync(key, "big.pdf", Sized(1_048_577, (byte)'a'));
        Assert.Equal("upload.too_large", over.Error!.Code);
        Assert.Equal(ErrorKind.Validation, over.Error.Kind);
        Assert.Empty(await RowsAsync(key));
        var rule = JsonSerializer.Deserialize<JsonElement>(Assert.Single(await AuditAsync(UploadService.Actions.FileUploadRejected, key)).Details!);
        Assert.Equal("upload.too_large", rule.GetProperty("rule").GetString());

        var exact = await AcceptAsync(key, "big.pdf", Sized(1_048_576, (byte)'b'));
        Assert.True(exact.IsSuccess, exact.Error?.Message);

        // The server's ceiling wins over the step's setting.
        var clamped = await AcceptAsync(key, "big.pdf", Sized(1001, (byte)'c'), ceiling: 1000);
        Assert.Equal("upload.too_large", clamped.Error!.Code);
    }

    [Fact]
    public async Task Inactive_unpublished_wrong_start_step_non_staff_are_not_available_and_leave_no_row()
    {
        // inactive
        var inactive = await PublishAsync();
        await WithDb(async db =>
        {
            (await db.Workflows.SingleAsync(w => w.Key == inactive)).Deactivate();
            return await db.SaveChangesAsync();
        });

        // never published
        var unpublished = "up-" + Guid.NewGuid().ToString("N")[..12];
        using (var create = await _client.PostAsJsonAsync(new Uri("/api/v1/workflows", UriKind.Relative), new { key = unpublished, name = "Draft only" }))
        {
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        }

        // starts with an API trigger
        var wrongStart = await _kit.PublishInvoiceWorkflowAsync();

        // a version published before "Who can upload" was limited to staff
        var portal = await PublishAsync();
        var portalId = await WorkflowIdAsync(portal);
        await WithDb(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [workflows].[WorkflowVersions] SET [Definition] = REPLACE([Definition], N'\"staff\"', N'\"portal-customers\"') WHERE [WorkflowId] = {portalId}"));

        foreach (var key in new[] { inactive, unpublished, wrongStart, portal })
        {
            var result = await AcceptAsync(key, "statement.pdf", Pdf);
            Assert.False(result.IsSuccess, key);
            Assert.Equal(ErrorKind.Conflict, result.Error!.Kind);
            Assert.Equal("upload.not_available", result.Error.Code);
            Assert.Empty(await RowsAsync(key));

            var settings = await WithDb(db => Service(db).SettingsAsync(key, CancellationToken.None));
            Assert.Equal("upload.not_available", settings.Error!.Code);
        }

        var missing = await AcceptAsync("no-such-workflow-" + Guid.NewGuid().ToString("N")[..8], "statement.pdf", Pdf);
        Assert.Equal(ErrorKind.NotFound, missing.Error!.Kind);
        Assert.Equal("workflow.not_found", missing.Error.Code);
    }

    [Fact]
    public async Task Submission_failure_removes_the_staged_row()
    {
        var key = await PublishAsync();

        // The correlation id is checked by the submission itself, after the upload was staged.
        var failed = await AcceptAsync(key, "statement.pdf", Pdf, correlationId: "not a valid correlation id");

        Assert.False(failed.IsSuccess);
        Assert.Equal("validation", failed.Error!.Code);
        Assert.Empty(await RowsAsync(key));
        Assert.Empty(await AuditAsync(UploadService.Actions.FileUploaded, key));

        // Nothing is left behind that would make the same file a duplicate.
        var retried = await AcceptAsync(key, "statement.pdf", Pdf, correlationId: "synthetic-correlation-1");
        Assert.True(retried.IsSuccess, retried.Error?.Message);
        Assert.True(retried.Value!.Created);
        Assert.Equal("synthetic-correlation-1", retried.Value.Execution.CorrelationId);
    }

    [Fact]
    public async Task Audit_never_contains_content_or_unsanitised_name()
    {
        var key = await PublishAsync();
        var content = Encoding.ASCII.GetBytes("%PDF-1.7\n% SYNTHETIC-MARKER-7731\n%%EOF\n");

        var result = await AcceptAsync(key, "..\\..\\secret\u0001folder/state<ment>.pdf", content);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var row = Assert.Single(await RowsAsync(key));
        Assert.DoesNotContain("..", row.FileName, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", row.FileName, StringComparison.Ordinal);
        Assert.DoesNotContain("/", row.FileName, StringComparison.Ordinal);
        Assert.DoesNotContain("\u0001", row.FileName, StringComparison.Ordinal);
        Assert.EndsWith(".pdf", row.FileName, StringComparison.Ordinal);

        var audit = Assert.Single(await AuditAsync(UploadService.Actions.FileUploaded, key));
        Assert.Equal("Upload", audit.EntityType);
        Assert.Equal(row.Id.ToString(), audit.EntityId);
        var details = JsonSerializer.Deserialize<JsonElement>(audit.Details!);
        Assert.Equal(row.Id, details.GetProperty("uploadId").GetGuid());
        Assert.Equal(key, details.GetProperty("workflowKey").GetString());
        Assert.Equal(row.FileName, details.GetProperty("fileName").GetString());
        Assert.Equal(content.Length, details.GetProperty("size").GetInt64());
        Assert.Equal(row.Sha256, details.GetProperty("sha256").GetString());
        Assert.DoesNotContain("SYNTHETIC-MARKER", audit.Details!, StringComparison.Ordinal);
        Assert.DoesNotContain("..", audit.Details!, StringComparison.Ordinal);
        Assert.DoesNotContain("secret\\u0001", audit.Details!, StringComparison.Ordinal);

        var payload = await PayloadAsync(result.Value!.Execution.ExecutionId);
        Assert.Equal(row.FileName, payload.GetProperty("upload").GetProperty("fileName").GetString());
        Assert.DoesNotContain("SYNTHETIC-MARKER", payload.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("photo.jpeg", "photo.jpg")]
    [InlineData("PHOTO.JPG", "PHOTO.jpg")]
    [InlineData("scan.Jpeg", "scan.jpg")]
    public async Task Jpeg_name_is_stored_with_the_canonical_extension_once(string fileName, string stored)
    {
        var key = await PublishAsync(accept: "JPG");
        byte[] jpg = [0xFF, 0xD8, 0xFF, 0xE0, .. Encoding.ASCII.GetBytes("synthetic jpg " + Guid.NewGuid())];

        var result = await AcceptAsync(key, fileName, jpg);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var row = Assert.Single(await RowsAsync(key));
        Assert.Equal(stored, row.FileName);
        Assert.Equal(".jpg", row.Extension);
        Assert.Equal("image/jpeg", row.ContentType);
    }

    [Fact]
    public async Task Settings_returns_accept_list_and_clamped_max()
    {
        var key = await PublishAsync(accept: "pdf, XML, JPG", maxSize: "25mb");

        var settings = await WithDb(db => Service(db, ceiling: 10_000_000).SettingsAsync(key, CancellationToken.None));

        Assert.True(settings.IsSuccess, settings.Error?.Message);
        Assert.Equal([".pdf", ".xml", ".jpg", ".jpeg"], settings.Value!.Accept);
        Assert.Equal(10_000_000, settings.Value.MaxBytes);

        var plain = await WithDb(db => Service(db).SettingsAsync(key, CancellationToken.None));
        Assert.Equal(26_214_400, plain.Value!.MaxBytes);
    }
}
