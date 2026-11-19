using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Verso.Application.Connections;
using Verso.Domain.Executions;

namespace Verso.Api.Tests;

/// <summary>
/// The Write to database step in a real run (engine side): parameters come from the run data, the database is called at most once per run attempt, a failure of
/// any kind is never retried automatically, a write that was started but never recorded as finished (a crash, a Worker stop) is never run again by the engine,
/// a succeeded write is never repeated on resume, and no parameter value or SQL text reaches history, events, audit or logs. Only an operator retry runs a
/// write again. The database is a <see cref="FakeDatabaseWrite"/>; the real runners are covered by DatabaseWriteTests.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class WriteDatabaseExecutionTests(MigratedDatabaseFixture fixture) : IDisposable
{
    /// <summary>A value that exists only in the submitted data and is used as a parameter.</summary>
    private const string ParameterMarker = "PO-WMARK-4417";

    /// <summary>A table name that exists only in the step's SQL.</summary>
    private const string SqlMarker = "ledger_wmark_entries";

    private const string Sql = "UPDATE " + SqlMarker + " SET po_ref = @ref WHERE number = @num";

    private readonly HttpClient _client = fixture.Factory.CreateClient();
    private readonly TestDocumentStore _store = new();
    private readonly FakePdfPrinter _printer = new();
    private readonly RecordingExecutionJobs _jobs = new();
    private readonly FakeDatabaseWrite _write = new();
    private ExecutionTestKit Kit => new(fixture.Factory, _client);

    public void Dispose()
    {
        _store.Dispose();
        _client.Dispose();
    }

    private static JsonObject Write(string sql = Sql, string parameters = "ref = invoice.poNumber\nnum = invoice.number", int maxRows = 1) => new()
    {
        ["connection"] = "ledger-db",
        ["sql"] = sql,
        ["parameters"] = parameters,
        ["maxRows"] = maxRows,
        ["timeoutSeconds"] = 20,
    };

    private static JsonNode Data(string? poNumber = ParameterMarker)
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

    private static JsonElement Step(JsonElement execution, string nodeId, int iteration = 0) =>
        execution.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("nodeId").GetString() == nodeId && s.GetProperty("iteration").GetInt32() == iteration);

    private Task RunEngineAsync(Guid id, bool captureLogs = false, CancellationToken ct = default) =>
        Kit.RunEngineAsync(id, _printer, _store, _jobs, ct: ct, write: _write, captureLogs: captureLogs);

    private async Task<(Guid Id, JsonElement Done)> RunAsync(JsonObject config, JsonNode? data = null)
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(writeConfig: config);
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

    /// <summary>The run failed for good: Failed, the write step Failed, no retry scheduled or announced, the database called <paramref name="calls"/> times.</summary>
    private async Task AssertFailedWithoutRetryAsync(Guid id, JsonElement done, string code, int calls)
    {
        Assert.Equal("Failed", done.GetProperty("status").GetString());
        Assert.Equal(code, done.GetProperty("failureCode").GetString());
        Assert.Equal("Failed", Step(done, "write").GetProperty("status").GetString());
        Assert.Equal(calls, _write.CallCount);
        Assert.Empty(_jobs.Scheduled);
        Assert.DoesNotContain(ExecutionEventTypes.RetryScheduled, await EventTypesAsync(id));
        Assert.Equal("Pending", Step(done, "pdf").GetProperty("status").GetString()); // nothing after the write ran
        Assert.Equal(0, _printer.Calls);
    }

    [Fact]
    public async Task Written_calls_the_database_once_and_data_is_unchanged()
    {
        _write.RowsAffected = 1;

        var (_, done) = await RunAsync(Write());

        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        var request = Assert.Single(_write.Requests);
        Assert.Equal("ledger-db", request.ConnectionKey);
        Assert.Equal(Sql, request.Sql);
        Assert.Equal(ParameterMarker, request.Parameters["ref"]);
        Assert.Equal("INV-2026-003901", request.Parameters["num"]);
        Assert.Equal(1, request.MaxRows);
        Assert.Equal(TimeSpan.FromSeconds(20), request.Timeout);

        var step = Step(done, "write");
        Assert.Equal("written", step.GetProperty("outputPort").GetString());
        Assert.Equal(1, step.GetProperty("attempt").GetInt32());
        var result = step.GetProperty("result");
        Assert.Equal(["rowsAffected", "durationMs", "connection"], result.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, result.GetProperty("rowsAffected").GetInt32());
        Assert.Equal(9, result.GetProperty("durationMs").GetInt64());
        Assert.Equal("ledger-db", result.GetProperty("connection").GetString());

        // The data is unchanged: the document and the archive saw the submitted record.
        Assert.Equal("Succeeded", Step(done, "pdf").GetProperty("status").GetString());
        var archive = Assert.Single(done.GetProperty("documents").EnumerateArray()).GetProperty("archive").GetProperty("metadata");
        Assert.Equal("Kestrel Logistics Ltd", archive.GetProperty("Customer").GetString());

        // No parameter value and no SQL anywhere in the execution (the archive metadata holds the invoice number and customer name on purpose; neither is the marker).
        var json = done.GetRawText();
        Assert.DoesNotContain(ParameterMarker, json, StringComparison.Ordinal);
        Assert.DoesNotContain(SqlMarker, json, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_parameter_rejects_without_calling_the_database()
    {
        var (_, done) = await RunAsync(Write(), Data(poNumber: null));

        Assert.Equal(0, _write.CallCount);
        Assert.Equal("Rejected", done.GetProperty("status").GetString());
        Assert.Equal("data.write_invalid", done.GetProperty("failureCode").GetString());
        Assert.Contains("Write to database", done.GetProperty("failureMessage").GetString(), StringComparison.Ordinal);
        var step = Step(done, "write");
        Assert.Equal("failed", step.GetProperty("outputPort").GetString());
        var issue = Assert.Single(step.GetProperty("result").GetProperty("issues").EnumerateArray());
        Assert.Equal("query.parameter_missing", issue.GetProperty("code").GetString());
        var message = issue.GetProperty("message").GetString()!;
        Assert.Contains("ref", message, StringComparison.Ordinal);
        Assert.Contains("invoice.poNumber", message, StringComparison.Ordinal);
        Assert.DoesNotContain("INV-2026-003901", done.GetProperty("failureMessage").GetString() + message, StringComparison.Ordinal); // the other parameter's value
        Assert.Equal(0, _printer.Calls);
    }

    [Fact]
    public async Task Known_failure_fails_the_run_without_retry()
    {
        _write.Failure = DatabaseWriteResult.Failure("db.query_failed", outcomeUnknown: false);

        var (id, done) = await RunAsync(Write());

        await AssertFailedWithoutRetryAsync(id, done, "db.query_failed", calls: 1);
        Assert.Contains(DatabaseQueryMessages.For("db.query_failed"), done.GetProperty("failureMessage").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, Step(done, "write").GetProperty("attempt").GetInt32());
    }

    [Fact]
    public async Task Unknown_outcome_fails_the_run_with_the_warning()
    {
        _write.Failure = DatabaseWriteResult.Failure("db.timeout", outcomeUnknown: true);

        var (id, done) = await RunAsync(Write());

        await AssertFailedWithoutRetryAsync(id, done, "db.timeout", calls: 1);
        Assert.Contains("outcome is unknown", done.GetProperty("failureMessage").GetString(), StringComparison.Ordinal);
        Assert.Contains("outcome is unknown", Step(done, "write").GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Too_many_rows_fails_without_retry()
    {
        _write.Failure = DatabaseWriteResult.Failure("db.too_many_rows_affected", outcomeUnknown: false);

        var (id, done) = await RunAsync(Write());

        await AssertFailedWithoutRetryAsync(id, done, "db.too_many_rows_affected", calls: 1);
        Assert.Contains(DatabaseQueryMessages.For("db.too_many_rows_affected"), done.GetProperty("failureMessage").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writes_not_allowed_fails_without_retry()
    {
        _write.Failure = DatabaseWriteResult.Failure("db.writes_not_allowed", outcomeUnknown: false);

        var (id, done) = await RunAsync(Write());

        await AssertFailedWithoutRetryAsync(id, done, "db.writes_not_allowed", calls: 1);
    }

    [Fact]
    public async Task An_unexpected_exception_from_the_write_service_fails_unknown_without_retry_and_without_its_text()
    {
        _write.Throw = new InvalidOperationException("secret server text Server=db01;User=sa");

        var (id, done) = await RunAsync(Write());

        await AssertFailedWithoutRetryAsync(id, done, "db.query_failed", calls: 1);
        Assert.Contains("outcome is unknown", done.GetProperty("failureMessage").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", done.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Operator_retry_runs_the_write_once_more()
    {
        _write.Failure = DatabaseWriteResult.Failure("db.timeout", outcomeUnknown: true);
        _write.FailTimes = 1;
        var (id, failed) = await RunAsync(Write());
        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Equal(1, _write.CallCount);

        // A person decides to run it again (after checking the database): the retry endpoint, then the next job.
        await RetryAsync(id);
        await RunEngineAsync(id);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(2, _write.CallCount);
        Assert.Equal(_write.Requests[0].Parameters, _write.Requests[1].Parameters);
        Assert.Equal(1, Step(done, "write").GetProperty("attempt").GetInt32()); // a fresh attempt budget, one call
        Assert.Equal(1, _printer.Calls);
    }

    [Fact]
    public async Task A_write_step_found_running_is_not_run_again()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(writeConfig: Write());
        var id = await Kit.SubmitNewAsync(key, Data());

        // The state a killed Worker leaves: the steps before the write done, the write started (attempt 1) and never recorded as finished, the run's lock expired.
        var now = DateTimeOffset.UtcNow;
        await Kit.WithDbAsync(async db =>
        {
            await db.ExecutionSteps.Where(s => s.ExecutionId == id && s.NodeId == "trigger").ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, StepStatus.Succeeded).SetProperty(x => x.OutputPort, "out").SetProperty(x => x.Attempt, 1)
                .SetProperty(x => x.StartedAt, now.AddSeconds(-30)).SetProperty(x => x.CompletedAt, now.AddSeconds(-30)));
            await db.ExecutionSteps.Where(s => s.ExecutionId == id && s.NodeId == "validate").ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, StepStatus.Succeeded).SetProperty(x => x.OutputPort, "valid").SetProperty(x => x.Attempt, 1)
                .SetProperty(x => x.StartedAt, now.AddSeconds(-29)).SetProperty(x => x.CompletedAt, now.AddSeconds(-29)));
            await db.ExecutionSteps.Where(s => s.ExecutionId == id && s.NodeId == "write").ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, StepStatus.Running).SetProperty(x => x.Attempt, 1).SetProperty(x => x.StartedAt, now.AddSeconds(-28)));
            return await db.Executions.Where(e => e.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, ExecutionStatus.Running).SetProperty(e => e.StartedAt, now.AddSeconds(-30))
                .SetProperty(e => e.LockedUntil, now.AddSeconds(-1)));
        });

        await RunEngineAsync(id);

        var failed = await Kit.GetExecutionAsync(id);
        Assert.Equal(0, _write.CallCount); // not run again
        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Equal("write.outcome_unknown", failed.GetProperty("failureCode").GetString());
        Assert.Equal(
            "Step 'Write to database' was started but never recorded as finished (the Worker stopped or crashed): the statement may or may not have changed data. Check the database before retrying.",
            failed.GetProperty("failureMessage").GetString());
        var step = Step(failed, "write");
        Assert.Equal("Failed", step.GetProperty("status").GetString());
        Assert.Contains("outcome is unknown", step.GetProperty("error").GetString(), StringComparison.Ordinal);
        var events = await EventTypesAsync(id);
        Assert.Contains(ExecutionEventTypes.StepFailed, events);
        Assert.Equal(ExecutionEventTypes.Failed, events[^1]);
        Assert.DoesNotContain(ExecutionEventTypes.RetryScheduled, events);
        Assert.DoesNotContain(ExecutionEventTypes.StepStarted, events); // nothing was started by this run
        Assert.Empty(_jobs.Scheduled);
        Assert.Equal(0, _printer.Calls);

        // An operator retry is a person's decision: it runs the write once.
        await RetryAsync(id);
        await RunEngineAsync(id);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(1, _write.CallCount);
    }

    [Fact]
    public async Task Shutdown_during_a_write_leaves_it_running_and_the_next_run_fails_unknown()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(writeConfig: Write());
        var id = await Kit.SubmitNewAsync(key, Data());

        // The Worker is told to stop while the statement runs.
        using var shutdown = new CancellationTokenSource();
        _write.During = (_, _) => shutdown.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunEngineAsync(id, ct: shutdown.Token));
        _write.During = null;

        var stopped = await Kit.GetExecutionAsync(id);
        Assert.Equal("Running", stopped.GetProperty("status").GetString());
        var step = Step(stopped, "write");
        Assert.Equal("Running", step.GetProperty("status").GetString()); // not put back to Pending: the statement may have run
        Assert.Equal(1, step.GetProperty("attempt").GetInt32());
        Assert.Null(await Kit.WithDbAsync(db => db.Executions.Where(e => e.Id == id).Select(e => e.LockedUntil).SingleAsync())); // the lock is released
        Assert.Equal(1, _write.CallCount);

        // The redelivered job does not run it again.
        await RunEngineAsync(id);

        var failed = await Kit.GetExecutionAsync(id);
        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Equal("write.outcome_unknown", failed.GetProperty("failureCode").GetString());
        Assert.Equal("Failed", Step(failed, "write").GetProperty("status").GetString());
        Assert.Equal(1, _write.CallCount);
        Assert.Empty(_jobs.Scheduled);
        Assert.Equal(0, _printer.Calls);
    }

    [Fact]
    public async Task Shutdown_in_another_step_still_puts_it_back_to_pending()
    {
        // The write-step exception to the shutdown rule changes nothing for other steps: a PDF step stopped mid-way is Pending again, attempt not counted.
        var key = await Kit.PublishInvoiceWorkflowAsync(writeConfig: Write());
        var id = await Kit.SubmitNewAsync(key, Data());
        using var shutdown = new CancellationTokenSource();
        _store.AfterWrite = k => { if (k.StartsWith("staging/", StringComparison.Ordinal)) { shutdown.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunEngineAsync(id, ct: shutdown.Token));
        _store.AfterWrite = null;

        var stopped = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", Step(stopped, "write").GetProperty("status").GetString());
        Assert.Equal("Pending", Step(stopped, "pdf").GetProperty("status").GetString());
        Assert.Equal(0, Step(stopped, "pdf").GetProperty("attempt").GetInt32());

        await RunEngineAsync(id);

        Assert.Equal("Succeeded", (await Kit.GetExecutionAsync(id)).GetProperty("status").GetString());
        Assert.Equal(1, _write.CallCount); // the succeeded write was not repeated on resume
    }

    [Fact]
    public async Task Step_timeout_during_a_write_is_a_non_retryable_unknown_failure()
    {
        // A cancellation that is not the Worker stopping (the run's time limit; any other step would treat it as a retryable timeout).
        // The fake throws it while the statement "runs"; the run's real 5-minute limit takes the same branch with the code execution.timeout.
        _write.Throw = new OperationCanceledException();

        var (id, done) = await RunAsync(Write());

        await AssertFailedWithoutRetryAsync(id, done, "db.query_failed", calls: 1);
        Assert.Contains("outcome is unknown", done.GetProperty("failureMessage").GetString(), StringComparison.Ordinal);
        Assert.Contains("outcome is unknown", Step(done, "write").GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resume_after_approval_does_not_repeat_a_succeeded_write()
    {
        // validate → write → approval → pdf → archive
        var key = await Kit.PublishInvoiceWorkflowAsync(writeConfig: Write(), approvalConfig: new JsonObject { ["approver"] = "operator", ["escalateAfter"] = "24h" });
        var id = await Kit.SubmitNewAsync(key, Data());
        await RunEngineAsync(id);
        Assert.Equal("AwaitingApproval", (await Kit.GetExecutionAsync(id)).GetProperty("status").GetString());
        Assert.Equal(1, _write.CallCount);

        using (var approver = fixture.Factory.CreateClientAs("dev.operator"))
        using (var response = await approver.PostAsJsonAsync(new Uri($"/api/v1/executions/{id}/approvals/approve", UriKind.Relative), new { decision = "approved", comment = "ok" }))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        await RunEngineAsync(id);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(1, _write.CallCount);
        var step = Step(done, "write");
        Assert.Equal(1, step.GetProperty("attempt").GetInt32());
        Assert.Equal(1, step.GetProperty("result").GetProperty("rowsAffected").GetInt32()); // its record is kept as it was
        Assert.Equal(1, _printer.Calls);
    }

    [Fact]
    public async Task Inside_for_each_completed_items_are_not_repeated_on_resume()
    {
        // loop (each line) → write → pdf → archive, three lines. The Worker stops while item 2's PDF is being made (after item 2's write succeeded).
        var key = await Kit.PublishInvoiceWorkflowAsync(
            writeConfig: Write(sql: "UPDATE " + SqlMarker + " SET seen = 1 WHERE line_text = @line", parameters: "line = line.description"), writeAt: "in-loop",
            forEachConfig: new JsonObject { ["field"] = "lines", ["mode"] = "each", ["item"] = "line" });
        var id = await Kit.SubmitNewAsync(key, Data());
        using var shutdown = new CancellationTokenSource();
        var staged = 0;
        _store.AfterWrite = k => { if (k.StartsWith("staging/", StringComparison.Ordinal) && ++staged == 2) { shutdown.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunEngineAsync(id, ct: shutdown.Token));
        _store.AfterWrite = null;

        var stopped = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", Step(stopped, "write", 2).GetProperty("status").GetString());
        Assert.Equal("Pending", Step(stopped, "pdf", 2).GetProperty("status").GetString());
        Assert.Equal(2, _write.CallCount);

        await RunEngineAsync(id);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        // One call per item, in order: items 1 and 2 were not written again on resume.
        Assert.Equal(
            ["Electricity supply, October 2026 (commercial tariff)", "Reactive power surcharge", "Meter rental"],
            _write.Requests.Select(r => (string?)r.Parameters["line"]));
        Assert.All(Enumerable.Range(1, 3), i => Assert.Equal(1, Step(done, "write", i).GetProperty("attempt").GetInt32()));
    }

    [Fact]
    public async Task Parameter_values_never_appear_in_logs_events_audit_or_results()
    {
        var marker = "WPARAM" + Guid.NewGuid().ToString("N")[..12];
        var key = await Kit.PublishInvoiceWorkflowAsync(writeConfig: Write());

        // A written run, a run that fails with an unknown outcome, a run whose parameter is missing, and a run found with its write still running.
        var written = await Kit.SubmitNewAsync(key, Data(marker));
        await RunEngineAsync(written, captureLogs: true);

        var unknown = await Kit.SubmitNewAsync(key, Data(marker));
        _write.Failure = DatabaseWriteResult.Failure("db.timeout", outcomeUnknown: true);
        await RunEngineAsync(unknown, captureLogs: true);
        _write.Failure = null;

        var missing = Data(null);
        missing["payment"]!["terms"] = marker; // a submitted value that is not a parameter must not be logged either
        var rejected = await Kit.SubmitNewAsync(key, missing);
        await RunEngineAsync(rejected, captureLogs: true);

        var stopped = await Kit.SubmitNewAsync(key, Data(marker));
        using (var shutdown = new CancellationTokenSource())
        {
            _write.During = (_, _) => shutdown.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunEngineAsync(stopped, captureLogs: true, ct: shutdown.Token));
            _write.During = null;
        }

        await RunEngineAsync(stopped, captureLogs: true);

        Assert.Contains(_write.Requests, r => Equals(r.Parameters["ref"], marker)); // the value was used
        var ids = new[] { written, unknown, rejected, stopped };
        foreach (var id in ids)
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
            foreach (var text in new[] { execution.GetRawText(), await events.Content.ReadAsStringAsync(), rows, audit })
            {
                Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
                Assert.DoesNotContain(SqlMarker, text, StringComparison.Ordinal);
            }
        }

        var leaks = fixture.Factory.Logs.Entries.Where(e => e.AllText.Contains(marker, StringComparison.Ordinal) || e.AllText.Contains(SqlMarker, StringComparison.Ordinal)).ToList();
        Assert.True(leaks.Count == 0, "Write values or SQL in logs:\n" + string.Join("\n", leaks.Take(5).Select(l => $"[{l.Category}] {l.Message}")));
        Assert.Contains(fixture.Factory.Logs.Entries, e => e.Category.Contains("ExecutionEngine", StringComparison.Ordinal)
                                                           && e.Scope.TryGetValue("ExecutionId", out var x) && x == stopped.ToString()); // the engine's lines were captured
    }
}
