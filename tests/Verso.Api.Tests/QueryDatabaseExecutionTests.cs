using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Verso.Application.Connections;
using Verso.Rendering;

namespace Verso.Api.Tests;

/// <summary>
/// The Query database step in a real run (engine side): parameters come from the run data, the row(s) found are merged under the result name,
/// the ports and failures end the run the right way, a resumed run queries again, and no row or parameter value ever reaches history, events or audit.
/// The database is a <see cref="FakeDatabaseQuery"/>; the real runners are covered by DatabaseQueryRunnerTests.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class QueryDatabaseExecutionTests(MigratedDatabaseFixture fixture) : IDisposable
{
    /// <summary>A value that exists only in the fake database's answer.</summary>
    private const string RowMarker = "Zephyr Query Holdings";

    /// <summary>A value that exists only in the submitted data and is used as a parameter.</summary>
    private const string ParameterMarker = "PO-QMARK-7731";

    private readonly HttpClient _client = fixture.Factory.CreateClient();
    private readonly TestDocumentStore _store = new();
    private readonly CapturingPdfPrinter _printer = new();
    private readonly RecordingExecutionJobs _jobs = new();
    private readonly FakeDatabaseQuery _query = new() { Rows = [FakeDatabaseQuery.Row(("name", RowMarker), ("address", "1 Synthetic Row, 0000 Nowhere"), ("vatNumber", "CY00000000Z"))] };
    private ExecutionTestKit Kit => new(fixture.Factory, _client);

    public void Dispose()
    {
        _store.Dispose();
        _client.Dispose();
    }

    private static JsonObject Query(string parameters = "po = invoice.poNumber", string resultName = "customer", int maxRows = 1) => new()
    {
        ["connection"] = "crm-db",
        ["sql"] = "SELECT name, address FROM customers WHERE po_number = @po",
        ["parameters"] = parameters,
        ["resultName"] = resultName,
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

    private Task RunEngineAsync(Guid id) => Kit.RunEngineAsync(id, _printer, _store, _jobs, query: _query);

    private async Task<(Guid Id, JsonElement Done)> RunAsync(JsonObject config, JsonNode? data = null, bool notFoundToPdf = false)
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(queryConfig: config, queryNotFoundToPdf: notFoundToPdf);
        var id = await Kit.SubmitNewAsync(key, data ?? Data());
        await RunEngineAsync(id);
        return (id, await Kit.GetExecutionAsync(id));
    }

    [Fact]
    public async Task Found_merges_one_row_as_object_and_the_document_uses_it()
    {
        var (_, done) = await RunAsync(Query());

        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        var request = Assert.Single(_query.Requests);
        Assert.Equal("crm-db", request.ConnectionKey);
        Assert.Equal("SELECT name, address FROM customers WHERE po_number = @po", request.Sql);
        Assert.Equal(ParameterMarker, Assert.Single(request.Parameters, p => p.Key == "po").Value);
        Assert.Equal(1, request.MaxRows);
        Assert.Equal(TimeSpan.FromSeconds(20), request.Timeout);

        // The row replaced 'customer' (it existed in the data): the PDF shows the database's name, not the submitted one.
        var html = Assert.Single(_printer.Bodies);
        Assert.Contains(RowMarker, html, StringComparison.Ordinal);
        Assert.DoesNotContain("Kestrel Logistics Ltd", html, StringComparison.Ordinal);

        var step = Step(done, "query");
        Assert.Equal("found", step.GetProperty("outputPort").GetString());
        var result = step.GetProperty("result");
        Assert.Equal(1, result.GetProperty("rows").GetInt32());
        Assert.Equal(["name", "address", "vatNumber"], result.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
        Assert.False(result.GetProperty("truncated").GetBoolean());
        Assert.Equal(7, result.GetProperty("durationMs").GetInt64());
        Assert.Equal("crm-db", result.GetProperty("connection").GetString());
        Assert.Equal(["rows", "columns", "truncated", "durationMs", "connection"], result.EnumerateObject().Select(p => p.Name));

        // The test workflow's Archive step records {{customer.name}} as metadata (the author's choice, shown with the document); nowhere else may a value appear.
        Assert.Equal(RowMarker, Assert.Single(done.GetProperty("documents").EnumerateArray()).GetProperty("archive").GetProperty("metadata").GetProperty("Customer").GetString());
        var json = WithoutArchiveMetadata(done);
        Assert.DoesNotContain(RowMarker, json, StringComparison.Ordinal);
        Assert.DoesNotContain("Synthetic Row", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Several_rows_become_an_array()
    {
        _query.Rows = [FakeDatabaseQuery.Row(("name", "Contact one")), FakeDatabaseQuery.Row(("name", "Contact two"))];
        _query.Truncated = true;
        var key = await Kit.PublishInvoiceWorkflowAsync(
            queryConfig: Query(resultName: "contacts", maxRows: 2),
            forEachConfig: new JsonObject { ["field"] = "contacts", ["mode"] = "each", ["item"] = "contact" });
        var id = await Kit.SubmitNewAsync(key, Data());

        await RunEngineAsync(id);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        var result = Step(done, "query").GetProperty("result");
        Assert.Equal(2, result.GetProperty("rows").GetInt32());
        Assert.True(result.GetProperty("truncated").GetBoolean());
        // The For each sees a list of two records under the result name.
        Assert.Equal(2, Step(done, "loop").GetProperty("result").GetProperty("count").GetInt32());
        Assert.Equal(2, _printer.Bodies.Count);
        Assert.DoesNotContain("Contact one", done.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_inside_a_for_each_reads_the_item()
    {
        _query.Rows = [FakeDatabaseQuery.Row(("name", RowMarker), ("address", "1 Synthetic Row"), ("vatNumber", "CY00000000Z"))];
        var key = await Kit.PublishInvoiceWorkflowAsync(
            queryConfig: Query(parameters: "po = line.unit"), queryAt: "in-loop",
            forEachConfig: new JsonObject { ["field"] = "lines", ["mode"] = "each", ["item"] = "line" });
        var id = await Kit.SubmitNewAsync(key, Data());

        await RunEngineAsync(id);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(["kWh", "month", "month"], _query.Requests.Select(r => r.Parameters["po"]));
        Assert.Equal(3, _printer.Bodies.Count);
        Assert.All(_printer.Bodies, html => Assert.Contains(RowMarker, html, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_found_port_is_followed_when_connected()
    {
        _query.Rows = [];

        var (_, done) = await RunAsync(Query(), notFoundToPdf: true);

        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        var step = Step(done, "query");
        Assert.Equal("not-found", step.GetProperty("outputPort").GetString());
        Assert.Equal(0, step.GetProperty("result").GetProperty("rows").GetInt32());
        Assert.Equal("Succeeded", Step(done, "pdf").GetProperty("status").GetString());
        Assert.Contains("Kestrel Logistics Ltd", Assert.Single(_printer.Bodies), StringComparison.Ordinal); // the data is unchanged
    }

    [Fact]
    public async Task Not_found_unconnected_ends_the_run_succeeded()
    {
        _query.Rows = [];

        var (_, done) = await RunAsync(Query());

        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal("not-found", Step(done, "query").GetProperty("outputPort").GetString());
        Assert.Equal("Skipped", Step(done, "pdf").GetProperty("status").GetString());
        Assert.Empty(_printer.Bodies);
    }

    [Fact]
    public async Task Missing_parameter_rejects_without_calling_the_database()
    {
        var (_, done) = await RunAsync(Query(), Data(poNumber: null));

        Assert.Equal(0, _query.CallCount);
        Assert.Equal("Rejected", done.GetProperty("status").GetString());
        Assert.Equal("data.query_invalid", done.GetProperty("failureCode").GetString());
        var step = Step(done, "query");
        Assert.Equal("failed", step.GetProperty("outputPort").GetString());
        var issue = Assert.Single(step.GetProperty("result").GetProperty("issues").EnumerateArray());
        Assert.Equal("query.parameter_missing", issue.GetProperty("code").GetString());
        var message = issue.GetProperty("message").GetString()!;
        Assert.Contains("po", message, StringComparison.Ordinal);
        Assert.Contains("invoice.poNumber", message, StringComparison.Ordinal);
        Assert.Empty(_printer.Bodies);
    }

    [Fact]
    public async Task Non_scalar_parameter_rejects()
    {
        var (_, done) = await RunAsync(Query(parameters: "po = customer"));

        Assert.Equal(0, _query.CallCount);
        Assert.Equal("Rejected", done.GetProperty("status").GetString());
        Assert.Equal("data.query_invalid", done.GetProperty("failureCode").GetString());
        var issue = Assert.Single(Step(done, "query").GetProperty("result").GetProperty("issues").EnumerateArray());
        Assert.Equal("query.parameter_invalid", issue.GetProperty("code").GetString());
        Assert.DoesNotContain("Kestrel", done.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retryable_failure_marks_step_failed_and_retry_succeeds()
    {
        _query.Failure = DatabaseQueryResult.Failure("db.timeout", retryable: true);
        _query.FailTimes = 1;
        var key = await Kit.PublishInvoiceWorkflowAsync(queryConfig: Query());
        var id = await Kit.SubmitNewAsync(key, Data());

        await RunEngineAsync(id);

        var first = await Kit.GetExecutionAsync(id);
        Assert.Equal("Running", first.GetProperty("status").GetString()); // a retry is scheduled
        Assert.Equal("Failed", Step(first, "query").GetProperty("status").GetString());
        Assert.NotEmpty(_jobs.Scheduled);
        Assert.Empty(_printer.Bodies);

        await RunEngineAsync(id);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(2, _query.CallCount);
        Assert.Equal(2, Step(done, "query").GetProperty("attempt").GetInt32());
        Assert.Contains(RowMarker, Assert.Single(_printer.Bodies), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_retryable_configuration_failure_fails_without_retry()
    {
        _query.Failure = DatabaseQueryResult.Failure("db.connection_unavailable", retryable: false);

        var (_, done) = await RunAsync(Query());

        Assert.Equal("Failed", done.GetProperty("status").GetString());
        Assert.Equal("db.connection_unavailable", done.GetProperty("failureCode").GetString());
        Assert.Contains(DatabaseQueryMessages.For("db.connection_unavailable"), done.GetProperty("failureMessage").GetString(), StringComparison.Ordinal);
        Assert.Empty(_jobs.Scheduled);
        Assert.Equal(1, _query.CallCount);
        Assert.Empty(_printer.Bodies);
    }

    [Fact]
    public async Task Resumed_run_requeries()
    {
        var id = await AwaitingApprovalAsync();
        Assert.Equal(1, _query.CallCount);

        await ApproveAsync(id);
        await RunEngineAsync(id);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        // The rows are not stored: the resumed run read them again, with the same parameters, and the document used them.
        Assert.Equal(2, _query.CallCount);
        Assert.Equal(_query.Requests[0].Parameters, _query.Requests[1].Parameters);
        Assert.Contains(RowMarker, Assert.Single(_printer.Bodies), StringComparison.Ordinal);
        Assert.Equal(1, Step(done, "query").GetProperty("attempt").GetInt32()); // restored, not run again as a step
    }

    [Fact]
    public async Task Resumed_run_fails_when_the_row_is_gone()
    {
        var id = await AwaitingApprovalAsync();
        _query.Rows = []; // the database changed while the run waited

        await ApproveAsync(id);
        await RunEngineAsync(id);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Failed", done.GetProperty("status").GetString());
        Assert.Equal("query.restore", done.GetProperty("failureCode").GetString());
        Assert.Empty(_printer.Bodies);
        Assert.Equal(2, _query.CallCount);
    }

    [Fact]
    public async Task Parameter_values_never_appear_in_events_or_step_results()
    {
        var (id, done) = await RunAsync(Query());
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(ParameterMarker, _query.Requests[0].Parameters["po"]); // it was used

        using var events = await _client.GetAsync(new Uri($"/api/v1/executions/{id}/events", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        var eventsJson = await events.Content.ReadAsStringAsync();
        var (rows, audit) = await Kit.WithDbAsync(async db =>
        {
            var correlation = await db.Executions.Where(e => e.Id == id).Select(e => e.CorrelationId).SingleAsync();
            var eventData = await db.ExecutionEvents.Where(e => e.ExecutionId == id).Select(e => e.Type + " " + e.Data).ToListAsync();
            var auditDetails = await db.AuditEntries.Where(a => a.EntityId == id.ToString() || a.TraceId == correlation).Select(a => a.Action + " " + a.Details).ToListAsync();
            return (string.Join('\n', eventData), string.Join('\n', auditDetails));
        });

        Assert.NotEmpty(rows);
        foreach (var text in new[] { WithoutArchiveMetadata(done), eventsJson, rows, audit })
        {
            Assert.DoesNotContain(ParameterMarker, text, StringComparison.Ordinal);
            Assert.DoesNotContain(RowMarker, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Parallel_branch_with_query_then_merge()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(parallelBranches: true, joinBranches: true, queryConfig: Query(), queryAt: "branch-b");
        var id = await Kit.SubmitNewAsync(key, Data());

        await RunEngineAsync(id);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(1, _query.CallCount);
        // Branch A ran from the data at the fork (no query result); Branch B's PDF used the row.
        Assert.Equal(2, _printer.Bodies.Count);
        Assert.Contains("Kestrel Logistics Ltd", _printer.Bodies[0], StringComparison.Ordinal);
        Assert.Contains(RowMarker, _printer.Bodies[1], StringComparison.Ordinal);

        // After the join the data is as Branch B left it: the archive's metadata carries the queried name.
        var metadata = await Kit.WithDbAsync(db =>
            (from a in db.ArchiveRecords join d in db.Documents on a.DocumentId equals d.Id where d.ExecutionId == id select a.Metadata).SingleAsync());
        Assert.Equal(RowMarker, JsonDocument.Parse(metadata).RootElement.GetProperty("Customer").GetString());
    }

    private async Task<Guid> AwaitingApprovalAsync()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(queryConfig: Query(), approvalConfig: new JsonObject { ["approver"] = "operator", ["escalateAfter"] = "24h" });
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
