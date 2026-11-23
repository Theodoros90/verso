using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Verso.Application.Connections;
using Verso.Application.Executions;
using Verso.Domain.Executions;

namespace Verso.Api.Tests;

/// <summary>
/// The Send SMS step in a real run (engine side): the number and the text are built from the run data, the service is called at most once per run attempt, a
/// data problem rejects the run without calling it, every failure of the send is final (never retried automatically), a send that was started but never
/// recorded as finished (a crash, a Worker stop) is never run again by the engine, a succeeded send is never repeated on resume, and neither the number nor the
/// text reaches history, events, audit or logs. Only an operator retry sends again. The provider is a <see cref="FakeSmsSend"/>; the real service and sender are
/// covered by SmsSendTests and SendSmsRealServiceExecutionTests.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class SendSmsExecutionTests(MigratedDatabaseFixture fixture) : IDisposable
{
    /// <summary>The number as the data holds it, and the digits the service must receive.</summary>
    private const string Mobile = "+357 99 000009";
    private const string MobileDigits = "35799000009";

    /// <summary>A value that exists only in the submitted data and is used in the text.</summary>
    private const string TextMarker = "TXTMARK-8812";

    private const string Unknown = SmsSendMessages.OutcomeUnknown;

    private readonly HttpClient _client = fixture.Factory.CreateClient();
    private readonly TestDocumentStore _store = new();
    private readonly FakePdfPrinter _printer = new();
    private readonly RecordingExecutionJobs _jobs = new();
    private readonly FakeSmsSend _sms = new();
    private readonly FakeDatabaseQuery _query = new();
    private ExecutionTestKit Kit => new(fixture.Factory, _client);

    public void Dispose()
    {
        _store.Dispose();
        _client.Dispose();
    }

    private static JsonObject Sms(string to = "{{customer.mobile}}", string text = "Dear {{customer.name}}, ref {{invoice.poNumber}}.") => new()
    {
        ["connection"] = "brevo-sms",
        ["to"] = to,
        ["text"] = text,
    };

    private static JsonNode Data(string? mobile = Mobile, string poNumber = TextMarker)
    {
        var data = ExecutionTestKit.Sample.DeepClone();
        if (mobile is not null)
        {
            data["customer"]!["mobile"] = mobile;
        }

        data["invoice"]!["poNumber"] = poNumber;
        return data;
    }

    private static JsonElement Step(JsonElement execution, string nodeId, int iteration = 0) =>
        execution.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("nodeId").GetString() == nodeId && s.GetProperty("iteration").GetInt32() == iteration);

    private Task RunEngineAsync(Guid id, bool captureLogs = false, CancellationToken ct = default) =>
        Kit.RunEngineAsync(id, _printer, _store, _jobs, ct: ct, query: _query, sms: _sms, captureLogs: captureLogs);

    private async Task<(Guid Id, JsonElement Done)> RunAsync(JsonObject config, JsonNode? data = null)
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(smsConfig: config);
        var id = await Kit.SubmitNewAsync(key, data ?? Data());
        await RunEngineAsync(id);
        return (id, await Kit.GetExecutionAsync(id));
    }

    private Task<List<string>> EventTypesAsync(Guid id) =>
        Kit.WithDbAsync(db => db.ExecutionEvents.Where(e => e.ExecutionId == id).OrderBy(e => e.Id).Select(e => e.Type).ToListAsync());

    private async Task RetryAsync(Guid id)
    {
        using var retry = await _client.PostAsync(new Uri($"/api/v1/executions/{id}/retry", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
    }

    private Task<List<string?>> SmsAuditAsync(Guid id) =>
        Kit.WithDbAsync(db => db.AuditEntries.Where(a => a.EntityId == id.ToString() && a.Action == "SmsSent").OrderBy(a => a.OccurredAt).Select(a => a.Details).ToListAsync());

    /// <summary>The run failed for good: Failed, the send step Failed, no retry scheduled or announced, the service called <paramref name="calls"/> times, nothing after it ran.</summary>
    private async Task AssertFailedWithoutRetryAsync(Guid id, JsonElement done, string code, int calls)
    {
        Assert.Equal("Failed", done.GetProperty("status").GetString());
        Assert.Equal(code, done.GetProperty("failureCode").GetString());
        Assert.Equal("Failed", Step(done, "sms").GetProperty("status").GetString());
        Assert.Equal(calls, _sms.CallCount);
        Assert.Empty(_jobs.Scheduled);
        Assert.DoesNotContain(ExecutionEventTypes.RetryScheduled, await EventTypesAsync(id));
        Assert.Equal("Pending", Step(done, "pdf").GetProperty("status").GetString());
        Assert.Equal(0, _printer.Calls);
        Assert.Empty(await SmsAuditAsync(id));
    }

    /// <summary>The run ended Rejected data.sms_invalid at the send step's 'failed' output; nothing after it ran; no value in the message.</summary>
    private void AssertRejected(JsonElement done, int calls)
    {
        Assert.Equal("Rejected", done.GetProperty("status").GetString());
        Assert.Equal("data.sms_invalid", done.GetProperty("failureCode").GetString());
        Assert.Equal("The data can't make a valid message in step 'Send SMS' (see its issues).", done.GetProperty("failureMessage").GetString());
        Assert.Equal("failed", Step(done, "sms").GetProperty("outputPort").GetString());
        Assert.Equal(calls, _sms.CallCount);
        Assert.Equal(0, _printer.Calls);
        Assert.Empty(_jobs.Scheduled);
        var json = done.GetRawText();
        Assert.DoesNotContain(MobileDigits, json, StringComparison.Ordinal);
        Assert.DoesNotContain(TextMarker, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sent_calls_the_service_once_and_data_is_unchanged()
    {
        var (id, done) = await RunAsync(Sms());

        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        var request = Assert.Single(_sms.Requests);
        Assert.Equal("brevo-sms", request.ConnectionKey);
        Assert.Equal(MobileDigits, request.RecipientDigits);
        Assert.Equal($"Dear Kestrel Logistics Ltd, ref {TextMarker}.", request.Text);
        Assert.Equal($"verso-{id.ToString("N")[..12]}-0", request.Tag);
        Assert.Matches("^verso-[0-9a-f]{12}-0$", request.Tag);

        var step = Step(done, "sms");
        Assert.Equal("sent", step.GetProperty("outputPort").GetString());
        Assert.Equal(1, step.GetProperty("attempt").GetInt32());
        var result = step.GetProperty("result");
        Assert.Equal(["messageId", "smsParts", "durationMs", "connection"], result.EnumerateObject().Select(p => p.Name));
        Assert.Equal("44111", result.GetProperty("messageId").GetString());
        Assert.Equal(1, result.GetProperty("smsParts").GetInt32());
        Assert.Equal(7, result.GetProperty("durationMs").GetInt64());
        Assert.Equal("brevo-sms", result.GetProperty("connection").GetString());

        // The data is unchanged: the document and the archive saw the submitted record.
        Assert.Equal("Succeeded", Step(done, "pdf").GetProperty("status").GetString());
        var archive = Assert.Single(done.GetProperty("documents").EnumerateArray()).GetProperty("archive").GetProperty("metadata");
        Assert.Equal("Kestrel Logistics Ltd", archive.GetProperty("Customer").GetString());

        // No number and no text in the execution, its events or its audit.
        using var events = await _client.GetAsync(new Uri($"/api/v1/executions/{id}/events", UriKind.Relative));
        var audit = await Kit.WithDbAsync(db => db.AuditEntries.Where(a => a.EntityId == id.ToString()).Select(a => a.Action + " " + a.Details).ToListAsync());
        foreach (var text in new[] { done.GetRawText(), await events.Content.ReadAsStringAsync(), string.Join('\n', audit) })
        {
            Assert.DoesNotContain(MobileDigits, text, StringComparison.Ordinal);
            Assert.DoesNotContain("99 000009", text, StringComparison.Ordinal); // the number as the data writes it (MobileDigits covers the digits)
            Assert.DoesNotContain(TextMarker, text, StringComparison.Ordinal);
            Assert.DoesNotContain(request.Tag, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Missing_value_rejects_without_calling()
    {
        var (_, done) = await RunAsync(Sms(), Data(mobile: null));

        AssertRejected(done, calls: 0);
        var issue = Assert.Single(Step(done, "sms").GetProperty("result").GetProperty("issues").EnumerateArray());
        Assert.Equal("sms.value_missing", issue.GetProperty("code").GetString());
        Assert.Equal("customer.mobile", issue.GetProperty("field").GetString());
    }

    [Fact]
    public async Task Invalid_number_in_data_rejects_without_calling()
    {
        var (_, done) = await RunAsync(Sms(), Data(mobile: "call me 99 000008"));

        AssertRejected(done, calls: 0);
        var issue = Assert.Single(Step(done, "sms").GetProperty("result").GetProperty("issues").EnumerateArray());
        Assert.Equal("sms.recipient_invalid", issue.GetProperty("code").GetString());
        Assert.Equal("to", issue.GetProperty("field").GetString());
        Assert.DoesNotContain("99 000008", done.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Out_of_range_number_in_data_is_an_issue_not_an_exception()
    {
        // 1e400 is valid JSON but not a double .NET can hold: reading it as a double throws FormatException. It must become a data issue, not a crash.
        var data = Data();
        data["customer"]!["mobile"] = JsonNode.Parse("1e400");

        var (_, done) = await RunAsync(Sms(), data);

        AssertRejected(done, calls: 0);
        var issue = Assert.Single(Step(done, "sms").GetProperty("result").GetProperty("issues").EnumerateArray());
        Assert.Equal("sms.value_invalid", issue.GetProperty("code").GetString());
        Assert.Equal("customer.mobile", issue.GetProperty("field").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Out_of_range_number_never_throws_from_Resolve(bool isText)
    {
        using var data = JsonDocument.Parse("""{ "m": 1e400 }""");

        var (value, issues) = SmsTemplates.Resolve(isText ? "Value {{m}}" : "{{m}}", data.RootElement, isText);

        Assert.Null(value);
        var issue = JsonSerializer.SerializeToElement(Assert.Single(issues));
        Assert.Equal("sms.value_invalid", issue.GetProperty("code").GetString());
        Assert.Equal("m", issue.GetProperty("field").GetString());
    }

    [Fact]
    public async Task A_value_containing_template_braces_stays_literal()
    {
        // A value is inserted as plain text once: a {{...}} inside it is never expanded again.
        var data = Data();
        data["customer"]!["name"] = "{{customer.mobile}}";

        var (_, done) = await RunAsync(Sms(text: "Hi {{customer.name}}"), data);

        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal("Hi {{customer.mobile}}", Assert.Single(_sms.Requests).Text);
    }

    [Theory]
    [InlineData("sms.country_not_allowed", "to")]
    [InlineData("sms.recipient_invalid", "to")]
    [InlineData("sms.text_invalid", "text")]
    public async Task Number_outside_allowed_country_from_the_service_is_rejected_as_data(string code, string field)
    {
        _sms.Failure = SmsSendResult.Failure(code, outcomeUnknown: false);

        var (id, done) = await RunAsync(Sms());

        AssertRejected(done, calls: 1); // Rejected, not Failed: the data's fault, nothing sent
        var issue = Assert.Single(Step(done, "sms").GetProperty("result").GetProperty("issues").EnumerateArray());
        Assert.Equal(code, issue.GetProperty("code").GetString());
        Assert.Equal(field, issue.GetProperty("field").GetString());
        Assert.Empty(await SmsAuditAsync(id));
    }

    [Fact]
    public async Task Known_failure_fails_the_run_without_retry()
    {
        _sms.Failure = SmsSendResult.Failure("sms.rate_limited", outcomeUnknown: false);

        var (id, done) = await RunAsync(Sms());

        await AssertFailedWithoutRetryAsync(id, done, "sms.rate_limited", calls: 1);
        Assert.Contains(SmsSendMessages.For("sms.rate_limited"), done.GetProperty("failureMessage").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, Step(done, "sms").GetProperty("attempt").GetInt32());
    }

    [Fact]
    public async Task Unknown_outcome_fails_with_the_warning()
    {
        _sms.Failure = SmsSendResult.Failure("sms.server_error", outcomeUnknown: true);

        var (id, done) = await RunAsync(Sms());

        await AssertFailedWithoutRetryAsync(id, done, "sms.server_error", calls: 1);
        Assert.Contains("outcome is unknown", done.GetProperty("failureMessage").GetString(), StringComparison.Ordinal);
        Assert.Equal(Unknown, Step(done, "sms").GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_unexpected_exception_from_the_service_fails_unknown_without_retry_and_without_its_text()
    {
        _sms.Throw = new InvalidOperationException("secret provider text " + MobileDigits);

        var (id, done) = await RunAsync(Sms());

        await AssertFailedWithoutRetryAsync(id, done, "sms.unreachable", calls: 1);
        Assert.Equal(Unknown, Step(done, "sms").GetProperty("error").GetString());
        Assert.DoesNotContain("secret", done.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(MobileDigits, done.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Operator_retry_sends_once_more()
    {
        _sms.Failure = SmsSendResult.Failure("sms.server_error", outcomeUnknown: true);
        _sms.FailTimes = 1;
        var (id, failed) = await RunAsync(Sms());
        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Equal(1, _sms.CallCount);

        await RetryAsync(id);
        await RunEngineAsync(id);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(2, _sms.CallCount);
        Assert.Equal(_sms.Requests[0], _sms.Requests[1]); // the same message, the same tag
        Assert.Equal(1, _printer.Calls);
    }

    [Fact]
    public async Task A_send_step_found_running_is_not_run_again()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(smsConfig: Sms());
        var id = await Kit.SubmitNewAsync(key, Data());

        // The state a killed Worker leaves: the steps before the send done, the send started (attempt 1) and never recorded as finished, the run's lock expired.
        var now = DateTimeOffset.UtcNow;
        await Kit.WithDbAsync(async db =>
        {
            await db.ExecutionSteps.Where(s => s.ExecutionId == id && s.NodeId == "trigger").ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, StepStatus.Succeeded).SetProperty(x => x.OutputPort, "out").SetProperty(x => x.Attempt, 1)
                .SetProperty(x => x.StartedAt, now.AddSeconds(-30)).SetProperty(x => x.CompletedAt, now.AddSeconds(-30)));
            await db.ExecutionSteps.Where(s => s.ExecutionId == id && s.NodeId == "validate").ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, StepStatus.Succeeded).SetProperty(x => x.OutputPort, "valid").SetProperty(x => x.Attempt, 1)
                .SetProperty(x => x.StartedAt, now.AddSeconds(-29)).SetProperty(x => x.CompletedAt, now.AddSeconds(-29)));
            await db.ExecutionSteps.Where(s => s.ExecutionId == id && s.NodeId == "sms").ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, StepStatus.Running).SetProperty(x => x.Attempt, 1).SetProperty(x => x.StartedAt, now.AddSeconds(-28)));
            return await db.Executions.Where(e => e.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, ExecutionStatus.Running).SetProperty(e => e.StartedAt, now.AddSeconds(-30))
                .SetProperty(e => e.LockedUntil, now.AddSeconds(-1)));
        });

        await RunEngineAsync(id);

        var failed = await Kit.GetExecutionAsync(id);
        Assert.Equal(0, _sms.CallCount); // not sent again
        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Equal("sms.outcome_unknown", failed.GetProperty("failureCode").GetString());
        Assert.Equal(
            "Step 'Send SMS' was started but never recorded as finished (the Worker stopped or crashed): the message may or may not have been sent. Check the SMS provider's log before retrying.",
            failed.GetProperty("failureMessage").GetString());
        var step = Step(failed, "sms");
        Assert.Equal("Failed", step.GetProperty("status").GetString());
        Assert.Equal(Unknown, step.GetProperty("error").GetString());
        var events = await EventTypesAsync(id);
        Assert.Contains(ExecutionEventTypes.StepFailed, events);
        Assert.Equal(ExecutionEventTypes.Failed, events[^1]);
        Assert.DoesNotContain(ExecutionEventTypes.RetryScheduled, events);
        Assert.DoesNotContain(ExecutionEventTypes.StepStarted, events);
        Assert.Empty(_jobs.Scheduled);
        Assert.Equal(0, _printer.Calls);

        // An operator retry is a person's decision: it sends once.
        await RetryAsync(id);
        await RunEngineAsync(id);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(1, _sms.CallCount);
    }

    [Fact]
    public async Task Shutdown_during_a_send_leaves_it_running_and_the_next_run_fails_unknown()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(smsConfig: Sms());
        var id = await Kit.SubmitNewAsync(key, Data());

        using var shutdown = new CancellationTokenSource();
        _sms.During = (_, _) => shutdown.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunEngineAsync(id, ct: shutdown.Token));
        _sms.During = null;

        var stopped = await Kit.GetExecutionAsync(id);
        Assert.Equal("Running", stopped.GetProperty("status").GetString());
        var step = Step(stopped, "sms");
        Assert.Equal("Running", step.GetProperty("status").GetString()); // not put back to Pending: the message may have left
        Assert.Equal(1, step.GetProperty("attempt").GetInt32());
        Assert.Null(await Kit.WithDbAsync(db => db.Executions.Where(e => e.Id == id).Select(e => e.LockedUntil).SingleAsync()));
        Assert.Equal(1, _sms.CallCount);

        await RunEngineAsync(id); // the redelivered job does not send it again

        var failed = await Kit.GetExecutionAsync(id);
        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Equal("sms.outcome_unknown", failed.GetProperty("failureCode").GetString());
        Assert.Equal(1, _sms.CallCount);
        Assert.Empty(_jobs.Scheduled);
        Assert.Equal(0, _printer.Calls);
    }

    [Fact]
    public async Task Cancellation_from_the_service_is_treated_as_unknown_never_retried()
    {
        // A cancellation that is neither the Worker's shutdown nor the run's time limit (a TaskCanceledException from inside the HTTP stack, say) may have
        // come after the message left: outcome unknown, final.
        _sms.Throw = new TaskCanceledException();

        var (id, done) = await RunAsync(Sms());

        await AssertFailedWithoutRetryAsync(id, done, "sms.unreachable", calls: 1);
        Assert.Equal(Unknown, Step(done, "sms").GetProperty("error").GetString());
        Assert.Contains("outcome is unknown", done.GetProperty("failureMessage").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Step_timeout_is_a_non_retryable_unknown_failure()
    {
        // The connection's own deadline: the service reports sms.timeout with an unknown outcome. Final, never retried.
        _sms.Failure = SmsSendResult.Failure("sms.timeout", outcomeUnknown: true);

        var (id, done) = await RunAsync(Sms());

        await AssertFailedWithoutRetryAsync(id, done, "sms.timeout", calls: 1);
        Assert.Equal(Unknown, Step(done, "sms").GetProperty("error").GetString());
    }

    [Fact]
    public void Run_time_limit_during_a_send_is_a_non_retryable_unknown_failure()
    {
        // The run's 5-minute limit has no seam to shorten it in an engine test, so the classification is checked directly.
        using var limit = new CancellationTokenSource();
        limit.Cancel();

        var (code, message, retryable) = ExecutionEngine.Describe(new OperationCanceledException(), "send-sms", limit.Token);
        Assert.Equal("execution.timeout", code);
        Assert.False(retryable);
        Assert.Equal($"The run exceeded its time limit of 5 minutes while the message was being sent. {Unknown}", message);

        var surprise = ExecutionEngine.Describe(new OperationCanceledException(), "send-sms", CancellationToken.None);
        Assert.Equal(("sms.unreachable", Unknown, false), surprise);

        // The write step's wording is unchanged.
        Assert.Equal(("execution.timeout", $"The run exceeded its time limit of 5 minutes while the statement was running. {DatabaseQueryMessages.OutcomeUnknown}", false),
            ExecutionEngine.Describe(new OperationCanceledException(), "write-database", limit.Token));
        Assert.Equal(("db.query_failed", DatabaseQueryMessages.OutcomeUnknown, false),
            ExecutionEngine.Describe(new InvalidOperationException("x"), "write-database", CancellationToken.None));

        // Other steps keep their retryable time limit.
        Assert.True(ExecutionEngine.Describe(new OperationCanceledException(), "generate-pdf", limit.Token).Retryable);
    }

    [Fact]
    public async Task Resume_after_approval_does_not_repeat_a_succeeded_send()
    {
        // validate → sms → approval → pdf → archive
        var key = await Kit.PublishInvoiceWorkflowAsync(smsConfig: Sms(), approvalConfig: new JsonObject { ["approver"] = "operator", ["escalateAfter"] = "24h" });
        var id = await Kit.SubmitNewAsync(key, Data());
        await RunEngineAsync(id);
        Assert.Equal("AwaitingApproval", (await Kit.GetExecutionAsync(id)).GetProperty("status").GetString());
        Assert.Equal(1, _sms.CallCount);

        using (var approver = fixture.Factory.CreateClientAs("dev.operator"))
        using (var response = await approver.PostAsJsonAsync(new Uri($"/api/v1/executions/{id}/approvals/approve", UriKind.Relative), new { decision = "approved", comment = "ok" }))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        await RunEngineAsync(id);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(1, _sms.CallCount);
        var step = Step(done, "sms");
        Assert.Equal(1, step.GetProperty("attempt").GetInt32());
        Assert.Equal("44111", step.GetProperty("result").GetProperty("messageId").GetString()); // its record is kept as it was
        Assert.Single(await SmsAuditAsync(id));
        Assert.Equal(1, _printer.Calls);
    }

    private static JsonObject PendingQuery() => new()
    {
        ["connection"] = "crm-db",
        ["sql"] = "SELECT id, label FROM pending WHERE processed = 0",
        ["parameters"] = "",
        ["resultName"] = "pending",
        ["maxRows"] = 10,
        ["timeoutSeconds"] = 20,
    };

    private static IReadOnlyDictionary<string, object?> Pending(string id, string? label = null) =>
        FakeDatabaseQuery.Row(("id", id), ("label", label ?? "row " + id));

    /// <summary>
    /// Query database (rows 1, 2, 3) → For each row → Send SMS (to +3579900000{{row.id}}) → PDF → Archive. The first pass sends for items 1 and 2, then the PDF of
    /// item 2 fails on a storage outage (a technical failure: an automatic retry is scheduled, the run is not over).
    /// </summary>
    private async Task<Guid> FirstPassStopsAtThePdfOfItemTwoAsync()
    {
        _query.Rows = [Pending("1"), Pending("2"), Pending("3")];
        var key = await Kit.PublishInvoiceWorkflowAsync(
            queryConfig: PendingQuery(),
            forEachConfig: new JsonObject { ["field"] = "pending", ["mode"] = "each", ["item"] = "row" },
            smsConfig: Sms(to: "+3579900000{{row.id}}", text: "Item {{row.label}}"), smsAt: "in-loop");
        var id = await Kit.SubmitNewAsync(key, Data());
        _sms.During = (call, _) => { if (call == 2) { _store.FailWrites = true; } };
        await RunEngineAsync(id);
        _sms.During = null;
        _store.FailWrites = false;

        var paused = await Kit.GetExecutionAsync(id);
        Assert.Single(_jobs.Scheduled);
        Assert.Equal(["35799000001", "35799000002"], _sms.Requests.Select(r => r.RecipientDigits));
        Assert.Equal([$"verso-{id.ToString("N")[..12]}-1", $"verso-{id.ToString("N")[..12]}-2"], _sms.Requests.Select(r => r.Tag));
        var loop = Step(paused, "loop").GetProperty("result");
        Assert.Equal(["count", "itemsFingerprint"], loop.EnumerateObject().Select(p => p.Name));
        return id;
    }

    [Fact]
    public async Task Loop_with_a_send_fails_loop_items_changed_when_the_list_changed()
    {
        var id = await FirstPassStopsAtThePdfOfItemTwoAsync();
        var printed = _printer.Calls;
        _query.Rows = [Pending("3")]; // by position, 3 would land on item 1, whose send already succeeded

        await RunEngineAsync(id);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Failed", done.GetProperty("status").GetString());
        Assert.Equal("loop.items_changed", done.GetProperty("failureCode").GetString());
        var message = done.GetProperty("failureMessage").GetString()!;
        Assert.Contains("For each line", message, StringComparison.Ordinal);
        Assert.Contains("Send SMS", message, StringComparison.Ordinal);
        Assert.Contains("Check the SMS provider's log", message, StringComparison.Ordinal);
        Assert.Equal(2, _sms.CallCount); // nothing sent again, nothing sent for the new list
        Assert.Equal(printed, _printer.Calls);
        Assert.Single(_jobs.Scheduled);
    }

    [Fact]
    public async Task Loop_with_a_send_over_an_unchanged_list_resumes_without_repeating_a_send()
    {
        var id = await FirstPassStopsAtThePdfOfItemTwoAsync();

        await RunEngineAsync(id);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(["35799000001", "35799000002", "35799000003"], _sms.Requests.Select(r => r.RecipientDigits));
        Assert.Equal([1, 2, 3], (await SmsAuditAsync(id)).Select(d => JsonDocument.Parse(d!).RootElement.GetProperty("iteration").GetInt32()));
    }

    [Fact]
    public async Task Audit_SmsSent_has_exactly_the_specified_fields_and_none_for_failures()
    {
        var (id, done) = await RunAsync(Sms());

        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        var entry = await Kit.WithDbAsync(db => db.AuditEntries.Where(a => a.EntityId == id.ToString() && a.Action == "SmsSent").SingleAsync());
        Assert.Equal("verso.worker", entry.ActorId);
        Assert.Equal("Execution", entry.EntityType);
        Assert.Equal(done.GetProperty("correlationId").GetString(), entry.TraceId);
        using var details = JsonDocument.Parse(entry.Details!);
        var root = details.RootElement;
        Assert.Equal(["executionId", "nodeId", "iteration", "connection", "messageId", "smsParts"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(id, root.GetProperty("executionId").GetGuid());
        Assert.Equal("sms", root.GetProperty("nodeId").GetString());
        Assert.Equal(0, root.GetProperty("iteration").GetInt32());
        Assert.Equal("brevo-sms", root.GetProperty("connection").GetString());
        Assert.Equal("44111", root.GetProperty("messageId").GetString());
        Assert.Equal(1, root.GetProperty("smsParts").GetInt32());
        Assert.DoesNotContain(MobileDigits, entry.Details!, StringComparison.Ordinal);
        Assert.DoesNotContain(TextMarker, entry.Details!, StringComparison.Ordinal);
        Assert.DoesNotContain("verso-", entry.Details!, StringComparison.Ordinal); // no tag

        // Failures (known, unknown, data) are not audited as sent.
        _sms.Failure = SmsSendResult.Failure("sms.server_error", outcomeUnknown: true);
        var (unknown, _) = await RunAsync(Sms());
        _sms.Failure = SmsSendResult.Failure("sms.unauthorized", outcomeUnknown: false);
        var (known, _) = await RunAsync(Sms());
        _sms.Failure = null;
        var (rejected, _) = await RunAsync(Sms(), Data(mobile: null));
        foreach (var other in new[] { unknown, known, rejected })
        {
            Assert.Empty(await SmsAuditAsync(other));
        }
    }

    [Fact]
    public async Task Number_text_and_data_never_appear_in_logs_events_audit_or_results()
    {
        var digits = "3579" + Random.Shared.Next(1000000, 9999999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var textMarker = "SMSTXT" + Guid.NewGuid().ToString("N")[..12];
        var dataMarker = "SMSDATA" + Guid.NewGuid().ToString("N")[..12];
        var key = await Kit.PublishInvoiceWorkflowAsync(smsConfig: Sms());
        JsonNode Marked(string? mobile = "+placeholder")
        {
            var data = Data(mobile: mobile is null ? null : "+" + digits, poNumber: textMarker);
            data["payment"]!["terms"] = dataMarker; // a submitted value that is not used by the step must not be logged either
            return data;
        }

        // A sent run, a run whose outcome is unknown, a run rejected by the data, a run rejected by the service, a run whose service throws, and a run found
        // with its send still running.
        var sent = await Kit.SubmitNewAsync(key, Marked());
        await RunEngineAsync(sent, captureLogs: true);

        var unknown = await Kit.SubmitNewAsync(key, Marked());
        _sms.Failure = SmsSendResult.Failure("sms.server_error", outcomeUnknown: true);
        await RunEngineAsync(unknown, captureLogs: true);

        var refused = await Kit.SubmitNewAsync(key, Marked());
        _sms.Failure = SmsSendResult.Failure("sms.country_not_allowed", outcomeUnknown: false);
        await RunEngineAsync(refused, captureLogs: true);
        _sms.Failure = null;

        var rejected = await Kit.SubmitNewAsync(key, Marked(mobile: null));
        await RunEngineAsync(rejected, captureLogs: true);

        var threw = await Kit.SubmitNewAsync(key, Marked());
        _sms.Throw = new InvalidOperationException($"provider said {digits} {textMarker}");
        await RunEngineAsync(threw, captureLogs: true);
        _sms.Throw = null;

        var stopped = await Kit.SubmitNewAsync(key, Marked());
        using (var shutdown = new CancellationTokenSource())
        {
            _sms.During = (_, _) => shutdown.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunEngineAsync(stopped, captureLogs: true, ct: shutdown.Token));
            _sms.During = null;
        }

        await RunEngineAsync(stopped, captureLogs: true);

        Assert.Contains(_sms.Requests, r => r.RecipientDigits == digits && r.Text.Contains(textMarker, StringComparison.Ordinal)); // the values were used
        foreach (var id in new[] { sent, unknown, refused, rejected, threw, stopped })
        {
            var execution = await Kit.GetExecutionAsync(id);
            using var events = await _client.GetAsync(new Uri($"/api/v1/executions/{id}/events", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, events.StatusCode);
            var (rows, audit) = await Kit.WithDbAsync(async db =>
            {
                var correlation = await db.Executions.Where(e => e.Id == id).Select(e => e.CorrelationId).SingleAsync();
                var eventData = await db.ExecutionEvents.Where(e => e.ExecutionId == id).Select(e => e.Type + " " + e.Data).ToListAsync();
                var auditDetails = await db.AuditEntries.Where(a => a.EntityId == id.ToString() || a.TraceId == correlation).Select(a => a.Action + " " + a.Details).ToListAsync();
                return (string.Join('\n', eventData), string.Join('\n', auditDetails));
            });

            Assert.NotEmpty(rows);
            Assert.Equal(id == sent, audit.Contains("SmsSent ", StringComparison.Ordinal));
            foreach (var text in new[] { execution.GetRawText(), await events.Content.ReadAsStringAsync(), rows, audit })
            {
                Assert.DoesNotContain(digits, text, StringComparison.Ordinal);
                Assert.DoesNotContain(textMarker, text, StringComparison.Ordinal);
                Assert.DoesNotContain(dataMarker, text, StringComparison.Ordinal);
            }
        }

        var leaks = fixture.Factory.Logs.Entries.Where(e => e.AllText.Contains(digits, StringComparison.Ordinal) || e.AllText.Contains(textMarker, StringComparison.Ordinal)
                                                            || e.AllText.Contains(dataMarker, StringComparison.Ordinal)).ToList();
        Assert.True(leaks.Count == 0, "SMS values in logs:\n" + string.Join("\n", leaks.Take(5).Select(l => $"[{l.Category}] {l.Message}")));
        Assert.Contains(fixture.Factory.Logs.Entries, e => e.Category.Contains("ExecutionEngine", StringComparison.Ordinal)
                                                           && e.Scope.TryGetValue("ExecutionId", out var x) && x == stopped.ToString());
    }
}
