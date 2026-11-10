using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Verso.Api.Realtime;
using Verso.Application.Documents;
using Verso.Application.Executions;
using Verso.Domain.Executions;

namespace Verso.Api.Tests;

/// <summary>
/// M8 read side through the real API and database: history list and filters, event catch-up, the restricted
/// payload view, archive search and verified downloads, and the live-progress relay.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ExecutionHistoryApiTests(MigratedDatabaseFixture fixture) : IDisposable
{
    private readonly HttpClient _client = fixture.Factory.CreateClient();
    private readonly TestDocumentStore _store = new();
    private readonly FakePdfPrinter _printer = new();
    private readonly RecordingExecutionJobs _jobs = new();
    private WebApplicationFactory<Program>? _storeFactory;
    private ExecutionTestKit Kit => new(fixture.Factory, _client);

    public void Dispose()
    {
        _storeFactory?.Dispose();
        _store.Dispose();
        _client.Dispose();
    }

    // ----- History list -----

    [Fact]
    public async Task List_FiltersByWorkflowStatusCorrelationAndDate_NewestFirst()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync();
        var other = await Kit.PublishInvoiceWorkflowAsync();
        var ok = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);
        var rejected = await Kit.SubmitNewAsync(key, ExecutionTestKit.Broken);
        var queued = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);
        await Kit.SubmitNewAsync(other, ExecutionTestKit.Sample);
        await Kit.RunEngineAsync(ok, _printer, _store, _jobs);
        await Kit.RunEngineAsync(rejected, _printer, _store, _jobs);

        var all = await ListAsync($"workflow={key}");
        Assert.Equal([queued, rejected, ok], Ids(all)); // newest first
        Assert.Equal(3, all.GetProperty("total").GetInt32());

        Assert.Equal([rejected], Ids(await ListAsync($"workflow={key}&status=Rejected")));
        Assert.Equal([ok], Ids(await ListAsync($"workflow={key}&status=succeeded"))); // case-insensitive
        Assert.Equal([queued], Ids(await ListAsync($"workflow={key}&status=Queued")));

        var summary = (await ListAsync($"workflow={key}&status=Rejected")).GetProperty("items")[0];
        Assert.Equal("data.invalid", summary.GetProperty("failureCode").GetString());
        Assert.Equal(key, summary.GetProperty("workflowKey").GetString());
        Assert.Equal(1, summary.GetProperty("workflowVersion").GetInt32());

        var correlation = (await Kit.GetExecutionAsync(ok)).GetProperty("correlationId").GetString();
        Assert.Equal([ok], Ids(await ListAsync($"correlationId={correlation}")));

        var now = DateTimeOffset.UtcNow;
        Assert.Equal(3, (await ListAsync($"workflow={key}&from={Uri.EscapeDataString(now.AddHours(-1).ToString("o"))}&to={Uri.EscapeDataString(now.AddHours(1).ToString("o"))}")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await ListAsync($"workflow={key}&to={Uri.EscapeDataString(now.AddHours(-1).ToString("o"))}")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await ListAsync($"workflow={key}&from={Uri.EscapeDataString(now.AddHours(1).ToString("o"))}")).GetProperty("total").GetInt32());

        var page2 = await ListAsync($"workflow={key}&pageSize=2&page=2");
        Assert.Equal([ok], Ids(page2));
        Assert.Equal(3, page2.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task List_RejectsBadFilters()
    {
        using var badStatus = await _client.GetAsync(new Uri("/api/v1/executions?status=Exploded", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);
        Assert.True((await ExecutionTestKit.ReadAsync(badStatus)).GetProperty("errors").TryGetProperty("status", out _));

        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("o"));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("o"));
        using var badRange = await _client.GetAsync(new Uri($"/api/v1/executions?from={from}&to={to}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, badRange.StatusCode);
    }

    // ----- Events and catch-up -----

    [Fact]
    public async Task Events_CatchUpAfterAnId_ReturnsExactlyTheMissingEvents()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync();
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);

        // A client watching a run that is interrupted at the archive step (Worker stopped), then resumed.
        using var shutdown = new CancellationTokenSource();
        _store.AfterWrite = k => { if (k.StartsWith("archive/", StringComparison.Ordinal)) { shutdown.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.RunEngineAsync(id, _printer, _store, _jobs, shutdown.Token));
        _store.AfterWrite = null;

        var seen = await EventsAsync(id, 0);
        var lastSeen = seen[^1].GetProperty("id").GetInt64();
        Assert.Equal("Succeeded", (await Kit.GetExecutionAsync(id)).GetProperty("steps")[2].GetProperty("status").GetString());
        Assert.Equal("Running", (await Kit.GetExecutionAsync(id)).GetProperty("status").GetString());

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);
        var missed = await EventsAsync(id, lastSeen);
        var everything = await EventsAsync(id, 0);

        Assert.NotEmpty(missed);
        Assert.All(missed, e => Assert.True(e.GetProperty("id").GetInt64() > lastSeen));
        Assert.Equal(everything.Select(e => e.GetProperty("id").GetInt64()), seen.Concat(missed).Select(e => e.GetProperty("id").GetInt64()));
        Assert.Equal(everything.Select(e => e.GetProperty("id").GetInt64()).Order(), everything.Select(e => e.GetProperty("id").GetInt64()));
        Assert.Equal("Succeeded", everything[^1].GetProperty("type").GetString());
        Assert.Equal("archive", missed.First(e => e.GetProperty("type").GetString() == "StepSucceeded").GetProperty("nodeId").GetString());
        Assert.Empty(await EventsAsync(id, everything[^1].GetProperty("id").GetInt64()));
    }

    [Fact]
    public async Task Events_OfUnknownExecution_Is404()
    {
        using var response = await _client.GetAsync(new Uri($"/api/v1/executions/{Guid.NewGuid()}/events", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ----- Restricted payload view -----

    [Fact]
    public async Task Input_IsReadableByAuditors_AndEachReadIsAudited()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync();
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);

        using var response = await _client.GetAsync(new Uri($"/api/v1/executions/{id}/input", UriKind.Relative));
        var body = await ExecutionTestKit.ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // the default test user is an Admin
        Assert.Equal("INV-2026-003901", body.GetProperty("data").GetProperty("invoice").GetProperty("number").GetString());
        Assert.Equal((await Kit.GetExecutionAsync(id)).GetProperty("inputHash").GetString(), body.GetProperty("inputHash").GetString());
        Assert.True(response.Headers.CacheControl!.NoStore);

        var audit = await Kit.WithDbAsync(db => db.AuditEntries
            .Where(a => a.EntityId == id.ToString() && a.Action == ExecutionService.Actions.InputViewed).Select(a => a.Details).ToListAsync());
        var entry = Assert.Single(audit);
        Assert.DoesNotContain("INV-2026-003901", entry, StringComparison.Ordinal); // the audit never holds payload content
    }

    [Theory]
    [InlineData("dev.viewer")]
    [InlineData("dev.operator")]
    [InlineData("dev.template-author")]
    [InlineData("dev.workflow-author")]
    public async Task Input_IsForbiddenWithoutAuditorOrAdmin_AndNotAudited(string user)
    {
        var key = await Kit.PublishInvoiceWorkflowAsync();
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);
        using var viewer = fixture.Factory.CreateClientAs(user);

        using var response = await viewer.GetAsync(new Uri($"/api/v1/executions/{id}/input", UriKind.Relative));
        using var unknown = await viewer.GetAsync(new Uri($"/api/v1/executions/{Guid.NewGuid()}/input", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", (await ExecutionTestKit.ReadAsync(response)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode); // doesn't reveal which executions exist
        Assert.DoesNotContain("INV-2026-003901", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, await Kit.WithDbAsync(db => db.AuditEntries.CountAsync(a => a.EntityId == id.ToString() && a.Action == ExecutionService.Actions.InputViewed)));

        // They can still read the execution itself (status, steps), which holds no payload values.
        using var detail = await viewer.GetAsync(new Uri($"/api/v1/executions/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.DoesNotContain("INV-2026-003901", await detail.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Input_AuditorCanRead_AndUnknownIdIs404ForThem()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync();
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);
        using var auditor = fixture.Factory.CreateClientAs("dev.auditor");

        using var ok = await auditor.GetAsync(new Uri($"/api/v1/executions/{id}/input", UriKind.Relative));
        using var missing = await auditor.GetAsync(new Uri($"/api/v1/executions/{Guid.NewGuid()}/input", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var actor = await Kit.WithDbAsync(db => db.AuditEntries
            .Where(a => a.EntityId == id.ToString() && a.Action == ExecutionService.Actions.InputViewed).Select(a => a.ActorId).SingleAsync());
        Assert.Equal("dev.auditor", actor); // the audit names who looked
    }

    // ----- Archive search and download -----

    [Fact]
    public async Task ArchiveSearch_MatchesFileNameAndMetadataValues_AsPlainText()
    {
        var api = DocumentApi();
        var key = await Kit.PublishInvoiceWorkflowAsync();
        var tag = Guid.NewGuid().ToString("N")[..8]; // the test database is shared: make every value unique to this test
        var first = await ArchiveAsync(key, $"INV-{tag}-1", $"Kestrel {tag} Logistics Ltd");
        var second = await ArchiveAsync(key, $"INV-{tag}-2", $"Orchid_Marine {tag} 100% Ltd");

        Assert.Equal([first], await SearchIdsAsync(api, $"q=INV-{tag}-1"));
        Assert.Equal([first], await SearchIdsAsync(api, $"q=kestrel%20{tag}%20logistics")); // case-insensitive, in a metadata value
        Assert.Equal([second], await SearchIdsAsync(api, $"q=orchid_marine%20{tag}%20100%25")); // _ and % are literal text
        Assert.Empty(await SearchIdsAsync(api, $"q={tag}%25")); // a % is not a wildcard
        Assert.Empty(await SearchIdsAsync(api, "q=%25%25%25%25%25%25%25%25%25%25%25%25%25%25%25%25%25%25%25%25%25%25%25")); // nor "match everything"
        Assert.Empty(await SearchIdsAsync(api, $"q=InvoiceNumber%20{tag}")); // keys aren't searched
        Assert.Equal([second, first], (await SearchIdsAsync(api, $"q=INV-{tag}")).ToArray()); // newest first

        var now = DateTimeOffset.UtcNow;
        Assert.Equal([second, first], await SearchIdsAsync(api, $"q=INV-{tag}&from={Uri.EscapeDataString(now.AddHours(-1).ToString("o"))}"));
        Assert.Empty(await SearchIdsAsync(api, $"q=INV-{tag}&to={Uri.EscapeDataString(now.AddHours(-1).ToString("o"))}"));

        using var detail = await api.GetAsync(new Uri($"/api/v1/documents/{first}", UriKind.Relative));
        var body = await ExecutionTestKit.ReadAsync(detail);
        Assert.Equal($"INV-{tag}-1.pdf", body.GetProperty("fileName").GetString());
        Assert.Equal("Archived", body.GetProperty("status").GetString());
        Assert.Equal("invoices/2026/11", body.GetProperty("folderPath").GetString());
        Assert.Equal("7y", body.GetProperty("retention").GetString());
        Assert.Equal($"Kestrel {tag} Logistics Ltd", body.GetProperty("metadata").GetProperty("Customer").GetString());
        Assert.Equal(64, body.GetProperty("sha256").GetString()!.Length);

        using var missing = await api.GetAsync(new Uri($"/api/v1/documents/{Guid.NewGuid()}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Download_ReturnsTheArchivedBytes_VerifiedAndAudited()
    {
        var api = DocumentApi();
        var key = await Kit.PublishInvoiceWorkflowAsync();
        var id = await ArchiveAsync(key, "INV-2026-007101", "Kestrel Logistics Ltd");
        var recorded = await Kit.WithDbAsync(db => db.Documents.Where(d => d.Id == id).Select(d => d.Sha256).SingleAsync());

        using var response = await api.GetAsync(new Uri($"/api/v1/documents/{id}/content", UriKind.Relative));
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("INV-2026-007101.pdf", response.Content.Headers.ContentDisposition.FileNameStar ?? response.Content.Headers.ContentDisposition.FileName);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(recorded, DocumentKeys.Sha256Hex(bytes));

        var audit = await Kit.WithDbAsync(db => db.AuditEntries
            .Where(a => a.EntityId == id.ToString() && a.Action == DocumentService.DownloadedAction).Select(a => a.Details).ToListAsync());
        Assert.Contains(recorded, Assert.Single(audit), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Download_RefusesTamperedOrMissingFiles_WithoutAuditing()
    {
        var api = DocumentApi();
        var key = await Kit.PublishInvoiceWorkflowAsync();
        var tampered = await ArchiveAsync(key, "INV-2026-007201", "Kestrel Logistics Ltd");
        var missing = await ArchiveAsync(key, "INV-2026-007202", "Kestrel Logistics Ltd");
        await File.AppendAllTextAsync(Assert.Single(_store.Files("archive"), f => f.Contains(tampered.ToString("N"), StringComparison.Ordinal)), "tampered");
        File.Delete(Assert.Single(_store.Files("archive"), f => f.Contains(missing.ToString("N"), StringComparison.Ordinal)));

        using var integrity = await api.GetAsync(new Uri($"/api/v1/documents/{tampered}/content", UriKind.Relative));
        using var gone = await api.GetAsync(new Uri($"/api/v1/documents/{missing}/content", UriKind.Relative));
        using var unknown = await api.GetAsync(new Uri($"/api/v1/documents/{Guid.NewGuid()}/content", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Conflict, integrity.StatusCode);
        Assert.Equal("document.integrity_failed", (await ExecutionTestKit.ReadAsync(integrity)).GetProperty("code").GetString());
        Assert.Equal("document.content_missing", (await ExecutionTestKit.ReadAsync(gone)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(0, await Kit.WithDbAsync(db => db.AuditEntries.CountAsync(a => (a.EntityId == tampered.ToString() || a.EntityId == missing.ToString()) && a.Action == DocumentService.DownloadedAction)));
    }

    [Fact]
    public async Task Documents_NotYetArchived_AreNotListedOrDownloadable()
    {
        var api = DocumentApi();
        var key = await Kit.PublishInvoiceWorkflowAsync();
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);

        // The Worker stops during the archive step: the PDF is generated and saved, the archive record isn't.
        using var shutdown = new CancellationTokenSource();
        _store.AfterWrite = k => { if (k.StartsWith("archive/", StringComparison.Ordinal)) { shutdown.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Kit.RunEngineAsync(id, _printer, _store, _jobs, shutdown.Token));
        _store.AfterWrite = null;
        var documentId = await Kit.WithDbAsync(db => db.Documents.Where(d => d.ExecutionId == id).Select(d => d.Id).SingleAsync());
        Assert.Equal("Generated", (await Kit.GetExecutionAsync(id)).GetProperty("documents")[0].GetProperty("status").GetString());

        Assert.DoesNotContain(documentId, await SearchIdsAsync(api, "q=INV-2026-003901"));
        using var detail = await api.GetAsync(new Uri($"/api/v1/documents/{documentId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        using var content = await api.GetAsync(new Uri($"/api/v1/documents/{documentId}/content", UriKind.Relative));
        Assert.Equal("document.not_archived", (await ExecutionTestKit.ReadAsync(content)).GetProperty("code").GetString());

        // After the run completes it is listed and downloadable.
        await Kit.RunEngineAsync(id, _printer, _store, _jobs);
        Assert.Contains(documentId, await SearchIdsAsync(api, "q=INV-2026-003901"));
    }

    // ----- Live progress relay -----

    [Fact]
    public async Task Relay_SendsEveryEventOfARunExactlyOnce_InOrder()
    {
        var recorder = new RecordingExecutionNotifier();
        var relay = NewRelay(recorder);
        await relay.RelayOnceAsync(CancellationToken.None); // starts at the latest event: no history replay

        var key = await Kit.PublishInvoiceWorkflowAsync();
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);
        await Kit.RunEngineAsync(id, _printer, _store, _jobs);
        await relay.RelayOnceAsync(CancellationToken.None);
        await relay.RelayOnceAsync(CancellationToken.None); // a second poll repeats nothing

        var stored = await Kit.WithDbAsync(db => db.ExecutionEvents.Where(e => e.ExecutionId == id).OrderBy(e => e.Id).Select(e => e.Id).ToListAsync());
        var sent = recorder.Sent.Where(e => e.ExecutionId == id).ToList();
        Assert.Equal(stored, sent.Select(e => e.Id));
        Assert.Equal("Succeeded", sent[^1].ExecutionStatus);
        Assert.Equal(ExecutionEventTypes.Succeeded, sent[^1].Type);

        // StepUpdated is derived from the step events: status, node and the port the step left by.
        var steps = sent.Select(ExecutionEventRelay.StepStatus).Where(s => s is not null).Select(s => s!.Value).ToList();
        Assert.Equal(8, steps.Count);
        Assert.Equal(("Succeeded", "valid"), steps[3]);
        Assert.Equal(("Running", null), steps[4]);
        Assert.Null(ExecutionEventRelay.StepStatus(sent[0])); // Submitted isn't a step event
    }

    [Fact]
    public async Task Relay_DeliversAnEventThatBecomesVisibleLate_WithoutRepeatingOthers()
    {
        var recorder = new RecordingExecutionNotifier();
        var relay = NewRelay(recorder);
        await relay.RelayOnceAsync(CancellationToken.None);
        var key = await Kit.PublishInvoiceWorkflowAsync();
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);

        // Two writers: the first takes an id but commits late; the second takes the next id and commits at once.
        var late = ExecutionEvent.Record(id, "Redispatched", DateTimeOffset.UtcNow);
        await Kit.WithDbAsync(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            db.ExecutionEvents.Add(late);
            await db.SaveChangesAsync();
            await tx.RollbackAsync(); // burns the id; the row appears later (below)
            return 0;
        });
        await Kit.WithDbAsync(async db =>
        {
            db.ExecutionEvents.Add(ExecutionEvent.Record(id, "Started", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
            return 0;
        });
        await relay.RelayOnceAsync(CancellationToken.None);
        Assert.DoesNotContain(recorder.Sent, e => e.Id == late.Id);
        var before = recorder.Sent.Count;

        await Kit.WithDbAsync(db => db.Database.ExecuteSqlAsync($"""
            SET IDENTITY_INSERT [executions].[ExecutionEvents] ON;
            INSERT INTO [executions].[ExecutionEvents] ([Id], [ExecutionId], [Type], [OccurredAt]) VALUES ({late.Id}, {id}, N'Redispatched', SYSDATETIMEOFFSET());
            SET IDENTITY_INSERT [executions].[ExecutionEvents] OFF;
            """));
        await relay.RelayOnceAsync(CancellationToken.None);
        await relay.RelayOnceAsync(CancellationToken.None);

        Assert.Equal(before + 1, recorder.Sent.Count);
        Assert.Equal(1, recorder.Sent.Count(e => e.Id == late.Id));
        Assert.Equal(recorder.Sent.Count, recorder.Sent.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public async Task Hub_ExposesTheExecutionSubscriptions()
    {
        using var response = await _client.PostAsync(new Uri("/hubs/executions/negotiate?negotiateVersion=1", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var methods = typeof(ExecutionsHub).GetMethods().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Superset(new HashSet<string> { "SubscribeExecution", "UnsubscribeExecution", "SubscribeWorkflow", "UnsubscribeWorkflow", "SubscribeExecutions", "UnsubscribeExecutions" }, methods);
    }

    // ----- Helpers -----

    private ExecutionEventRelay NewRelay(IExecutionNotifier notifier) =>
        new(fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(), notifier, NullLogger<ExecutionEventRelay>.Instance);

    /// <summary>A client of an API host that reads documents from this test's store.</summary>
    private HttpClient DocumentApi()
    {
        _storeFactory = fixture.Factory.WithWebHostBuilder(b => b.UseSetting("Documents:RootPath", _store.Root));
        return _storeFactory.CreateClient();
    }

    /// <summary>Runs a valid invoice with the given number and customer to the archive; returns its document id.</summary>
    private async Task<Guid> ArchiveAsync(string workflowKey, string number, string customer)
    {
        var data = ExecutionTestKit.Sample;
        data["invoice"]!["number"] = number;
        data["customer"]!["name"] = customer;
        var id = await Kit.SubmitNewAsync(workflowKey, data);
        await Kit.RunEngineAsync(id, _printer, _store, _jobs);
        var execution = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", execution.GetProperty("status").GetString());
        return execution.GetProperty("documents")[0].GetProperty("id").GetGuid();
    }

    private static async Task<List<Guid>> SearchIdsAsync(HttpClient api, string query)
    {
        using var response = await api.GetAsync(new Uri($"/api/v1/documents?{query}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await ExecutionTestKit.ReadAsync(response)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())];
    }

    private async Task<JsonElement> ListAsync(string query)
    {
        using var response = await _client.GetAsync(new Uri($"/api/v1/executions?{query}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ExecutionTestKit.ReadAsync(response);
    }

    private static List<Guid> Ids(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())];

    private async Task<List<JsonElement>> EventsAsync(Guid id, long after)
    {
        using var response = await _client.GetAsync(new Uri($"/api/v1/executions/{id}/events?after={after}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await ExecutionTestKit.ReadAsync(response)).EnumerateArray()];
    }
}
