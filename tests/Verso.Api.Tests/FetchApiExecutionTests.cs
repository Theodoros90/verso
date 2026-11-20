using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Verso.Application.Connections;
using Verso.Domain.Executions;
using Verso.Rendering;

namespace Verso.Api.Tests;

/// <summary>
/// The Fetch from API step in a real run (engine side): the path and query values come from the run data (the path encoded piece by piece, the query pairs raw),
/// the JSON answer is merged under the result name, 404 / empty / JSON null leave by 'not-found', a value the data can't give rejects the run without calling the
/// API, retryable failures go through the engine's retries, a resumed run fetches again, and no value, answer or path reaches history, events, audit or logs.
/// The API is a <see cref="FakeApiFetch"/>; the service and the HTTP sender are covered by ApiFetchTests.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class FetchApiExecutionTests(MigratedDatabaseFixture fixture) : IDisposable
{
    /// <summary>A value that exists only in the fake API's answer.</summary>
    private const string AnswerMarker = "Quokka Fetch Holdings";

    /// <summary>A value that exists only in the submitted data and is used in the path and the query.</summary>
    private const string ValueMarker = "PO-FMARK-4411";

    private const string Connection = "rates-api";

    private readonly HttpClient _client = fixture.Factory.CreateClient();
    private readonly TestDocumentStore _store = new();
    private readonly CapturingPdfPrinter _printer = new();
    private readonly RecordingExecutionJobs _jobs = new();
    private readonly FakeApiFetch _api = new() { Result = FakeApiFetch.Answer($$"""{ "name": "{{AnswerMarker}}", "address": "1 Synthetic Answer Way, 0000 Nowhere", "vatNumber": "CY00000000Z" }""") };
    private ExecutionTestKit Kit => new(fixture.Factory, _client);

    public void Dispose()
    {
        _store.Dispose();
        _client.Dispose();
    }

    private static JsonObject Fetch(string path = "/customers/{{invoice.poNumber}}", string query = "currency = invoice.currency\npo = invoice.poNumber", string resultName = "customer") => new()
    {
        ["connection"] = Connection,
        ["path"] = path,
        ["query"] = query,
        ["resultName"] = resultName,
        ["maxResponseKB"] = 32,
        ["timeoutSeconds"] = 20,
    };

    private static JsonNode Data(string? poNumber = ValueMarker)
    {
        var data = ExecutionTestKit.Sample.DeepClone();
        if (poNumber is null)
        {
            data["invoice"]!.AsObject().Remove("poNumber");
        }
        else
        {
            data["invoice"]!["poNumber"] = poNumber;
        }

        return data;
    }

    /// <summary>The execution JSON without the archive metadata of its documents: values the workflow's Archive step was told to record on purpose.</summary>
    private static string WithoutArchiveMetadata(JsonElement execution)
    {
        var node = JsonNode.Parse(execution.GetRawText())!;
        foreach (var document in node["documents"]!.AsArray())
        {
            (document!["archive"] as JsonObject)?.Remove("metadata");
        }

        return node.ToJsonString();
    }

    private static JsonElement Step(JsonElement execution, string nodeId) =>
        execution.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("nodeId").GetString() == nodeId);

    private Task RunEngineAsync(Guid id, bool captureLogs = false) => Kit.RunEngineAsync(id, _printer, _store, _jobs, apiFetch: _api, captureLogs: captureLogs);

    private async Task<(Guid Id, JsonElement Done)> RunAsync(JsonObject config, JsonNode? data = null, bool notFoundToPdf = false)
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(fetchConfig: config, fetchNotFoundToPdf: notFoundToPdf);
        var id = await Kit.SubmitNewAsync(key, data ?? Data());
        await RunEngineAsync(id);
        return (id, await Kit.GetExecutionAsync(id));
    }

    private Task<List<string>> EventTypesAsync(Guid id) =>
        Kit.WithDbAsync(db => db.ExecutionEvents.Where(e => e.ExecutionId == id).OrderBy(e => e.Id).Select(e => e.Type).ToListAsync());

    [Fact]
    public async Task Found_merges_json_and_the_document_uses_it()
    {
        var (_, done) = await RunAsync(Fetch());

        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        var request = Assert.Single(_api.Requests);
        Assert.Equal(Connection, request.ConnectionKey);
        Assert.Equal("/customers/" + ValueMarker, request.Path);
        Assert.Equal([new("currency", "EUR"), new("po", ValueMarker)], request.Query);
        Assert.Equal(32 * 1024, request.MaxResponseBytes);
        Assert.Equal(TimeSpan.FromSeconds(20), request.Timeout);

        // The answer replaced 'customer' (it existed in the data): the PDF shows the API's name, not the submitted one.
        var html = Assert.Single(_printer.Bodies);
        Assert.Contains(AnswerMarker, html, StringComparison.Ordinal);
        Assert.Contains("Synthetic Answer Way", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Kestrel Logistics Ltd", html, StringComparison.Ordinal);

        var step = Step(done, "fetch");
        Assert.Equal("found", step.GetProperty("outputPort").GetString());

        // The test workflow's Archive step records {{customer.name}} as metadata (the author's choice, shown with the document); nowhere else may a value appear.
        Assert.Equal(AnswerMarker, Assert.Single(done.GetProperty("documents").EnumerateArray()).GetProperty("archive").GetProperty("metadata").GetProperty("Customer").GetString());
        var json = WithoutArchiveMetadata(done);
        Assert.DoesNotContain(AnswerMarker, json, StringComparison.Ordinal);
        Assert.DoesNotContain("Synthetic Answer", json, StringComparison.Ordinal);
        Assert.DoesNotContain(ValueMarker, json, StringComparison.Ordinal);
        Assert.DoesNotContain("/customers", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Array_and_scalar_answers_are_merged()
    {
        // A list: the For each after the step sees two records under the result name.
        _api.Result = FakeApiFetch.Answer("""[ { "name": "Contact one" }, { "name": "Contact two" } ]""");
        var key = await Kit.PublishInvoiceWorkflowAsync(fetchConfig: Fetch(resultName: "contacts"), forEachConfig: new JsonObject { ["field"] = "contacts", ["mode"] = "each", ["item"] = "contact" });
        var id = await Kit.SubmitNewAsync(key, Data());
        await RunEngineAsync(id);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(2, Step(done, "loop").GetProperty("result").GetProperty("count").GetInt32());
        Assert.Equal(2, _printer.Bodies.Count);
        Assert.DoesNotContain("Contact one", done.GetRawText(), StringComparison.Ordinal);

        // A scalar: one item (a For each over a missing field would find none and leave by 'empty', printing nothing).
        _api.Result = FakeApiFetch.Answer("\"Gold tier\"");
        key = await Kit.PublishInvoiceWorkflowAsync(fetchConfig: Fetch(resultName: "tier"), forEachConfig: new JsonObject { ["field"] = "tier", ["mode"] = "each", ["item"] = "level" });
        id = await Kit.SubmitNewAsync(key, Data());
        await RunEngineAsync(id);

        done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal("found", Step(done, "fetch").GetProperty("outputPort").GetString());
        Assert.Equal(1, Step(done, "loop").GetProperty("result").GetProperty("count").GetInt32());
        Assert.Equal(3, _printer.Bodies.Count);
    }

    [Fact]
    public async Task Not_found_port_followed_when_connected_and_run_ends_succeeded_when_not()
    {
        _api.Result = FakeApiFetch.Nothing(404);

        var (_, connected) = await RunAsync(Fetch(), notFoundToPdf: true);

        Assert.Equal("Succeeded", connected.GetProperty("status").GetString());
        var step = Step(connected, "fetch");
        Assert.Equal("not-found", step.GetProperty("outputPort").GetString());
        Assert.Equal(404, step.GetProperty("result").GetProperty("status").GetInt32());
        Assert.Equal("Succeeded", Step(connected, "pdf").GetProperty("status").GetString());
        Assert.Contains("Kestrel Logistics Ltd", Assert.Single(_printer.Bodies), StringComparison.Ordinal); // the data is unchanged

        _api.Result = FakeApiFetch.Nothing(204); // an empty answer is 'not-found' too
        var (_, open) = await RunAsync(Fetch());

        Assert.Equal("Succeeded", open.GetProperty("status").GetString());
        Assert.Equal("not-found", Step(open, "fetch").GetProperty("outputPort").GetString());
        Assert.Equal("Skipped", Step(open, "pdf").GetProperty("status").GetString());
        Assert.Single(_printer.Bodies); // nothing printed by the second run
    }

    [Fact]
    public async Task Json_null_answer_is_not_found()
    {
        _api.Result = FakeApiFetch.Answer("null");

        var (_, done) = await RunAsync(Fetch(), notFoundToPdf: true);

        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal("not-found", Step(done, "fetch").GetProperty("outputPort").GetString());
        Assert.Contains("Kestrel Logistics Ltd", Assert.Single(_printer.Bodies), StringComparison.Ordinal); // 'customer' was not replaced by null
    }

    [Fact]
    public async Task Missing_value_rejects_without_calling()
    {
        var (_, done) = await RunAsync(Fetch(), Data(poNumber: null));

        Assert.Equal(0, _api.CallCount);
        Assert.Equal("Rejected", done.GetProperty("status").GetString());
        Assert.Equal("data.fetch_invalid", done.GetProperty("failureCode").GetString());
        var step = Step(done, "fetch");
        Assert.Equal("failed", step.GetProperty("outputPort").GetString());
        var issues = step.GetProperty("result").GetProperty("issues").EnumerateArray().ToList();
        Assert.Equal(2, issues.Count); // the path piece and the query line both need invoice.poNumber
        Assert.All(issues, i => Assert.Equal("fetch.value_missing", i.GetProperty("code").GetString()));
        Assert.All(issues, i => Assert.Contains("invoice.poNumber", i.GetProperty("message").GetString(), StringComparison.Ordinal));
        Assert.Contains("po", issues[1].GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(_printer.Bodies);
        Assert.DoesNotContain("Kestrel", done.GetRawText(), StringComparison.Ordinal);

        // A non-scalar value is the data's fault too.
        var (_, group) = await RunAsync(Fetch(path: "/customers/{{customer}}", query: ""));
        Assert.Equal(0, _api.CallCount);
        Assert.Equal("data.fetch_invalid", group.GetProperty("failureCode").GetString());
        Assert.Equal("fetch.value_invalid", Assert.Single(Step(group, "fetch").GetProperty("result").GetProperty("issues").EnumerateArray()).GetProperty("code").GetString());
        Assert.DoesNotContain("Kestrel", group.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dot_segment_and_lone_surrogate_values_reject_without_calling()
    {
        foreach (var value in new[] { "..", "." })
        {
            var (_, done) = await RunAsync(Fetch(query: ""), Data(poNumber: value));

            Assert.Equal("Rejected", done.GetProperty("status").GetString());
            Assert.Equal("data.fetch_invalid", done.GetProperty("failureCode").GetString());
            Assert.Equal("fetch.value_invalid", Assert.Single(Step(done, "fetch").GetProperty("result").GetProperty("issues").EnumerateArray()).GetProperty("code").GetString());
        }

        // A lone surrogate (only expressible as a JSON escape) in the path and in a query value: URL-encoding would send U+FFFD instead.
        // No route lets one into the run data today: the submission API refuses such text, the Parse step can't read it, and an API answer carrying a "\uD800"
        // escape (valid JSON, accepted by the sender) is refused before it is merged (below). The rejection of a lone surrogate in a path piece or query value is
        // therefore defence in depth, covered by ApiFetchStepTests (Resolve / ResolveQuery). Here: the answer route is closed, final, and no later step calls.
        var calls = _api.CallCount;
        _api.Result = FakeApiFetch.Answer("""{ "code": "PO\uD800X", "ref": "REF\uDC00" }""");
        var key = await Kit.PublishInvoiceWorkflowAsync(
            fetchConfig: Fetch(path: "/lookup", query: "", resultName: "lookup"),
            secondFetchConfig: Fetch(path: "/customers/{{lookup.code}}", query: "ref = lookup.ref"));
        var id = await Kit.SubmitNewAsync(key, Data());
        await RunEngineAsync(id);

        var surrogate = await Kit.GetExecutionAsync(id);
        Assert.Equal("Failed", surrogate.GetProperty("status").GetString());
        Assert.Equal("fetch.answer_unsupported", surrogate.GetProperty("failureCode").GetString());
        Assert.Empty(_jobs.Scheduled); // not retried: the same answer would fail again
        Assert.Equal("Pending", Step(surrogate, "fetch2").GetProperty("status").GetString());
        Assert.Equal(calls + 1, _api.CallCount); // only the first fetch called
        Assert.Equal("/lookup", _api.Requests[^1].Path);
        Assert.DoesNotContain("UTF-16", surrogate.GetRawText(), StringComparison.Ordinal); // .NET's exception text is not kept
    }

    [Fact]
    public async Task Hostile_path_value_reaches_the_service_encoded()
    {
        const string hostile = "../admin?x=1#f/%2e";

        var (_, done) = await RunAsync(Fetch(query: "po = invoice.poNumber"), Data(poNumber: hostile));

        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        var request = Assert.Single(_api.Requests);
        Assert.Equal("/customers/..%2Fadmin%3Fx%3D1%23f%2F%252e", request.Path); // one encoded segment: no new segment, query or fragment
        Assert.Equal("/customers/" + Uri.EscapeDataString(hostile), request.Path);
        Assert.Equal([new("po", hostile)], request.Query); // query values are raw; the sender encodes them
    }

    [Fact]
    public async Task Query_values_are_passed_raw_pairs_in_order()
    {
        var data = Data(poNumber: "a&b=c d");
        data["flag"] = true;
        var (_, done) = await RunAsync(Fetch(path: "/v1/latest", query: "z = invoice.currency\na = invoice.poNumber\nn = totals.gross\nb = issuer.name\nflag = flag"), data);

        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        var request = Assert.Single(_api.Requests);
        Assert.Equal("/v1/latest", request.Path);
        Assert.Equal([new("z", "EUR"), new("a", "a&b=c d"), new("n", "5516.69"), new("b", "Halcyon Energy Ltd"), new("flag", "true")], request.Query);
    }

    [Fact]
    public async Task Retryable_failure_is_retried_and_succeeds()
    {
        _api.Failure = ApiFetchResult.Failure("api.server_error", retryable: true, httpStatus: 503);
        _api.FailTimes = 1;
        var key = await Kit.PublishInvoiceWorkflowAsync(fetchConfig: Fetch());
        var id = await Kit.SubmitNewAsync(key, Data());

        await RunEngineAsync(id);

        var first = await Kit.GetExecutionAsync(id);
        Assert.Equal("Running", first.GetProperty("status").GetString()); // a retry is scheduled
        Assert.Equal("Failed", Step(first, "fetch").GetProperty("status").GetString());
        Assert.NotEmpty(_jobs.Scheduled);
        Assert.Contains(ExecutionEventTypes.RetryScheduled, await EventTypesAsync(id));
        Assert.Empty(_printer.Bodies);

        await RunEngineAsync(id);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(2, _api.CallCount);
        Assert.Equal(2, Step(done, "fetch").GetProperty("attempt").GetInt32());
        Assert.Contains(AnswerMarker, Assert.Single(_printer.Bodies), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_retryable_failure_fails_the_run_without_retry()
    {
        _api.Failure = ApiFetchResult.Failure("api.unauthorized", retryable: false, httpStatus: 401);

        var (id, done) = await RunAsync(Fetch());

        Assert.Equal("Failed", done.GetProperty("status").GetString());
        Assert.Equal("api.unauthorized", done.GetProperty("failureCode").GetString());
        Assert.Contains(ApiFetchMessages.For("api.unauthorized"), done.GetProperty("failureMessage").GetString(), StringComparison.Ordinal);
        Assert.Empty(_jobs.Scheduled);
        Assert.DoesNotContain(ExecutionEventTypes.RetryScheduled, await EventTypesAsync(id));
        Assert.Equal(1, _api.CallCount);
        Assert.Empty(_printer.Bodies);
    }

    [Fact]
    public async Task An_unexpected_exception_from_the_service_fails_the_run_without_retry_and_without_its_text()
    {
        _api.Throw = new InvalidOperationException("secret text https://api.internal.example/v1?key=TOPSECRET");

        var (id, done) = await RunAsync(Fetch());

        Assert.Equal("Failed", done.GetProperty("status").GetString());
        Assert.Equal("api.unreachable", done.GetProperty("failureCode").GetString());
        Assert.Empty(_jobs.Scheduled);
        Assert.DoesNotContain(ExecutionEventTypes.RetryScheduled, await EventTypesAsync(id));
        Assert.Equal(1, _api.CallCount);
        Assert.DoesNotContain("secret", done.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("internal.example", done.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Too_deep_answer_fails_without_retry()
    {
        // 64 levels parse (the sender's limit); one more level under the result name can't be kept in the data.
        _api.Result = FakeApiFetch.Answer(new string('[', 64) + new string(']', 64));

        var (_, done) = await RunAsync(Fetch(resultName: "deep"));

        Assert.Equal("Failed", done.GetProperty("status").GetString());
        Assert.Equal("fetch.answer_too_deep", done.GetProperty("failureCode").GetString());
        Assert.Empty(_jobs.Scheduled);
        Assert.Empty(_printer.Bodies);
    }

    [Fact]
    public async Task Resume_refetches_and_failed_refetch_is_fetch_restore()
    {
        // The answer is not stored: the resumed run fetches again, with the same path and query, and the document uses it.
        var id = await AwaitingApprovalAsync();
        Assert.Equal(1, _api.CallCount);

        await ApproveAsync(id);
        await RunEngineAsync(id);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(2, _api.CallCount);
        Assert.Equal(_api.Requests[0].Path, _api.Requests[1].Path);
        Assert.Equal(_api.Requests[0].Query, _api.Requests[1].Query);
        Assert.Contains(AnswerMarker, Assert.Single(_printer.Bodies), StringComparison.Ordinal);
        Assert.Equal(1, Step(done, "fetch").GetProperty("attempt").GetInt32()); // restored, not run again as a step

        // The re-fetch fails (retryable kind): the run fails with fetch.restore, the step stays Succeeded, nothing is printed.
        id = await AwaitingApprovalAsync();
        _api.Failure = ApiFetchResult.Failure("api.server_error", retryable: true, httpStatus: 503);
        await ApproveAsync(id);
        await RunEngineAsync(id);

        var failed = await Kit.GetExecutionAsync(id);
        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Equal("fetch.restore", failed.GetProperty("failureCode").GetString());
        Assert.Contains(ApiFetchMessages.For("api.server_error"), failed.GetProperty("failureMessage").GetString(), StringComparison.Ordinal);
        Assert.Equal("Succeeded", Step(failed, "fetch").GetProperty("status").GetString());
        var failedEvent = await Kit.WithDbAsync(db => db.ExecutionEvents.Where(e => e.ExecutionId == id && e.Type == ExecutionEventTypes.Failed).Select(e => e.Data).SingleAsync());
        Assert.True(JsonDocument.Parse(failedEvent!).RootElement.GetProperty("retryable").GetBoolean());
        Assert.Single(_printer.Bodies); // only the first scenario printed

        // The re-fetch finds nothing now (the API changed while the run waited): fetch.restore, not retryable.
        _api.Failure = null;
        id = await AwaitingApprovalAsync();
        _api.Result = FakeApiFetch.Nothing(404);
        await ApproveAsync(id);
        await RunEngineAsync(id);

        var gone = await Kit.GetExecutionAsync(id);
        Assert.Equal("Failed", gone.GetProperty("status").GetString());
        Assert.Equal("fetch.restore", gone.GetProperty("failureCode").GetString());
        Assert.Single(_printer.Bodies);
    }

    [Fact]
    public async Task Result_has_only_status_duration_bytes_connection()
    {
        var (_, found) = await RunAsync(Fetch());
        var result = Step(found, "fetch").GetProperty("result");
        Assert.Equal(["status", "durationMs", "responseBytes", "connection"], result.EnumerateObject().Select(p => p.Name));
        Assert.Equal(200, result.GetProperty("status").GetInt32());
        Assert.Equal(9, result.GetProperty("durationMs").GetInt64());
        Assert.Equal(_api.Result.ResponseBytes, result.GetProperty("responseBytes").GetInt64());
        Assert.Equal(Connection, result.GetProperty("connection").GetString());

        _api.Result = FakeApiFetch.Nothing(404);
        var (_, notFound) = await RunAsync(Fetch());
        result = Step(notFound, "fetch").GetProperty("result");
        Assert.Equal(["status", "durationMs", "responseBytes", "connection"], result.EnumerateObject().Select(p => p.Name));
        Assert.Equal(404, result.GetProperty("status").GetInt32());
        Assert.Equal(0, result.GetProperty("responseBytes").GetInt64());
    }

    [Fact]
    public async Task Values_and_secrets_never_appear_in_logs_events_audit_or_results()
    {
        var pathMarker = "FPATH" + Guid.NewGuid().ToString("N")[..12];
        var queryMarker = "FQUERY" + Guid.NewGuid().ToString("N")[..12];
        var answerMarker = "FANSWER" + Guid.NewGuid().ToString("N")[..12];
        var secretMarker = "FSECRET" + Guid.NewGuid().ToString("N")[..12];
        _api.Result = FakeApiFetch.Answer($$"""{ "name": "{{answerMarker}}", "address": "{{answerMarker}} street", "vatNumber": "CY00000000Z", "token": "{{secretMarker}}" }""");
        var config = Fetch(path: "/customers/{{invoice.poNumber}}", query: "ref = payment.reference");
        var key = await Kit.PublishInvoiceWorkflowAsync(fetchConfig: config, approvalConfig: new JsonObject { ["approver"] = "operator", ["escalateAfter"] = "24h" });
        var data = Data(poNumber: pathMarker);
        data["payment"]!["reference"] = queryMarker;

        // Found, then resumed after the approval (a re-fetch); a run whose fetch fails retryably until the attempts run out; a run with a missing value
        // (whose submitted customer name equals the answer's); a run whose service throws with a secret in the text.
        var found = await Kit.SubmitNewAsync(key, data);
        await RunEngineAsync(found, captureLogs: true);
        await ApproveAsync(found);
        await RunEngineAsync(found, captureLogs: true);

        var failing = await Kit.SubmitNewAsync(key, data);
        _api.Failure = ApiFetchResult.Failure("api.timeout", retryable: true);
        for (var attempt = 0; attempt < Verso.Application.Executions.ExecutionEngine.MaxStepAttempts; attempt++)
        {
            await RunEngineAsync(failing, captureLogs: true);
        }

        _api.Failure = null;
        var missing = data.DeepClone();
        missing["invoice"]!.AsObject().Remove("poNumber");
        missing["customer"]!["name"] = answerMarker;
        var rejected = await Kit.SubmitNewAsync(key, missing);
        await RunEngineAsync(rejected, captureLogs: true);

        _api.Throw = new InvalidOperationException($"boom {secretMarker} /customers/{pathMarker}?ref={queryMarker}");
        var thrown = await Kit.SubmitNewAsync(key, data);
        await RunEngineAsync(thrown, captureLogs: true);

        Assert.Contains(_api.Requests, r => r.Path.Contains(pathMarker, StringComparison.Ordinal)); // the values were used
        Assert.Contains(_api.Requests, r => r.Query.Any(p => p.Value == queryMarker));
        Assert.Contains(answerMarker, string.Join('\n', _printer.Bodies), StringComparison.Ordinal); // and the answer reached the document
        var markers = new[] { pathMarker, queryMarker, answerMarker, secretMarker, "/customers" };

        foreach (var id in new[] { found, failing, rejected, thrown })
        {
            var done = await Kit.GetExecutionAsync(id);
            using var events = await _client.GetAsync(new Uri($"/api/v1/executions/{id}/events", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, events.StatusCode);
            var eventsJson = await events.Content.ReadAsStringAsync();
            var (rows, audit) = await Kit.WithDbAsync(async db =>
            {
                var correlation = await db.Executions.Where(e => e.Id == id).Select(e => e.CorrelationId).SingleAsync();
                var eventData = await db.ExecutionEvents.Where(e => e.ExecutionId == id).Select(e => e.Type + " " + e.Data).ToListAsync();
                var stepRows = await db.ExecutionSteps.Where(s => s.ExecutionId == id).Select(s => s.Result + " " + s.Error).ToListAsync();
                var auditDetails = await db.AuditEntries.Where(a => a.EntityId == id.ToString() || a.TraceId == correlation).Select(a => a.Action + " " + a.Details).ToListAsync();
                return (string.Join('\n', eventData.Concat(stepRows)), string.Join('\n', auditDetails));
            });

            Assert.NotEmpty(rows);
            foreach (var text in new[] { WithoutArchiveMetadata(done), eventsJson, rows, audit })
            {
                foreach (var marker in markers)
                {
                    Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
                }
            }
        }

        var leaks = fixture.Factory.Logs.Entries.Where(e => markers.Take(4).Any(m => e.AllText.Contains(m, StringComparison.Ordinal))).ToList();
        Assert.True(leaks.Count == 0, "Fetch values in logs:\n" + string.Join("\n", leaks.Take(5).Select(l => $"[{l.Category}] {l.Message}")));
        Assert.Contains(fixture.Factory.Logs.Entries, e => e.Category.Contains("ExecutionEngine", StringComparison.Ordinal) && e.Level == Microsoft.Extensions.Logging.LogLevel.Warning); // failures were logged
    }

    [Fact]
    public async Task Inside_for_each_each_item_fetches()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(
            fetchConfig: Fetch(path: "/units/{{line.unit}}", query: "unit = line.unit"), fetchAt: "in-loop",
            forEachConfig: new JsonObject { ["field"] = "lines", ["mode"] = "each", ["item"] = "line" });
        var id = await Kit.SubmitNewAsync(key, Data());

        await RunEngineAsync(id);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(["/units/kWh", "/units/month", "/units/month"], _api.Requests.Select(r => r.Path));
        Assert.Equal(["kWh", "month", "month"], _api.Requests.Select(r => Assert.Single(r.Query).Value));
        Assert.Equal(3, _printer.Bodies.Count);
        Assert.All(_printer.Bodies, html => Assert.Contains(AnswerMarker, html, StringComparison.Ordinal));
    }

    private async Task<Guid> AwaitingApprovalAsync()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(fetchConfig: Fetch(), approvalConfig: new JsonObject { ["approver"] = "operator", ["escalateAfter"] = "24h" });
        var id = await Kit.SubmitNewAsync(key, Data());
        await RunEngineAsync(id);
        Assert.Equal("AwaitingApproval", (await Kit.GetExecutionAsync(id)).GetProperty("status").GetString());
        return id;
    }

    private async Task ApproveAsync(Guid id)
    {
        using var approver = fixture.Factory.CreateClientAs("dev.operator");
        using var response = await approver.PostAsJsonAsync(new Uri($"/api/v1/executions/{id}/approvals/approve", UriKind.Relative), new { decision = "approved", comment = "ok" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    /// <summary>Stands in for Chromium and keeps the HTML of every print (header, body and footer), so a test can see which values the document used.</summary>
    private sealed class CapturingPdfPrinter : IPdfPrinter
    {
        private readonly List<string> _bodies = [];

        public IReadOnlyList<string> Bodies
        {
            get
            {
                lock (_bodies)
                {
                    return [.. _bodies];
                }
            }
        }

        public Task<PdfOutput> PrintAsync(PrintDocument document, CancellationToken cancellationToken)
        {
            lock (_bodies)
            {
                _bodies.Add(document.HeaderTemplate + document.BodyHtml + document.FooterTemplate);
            }

            return Task.FromResult(new PdfOutput(Encoding.ASCII.GetBytes($"%PDF-1.7\n% fake {Guid.NewGuid():N}\n%%EOF\n"), 0, 1));
        }
    }
}
