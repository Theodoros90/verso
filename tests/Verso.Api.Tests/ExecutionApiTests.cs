using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verso.Application.Executions;
using Verso.Domain.Executions;

namespace Verso.Api.Tests;

/// <summary>
/// M7 acceptance: submit through the real API and database, run the engine as the Worker does (fake printer, real
/// local document store in a temporary folder), and check status, steps, documents, archive records and files.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ExecutionApiTests(MigratedDatabaseFixture fixture) : IDisposable
{
    private readonly HttpClient _client = fixture.Factory.CreateClient();
    private readonly TestDocumentStore _store = new();
    private readonly FakePdfPrinter _printer = new();
    private readonly RecordingExecutionJobs _jobs = new();
    private ExecutionTestKit Kit => new(fixture.Factory, _client);

    public void Dispose()
    {
        _store.Dispose();
        _client.Dispose();
    }

    [Theory]
    [InlineData("erp-connector", """{ "system": "oracle-netsuite", "entity": "Invoices", "mode": "on-post" }""")]
    [InlineData("file-upload", """{ "accept": "PDF, XML, CSV", "maxSize": "25mb", "who": "staff" }""")]
    [InlineData("sftp-folder", """{ "host": "sftp.example.com", "folder": "/inbound", "pattern": "*.xml", "poll": "every-5-minutes" }""")]
    [InlineData("scheduled-trigger", """{ "cron": "0 1 * * *", "tz": "Europe/Nicosia" }""")]
    public async Task SimulatedInputTriggers_RunSubmittedDataLikeTheApiTrigger(string type, string config)
    {
        var trigger = new JsonObject
        {
            ["id"] = "trigger", ["type"] = type, ["title"] = type, ["position"] = new JsonObject { ["x"] = 0, ["y"] = 80 },
            ["config"] = JsonNode.Parse(config),
        };
        var key = await Kit.PublishInvoiceWorkflowAsync(trigger: trigger);

        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);
        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        var first = done.GetProperty("steps")[0];
        Assert.Equal(type, first.GetProperty("type").GetString());
        Assert.Equal("out", first.GetProperty("outputPort").GetString());
    }

    [Fact]
    public async Task ValidFixture_Succeeds_WithOneArchivedDocumentWhoseHashMatches()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync();

        using var submit = await Kit.SubmitAsync(key, Guid.NewGuid().ToString(), ExecutionTestKit.Sample);
        var accepted = await ExecutionTestKit.ReadAsync(submit);
        Assert.Equal(HttpStatusCode.Accepted, submit.StatusCode);
        var id = accepted.GetProperty("executionId").GetGuid();
        Assert.Equal($"/api/v1/executions/{id}", submit.Headers.Location!.OriginalString);
        Assert.Equal("Queued", accepted.GetProperty("status").GetString());
        Assert.Equal(1, accepted.GetProperty("workflowVersion").GetInt32());

        var queued = await Kit.GetExecutionAsync(id);
        Assert.Equal(["Pending", "Pending", "Pending", "Pending"], Statuses(queued));
        Assert.Equal(1, await HangfireJobCountAsync(id)); // queued for the Worker right after commit

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(["Succeeded", "Succeeded", "Succeeded", "Succeeded"], Statuses(done));
        Assert.Equal(["out", "valid", "success", null], done.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("outputPort").GetString()));
        Assert.True(done.GetProperty("templateVersions").TryGetProperty("pdf", out _));

        var document = Assert.Single(done.GetProperty("documents").EnumerateArray());
        Assert.Equal("Archived", document.GetProperty("status").GetString());
        Assert.Equal("INV-2026-003901.pdf", document.GetProperty("fileName").GetString());
        var archive = document.GetProperty("archive");
        Assert.Equal("invoices/2026/11", archive.GetProperty("folderPath").GetString());
        Assert.Equal("7y", archive.GetProperty("retention").GetString());
        Assert.Equal(archive.GetProperty("archivedAt").GetDateTimeOffset().AddYears(7), archive.GetProperty("retentionUntil").GetDateTimeOffset());
        Assert.Equal("INV-2026-003901", archive.GetProperty("metadata").GetProperty("InvoiceNumber").GetString());
        Assert.Equal("Kestrel Logistics Ltd", archive.GetProperty("metadata").GetProperty("Customer").GetString());

        // The archived file exists, once, and its SHA-256 is the recorded one. The staging copy is gone.
        var file = Assert.Single(_store.Files("archive"));
        Assert.Equal(document.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(file))));
        Assert.Equal(document.GetProperty("sizeBytes").GetInt64(), new FileInfo(file).Length);
        Assert.Contains(document.GetProperty("id").GetGuid().ToString("N"), file, StringComparison.Ordinal);
        Assert.Empty(_store.Files("staging"));

        // Events and audit trail
        var events = await EventTypesAsync(id);
        Assert.Equal(
            ["Submitted", "Started", "StepStarted", "StepSucceeded", "StepStarted", "StepSucceeded", "StepStarted", "StepSucceeded", "StepStarted", "StepSucceeded", "Succeeded"],
            events);
        var audit = await AuditActionsAsync(id);
        Assert.Equal([ExecutionService.Actions.Submitted, ExecutionService.Actions.Completed], audit);
    }

    [Fact]
    public async Task BrokenFixture_IsRejected_WithEachFailedRuleListed_AndNoDocument()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync();
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Broken);

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Rejected", e.GetProperty("status").GetString());
        Assert.Equal("data.invalid", e.GetProperty("failureCode").GetString());
        Assert.Equal(["Succeeded", "Succeeded", "Skipped", "Skipped"], Statuses(e));
        var validate = e.GetProperty("steps")[1];
        Assert.Equal("invalid", validate.GetProperty("outputPort").GetString());
        var issues = validate.GetProperty("result").GetProperty("issues").EnumerateArray()
            .Select(i => (i.GetProperty("path").GetString(), i.GetProperty("rule").GetString())).ToList();
        Assert.Equal(
            [("customer.name", "required"), ("invoice.date", "type:date"), ("totals.gross", "type:number"),
             ("customer.email", "type:email"), ("customer.vatNumber", "type:eu-vat"), ("payment.iban", "type:iban")],
            issues);
        Assert.Contains("6 validation rule(s)", e.GetProperty("failureMessage").GetString(), StringComparison.Ordinal);
        Assert.Empty(e.GetProperty("documents").EnumerateArray());
        Assert.Equal(0, _printer.Calls);
        Assert.Empty(_store.Files(""));
    }

    [Fact]
    public async Task DuplicateSubmit_WithTheSameKeyAndData_ReturnsTheSameExecution()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync();
        var idempotencyKey = Guid.NewGuid().ToString();
        using var first = await Kit.SubmitAsync(key, idempotencyKey, ExecutionTestKit.Sample);
        var firstId = (await ExecutionTestKit.ReadAsync(first)).GetProperty("executionId").GetGuid();

        // Same data with properties in another order: the canonical hash is the same.
        var reordered = new JsonObject();
        foreach (var (name, value) in ExecutionTestKit.Sample.AsObject().Reverse())
        {
            reordered[name] = value?.DeepClone();
        }

        using var second = await Kit.SubmitAsync(key, idempotencyKey, reordered);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstId, (await ExecutionTestKit.ReadAsync(second)).GetProperty("executionId").GetGuid());
        Assert.Equal(1, await Kit.WithDbAsync(db => db.Executions.CountAsync(e => e.IdempotencyKey == idempotencyKey)));
        Assert.Equal(1, await HangfireJobCountAsync(firstId)); // the replay queues nothing
    }

    [Fact]
    public async Task SameKey_WithDifferentData_Is409()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync();
        var idempotencyKey = Guid.NewGuid().ToString();
        using var first = await Kit.SubmitAsync(key, idempotencyKey, ExecutionTestKit.Sample);
        var changed = ExecutionTestKit.Sample;
        changed["invoice"]!["number"] = "INV-2026-009999";

        using var second = await Kit.SubmitAsync(key, idempotencyKey, changed);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await ExecutionTestKit.ReadAsync(second);
        Assert.Equal("execution.idempotency_conflict", problem.GetProperty("code").GetString());
        Assert.Equal((await ExecutionTestKit.ReadAsync(first)).GetProperty("executionId").GetGuid(), problem.GetProperty("executionId").GetGuid());
    }

    [Fact]
    public async Task Submit_RejectsBadRequests()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync();

        using var noKey = await Kit.SubmitAsync(key, null, ExecutionTestKit.Sample);
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Equal("execution.idempotency_key_required", (await ExecutionTestKit.ReadAsync(noKey)).GetProperty("code").GetString());

        using var noData = await Kit.SubmitAsync(key, Guid.NewGuid().ToString(), null);
        Assert.Equal(HttpStatusCode.BadRequest, noData.StatusCode);

        using var notObject = await Kit.SubmitAsync(key, Guid.NewGuid().ToString(), JsonValue.Create("text"));
        Assert.Equal(HttpStatusCode.BadRequest, notObject.StatusCode);

        JsonNode deep = new JsonObject();
        for (var i = 0; i < 40; i++)
        {
            deep = new JsonObject { ["a"] = deep };
        }

        using var tooDeep = await Kit.SubmitAsync(key, Guid.NewGuid().ToString(), deep);
        Assert.Equal(HttpStatusCode.BadRequest, tooDeep.StatusCode);

        using var tooLarge = await Kit.SubmitAsync(key, Guid.NewGuid().ToString(), new JsonObject { ["blob"] = new string('x', 1_000_001) });
        Assert.True(tooLarge.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode.ToString());

        using var unknown = await Kit.SubmitAsync("no-such-workflow", Guid.NewGuid().ToString(), ExecutionTestKit.Sample);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        using var created = await _client.PostAsync(new Uri("/api/v1/workflows", UriKind.Relative),
            System.Net.Http.Json.JsonContent.Create(new { key = "unpub-" + Guid.NewGuid().ToString("N")[..8], name = "Never published" }));
        var unpublishedKey = (await ExecutionTestKit.ReadAsync(created)).GetProperty("key").GetString()!;
        using var unpublished = await Kit.SubmitAsync(unpublishedKey, Guid.NewGuid().ToString(), ExecutionTestKit.Sample);
        Assert.Equal(HttpStatusCode.Conflict, unpublished.StatusCode);
        Assert.Equal("workflow.not_published", (await ExecutionTestKit.ReadAsync(unpublished)).GetProperty("code").GetString());

        using var missing = await _client.GetAsync(new Uri($"/api/v1/executions/{Guid.NewGuid()}", UriKind.Relative));
        Assert.Equal("execution.not_found", (await ExecutionTestKit.ReadAsync(missing)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task WorkerStoppedDuringArchive_ThenRestarted_CompletesWithoutDuplicateDocuments()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync();
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);

        // The Worker shuts down right after the archive copy was written, before the step was recorded.
        using var shutdown = new CancellationTokenSource();
        _store.AfterWrite = k => { if (k.StartsWith("archive/", StringComparison.Ordinal)) { shutdown.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.RunEngineAsync(id, _printer, _store, _jobs, shutdown.Token));

        var interrupted = await Kit.GetExecutionAsync(id);
        Assert.Equal("Running", interrupted.GetProperty("status").GetString());
        Assert.Equal(["Succeeded", "Succeeded", "Succeeded", "Pending"], Statuses(interrupted));
        Assert.Equal(0, interrupted.GetProperty("steps")[3].GetProperty("attempt").GetInt32()); // a shutdown isn't a failed attempt
        Assert.Null(await Kit.WithDbAsync(db => db.Executions.Where(e => e.Id == id).Select(e => e.LockedUntil).SingleAsync()));

        // Restart: the redelivered job continues at the archive step.
        _store.AfterWrite = null;
        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Single(done.GetProperty("documents").EnumerateArray());
        Assert.Equal(1, _printer.Calls); // the PDF wasn't printed again
        Assert.Single(_store.Files("archive"));
        Assert.Equal(1, await Kit.WithDbAsync(db => db.ArchiveRecords.CountAsync(a => db.Documents.Any(d => d.Id == a.DocumentId && d.ExecutionId == id))));
    }

    [Fact]
    public async Task WorkerKilledAfterStoringThePdf_ThenAnotherRunTakesOver_WithoutDuplicateDocuments()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync();
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);

        // Stopped right after the staging PDF was written, before the document row existed…
        using var shutdown = new CancellationTokenSource();
        _store.AfterWrite = k => { if (k.StartsWith("staging/", StringComparison.Ordinal)) { shutdown.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.RunEngineAsync(id, _printer, _store, _jobs, shutdown.Token));
        _store.AfterWrite = null;

        // …and, unlike a clean stop, a killed process leaves its step Running and its lock in place.
        await Kit.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"UPDATE [executions].[ExecutionSteps] SET [Status] = 'Running', [Attempt] = 1 WHERE [ExecutionId] = {id} AND [NodeId] = 'pdf'"));
        await Kit.WithDbAsync(db => db.Executions.Where(e => e.Id == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.LockedUntil, DateTimeOffset.UtcNow.AddMinutes(5))));

        // A duplicate delivery while the lock is live does nothing.
        await Kit.RunEngineAsync(id, _printer, _store, _jobs);
        Assert.Equal("Running", (await Kit.GetExecutionAsync(id)).GetProperty("status").GetString());
        Assert.Equal(1, _printer.Calls);

        // Once the lock has expired, the next run resumes at the interrupted step.
        await Kit.WithDbAsync(db => db.Executions.Where(e => e.Id == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.LockedUntil, DateTimeOffset.UtcNow.AddSeconds(-1))));
        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(2, done.GetProperty("steps")[2].GetProperty("attempt").GetInt32());
        Assert.Single(done.GetProperty("documents").EnumerateArray());
        Assert.Equal(1, await Kit.WithDbAsync(db => db.Documents.CountAsync(d => d.ExecutionId == id)));
        Assert.Single(_store.Files("archive"));
        Assert.Empty(_store.Files("staging"));
    }

    [Fact]
    public async Task StorageFailure_FailsAfterThreeAttempts_ThenOperatorRetrySucceeds()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync();
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);
        _store.FailWrites = true;

        // Attempts 1 and 2 fail and schedule a retry (5 s, then 10 s); the run stays Running.
        for (var attempt = 1; attempt < ExecutionEngine.MaxStepAttempts; attempt++)
        {
            await Kit.RunEngineAsync(id, _printer, _store, _jobs);
            var running = await Kit.GetExecutionAsync(id);
            Assert.Equal("Running", running.GetProperty("status").GetString());
            Assert.Equal("Failed", running.GetProperty("steps")[2].GetProperty("status").GetString());
            Assert.Contains("Simulated storage outage", running.GetProperty("steps")[2].GetProperty("error").GetString(), StringComparison.Ordinal);
        }

        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)], _jobs.Scheduled.Where(s => s.Id == id).Select(s => s.Delay));

        // Attempt 3 fails for good.
        await Kit.RunEngineAsync(id, _printer, _store, _jobs);
        var failed = await Kit.GetExecutionAsync(id);
        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Equal("step.technical_error", failed.GetProperty("failureCode").GetString());
        Assert.Equal(["Succeeded", "Succeeded", "Failed", "Pending"], Statuses(failed));
        Assert.Equal(3, failed.GetProperty("steps")[2].GetProperty("attempt").GetInt32());
        Assert.Empty(failed.GetProperty("documents").EnumerateArray());

        // The operator retries once storage is back.
        _store.FailWrites = false;
        using var retry = await _client.PostAsync(new Uri($"/api/v1/executions/{id}/retry", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var queued = await ExecutionTestKit.ReadAsync(retry);
        Assert.Equal("Queued", queued.GetProperty("status").GetString());
        Assert.Equal(2, queued.GetProperty("attempt").GetInt32());
        Assert.Equal(2, await HangfireJobCountAsync(id)); // submission + retry

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, done.GetProperty("failureCode").ValueKind);
        Assert.Single(done.GetProperty("documents").EnumerateArray());
        Assert.Single(_store.Files("archive"));
        Assert.Contains(ExecutionService.Actions.RetryRequested, await AuditActionsAsync(id));
    }

    [Fact]
    public async Task Retry_IsRefusedForRejectedAndRunningExecutions()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync();
        var rejected = await Kit.SubmitNewAsync(key, ExecutionTestKit.Broken);
        await Kit.RunEngineAsync(rejected, _printer, _store, _jobs);
        var queued = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);

        using var retryRejected = await _client.PostAsync(new Uri($"/api/v1/executions/{rejected}/retry", UriKind.Relative), null);
        using var retryQueued = await _client.PostAsync(new Uri($"/api/v1/executions/{queued}/retry", UriKind.Relative), null);
        using var retryMissing = await _client.PostAsync(new Uri($"/api/v1/executions/{Guid.NewGuid()}/retry", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.Conflict, retryRejected.StatusCode);
        Assert.Equal("execution.not_retryable", (await ExecutionTestKit.ReadAsync(retryRejected)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, retryQueued.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, retryMissing.StatusCode);
    }

    [Fact]
    public async Task DataThatCantMakeACompleteDocument_IsRejected_AtTheGeneratePdfStep()
    {
        // Lenient rules: the layout binds customer.name, which this data lacks.
        var key = await Kit.PublishInvoiceWorkflowAsync(JsonNode.Parse("""{ "required": ["invoice.number"], "types": [] }"""));
        var data = ExecutionTestKit.Sample;
        data["customer"]!.AsObject().Remove("name");
        var id = await Kit.SubmitNewAsync(key, data);

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Rejected", e.GetProperty("status").GetString());
        Assert.Equal("document.data_incomplete", e.GetProperty("failureCode").GetString());
        var pdf = e.GetProperty("steps")[2];
        Assert.Equal("failed", pdf.GetProperty("outputPort").GetString());
        Assert.Contains(pdf.GetProperty("result").GetProperty("issues").EnumerateArray(), i => i.GetProperty("path").GetString() == "customer.name");
        Assert.Equal(0, _printer.Calls);
        Assert.Empty(e.GetProperty("documents").EnumerateArray());
    }

    [Fact]
    public async Task SubmissionWhoseQueueingFailed_IsAcceptedAndRequeuedByTheDispatcher()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync();

        // An API host whose job queue is down: the row is committed anyway (outbox), and nothing is queued.
        await using var broken = fixture.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddScoped<IExecutionJobs, FailingExecutionJobs>()));
        using var client = broken.CreateClient();
        var id = await new ExecutionTestKit(fixture.Factory, client).SubmitNewAsync(key, ExecutionTestKit.Sample);
        Assert.Equal(0, await HangfireJobCountAsync(id));
        Assert.Equal("Queued", (await Kit.GetExecutionAsync(id)).GetProperty("status").GetString());

        // Within the grace period the dispatcher leaves it alone (its job may simply not have run yet).
        Assert.DoesNotContain(id, await DispatchAsync());

        // After it, the dispatcher queues it again; a running execution with a live lock is never touched.
        var running = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);
        await Kit.WithDbAsync(db => db.Executions.Where(e => e.Id == id || e.Id == running)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.DispatchedAt, DateTimeOffset.UtcNow.AddMinutes(-3))));
        await Kit.WithDbAsync(db => db.Executions.Where(e => e.Id == running).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.Status, ExecutionStatus.Running).SetProperty(e => e.LockedUntil, DateTimeOffset.UtcNow.AddMinutes(5))));

        var dispatched = await DispatchAsync();

        Assert.Contains(id, dispatched);
        Assert.DoesNotContain(running, dispatched);
        Assert.Contains("Redispatched", await EventTypesAsync(id));
        Assert.DoesNotContain(id, await DispatchAsync()); // not again until the grace period has passed

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);
        Assert.Equal("Succeeded", (await Kit.GetExecutionAsync(id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Engine_IgnoresUnknownAndFinishedExecutions()
    {
        await Kit.RunEngineAsync(Guid.NewGuid(), _printer, _store, _jobs); // a job without a row does nothing

        var key = await Kit.PublishInvoiceWorkflowAsync();
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);
        await Kit.RunEngineAsync(id, _printer, _store, _jobs);
        var events = (await EventTypesAsync(id)).Count;

        await Kit.RunEngineAsync(id, _printer, _store, _jobs); // duplicate delivery after completion

        Assert.Equal(events, (await EventTypesAsync(id)).Count);
        Assert.Equal(1, _printer.Calls);
    }

    // ----- Helpers -----

    private static List<string?> Statuses(JsonElement execution) =>
        [.. execution.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("status").GetString())];

    private async Task<List<Guid>> DispatchAsync()
    {
        var jobs = new RecordingExecutionJobs();
        await Kit.WithDbAsync(db => new ExecutionDispatcher(db, jobs, TimeProvider.System).RunOnceAsync(CancellationToken.None));
        return [.. jobs.Enqueued];
    }

    private Task<List<string>> EventTypesAsync(Guid id) =>
        Kit.WithDbAsync(db => db.ExecutionEvents.Where(e => e.ExecutionId == id).OrderBy(e => e.Id).Select(e => e.Type).ToListAsync());

    private Task<List<string>> AuditActionsAsync(Guid id) =>
        Kit.WithDbAsync(db => db.AuditEntries.Where(a => a.EntityType == ExecutionService.EntityType && a.EntityId == id.ToString())
            .OrderBy(a => a.Sequence).Select(a => a.Action).ToListAsync());

    private Task<int> HangfireJobCountAsync(Guid id) =>
        Kit.WithDbAsync(db => db.Database.SqlQuery<int>(
            $"SELECT COUNT(*) AS [Value] FROM [hangfire].[Job] WHERE [InvocationData] LIKE '%ExecutionJob%' AND [Arguments] LIKE {"%" + id + "%"}").SingleAsync());

    private sealed class FailingExecutionJobs : IExecutionJobs
    {
        public void Enqueue(Guid executionId) => throw new InvalidOperationException("Simulated job storage outage.");

        public void Schedule(Guid executionId, TimeSpan delay) => throw new InvalidOperationException("Simulated job storage outage.");
    }
}
