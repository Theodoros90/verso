using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Verso.Application.Executions;
using Verso.Infrastructure.Persistence;

namespace Verso.Api.Tests;

/// <summary>
/// Plan §7 / M9: "payloads are never written to logs", and every log line of a request or run carries its
/// correlation id. All log output of the API host is captured at Trace level (more than production writes).
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class LoggingTests(MigratedDatabaseFixture fixture) : IDisposable
{
    private readonly HttpClient _client = fixture.Factory.CreateClient();
    private readonly TestDocumentStore _store = new();
    private ExecutionTestKit Kit => new(fixture.Factory, _client);

    public void Dispose()
    {
        _store.Dispose();
        _client.Dispose();
    }

    [Fact]
    public async Task PayloadValues_NeverReachTheLogs_OnAnyPath()
    {
        var marker = "MARKER" + Guid.NewGuid().ToString("N")[..12]; // a value only the payloads contain
        var key = await Kit.PublishInvoiceWorkflowAsync();
        var valid = WithMarker(ExecutionTestKit.Sample, marker);
        var broken = WithMarker(ExecutionTestKit.Broken, marker);

        // Valid run, rejected run, a run whose storage fails (technical error path), the payload view, and bad requests.
        var ok = await Kit.SubmitNewAsync(key, valid);
        var rejected = await Kit.SubmitNewAsync(key, broken);
        var failing = await Kit.SubmitNewAsync(key, valid);
        await RunEngineAsync(ok, _store);
        await RunEngineAsync(rejected, _store);
        _store.FailWrites = true;
        for (var attempt = 0; attempt < ExecutionEngine.MaxStepAttempts; attempt++)
        {
            await RunEngineAsync(failing, _store);
        }

        using (var input = await _client.GetAsync(new Uri($"/api/v1/executions/{ok}/input", UriKind.Relative)))
        {
            Assert.Contains(marker, await input.Content.ReadAsStringAsync(), StringComparison.Ordinal); // the response has it…
        }

        await SendRawAsync(key, $$"""{ "data": { "customer": { "name": "{{marker}}" """); // malformed JSON
        await SendRawAsync(key, "{ \"data\": " + string.Concat(Enumerable.Repeat("{\"a\":", 40)) + $"\"{marker}\"" + new string('}', 40) + " }"); // too deep
        await SendRawAsync(key, $$"""{ "data": "{{marker}}" }"""); // not an object
        using (var same = await Kit.SubmitAsync(key, (await Kit.GetExecutionAsync(ok)).GetProperty("idempotencyKey").GetString(), broken))
        {
            Assert.Equal(HttpStatusCode.Conflict, same.StatusCode); // idempotency conflict path
        }

        // …the logs don't.
        var leaks = fixture.Factory.Logs.Entries.Where(e => e.AllText.Contains(marker, StringComparison.Ordinal)).ToList();
        Assert.True(leaks.Count == 0, "Payload content in logs:\n" + string.Join("\n", leaks.Take(5).Select(l => $"[{l.Category}] {l.Message}")));
        Assert.Contains(fixture.Factory.Logs.Entries, e => e.Category.Contains("ExecutionEngine", StringComparison.Ordinal) && e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task RequestsAndRuns_LogTheirCorrelationId()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync();
        var correlationId = "corr-" + Guid.NewGuid().ToString("N")[..12];

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/workflows/{key}/executions", UriKind.Relative))
        {
            Content = System.Net.Http.Json.JsonContent.Create(new JsonObject { ["data"] = ExecutionTestKit.Sample }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add("X-Correlation-Id", correlationId);
        using var response = await _client.SendAsync(request);
        var id = (await ExecutionTestKit.ReadAsync(response)).GetProperty("executionId").GetGuid();
        await RunEngineAsync(id, _store);

        Assert.Equal(correlationId, response.Headers.GetValues("X-Correlation-Id").Single()); // echoed
        Assert.Equal(correlationId, (await Kit.GetExecutionAsync(id)).GetProperty("correlationId").GetString()); // stored on the run
        var tagged = fixture.Factory.Logs.Entries.Where(e => e.Scope.TryGetValue("CorrelationId", out var c) && c == correlationId).ToList();
        Assert.Contains(tagged, e => e.Category.Contains("HttpLogging", StringComparison.Ordinal)); // the API request
        Assert.Contains(tagged, e => e.Category.Contains("ExecutionEngine", StringComparison.Ordinal)
                                     && e.Scope.TryGetValue("ExecutionId", out var x) && x == id.ToString()); // the run
    }

    [Fact]
    public async Task RequestsWithoutACorrelationId_GetOne_AndASubmissionUsesIt()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync();

        using var response = await Kit.SubmitAsync(key, Guid.NewGuid().ToString(), ExecutionTestKit.Sample);
        using var invalid = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/templates", UriKind.Relative));
        invalid.Headers.TryAddWithoutValidation("X-Correlation-Id", "not valid! <script>");
        using var other = await _client.SendAsync(invalid);

        var generated = response.Headers.GetValues("X-Correlation-Id").Single();
        Assert.Matches("^[0-9a-f]{32}$", generated);
        Assert.Equal(generated, (await ExecutionTestKit.ReadAsync(response)).GetProperty("correlationId").GetString());
        Assert.Matches("^[0-9a-f]{32}$", other.Headers.GetValues("X-Correlation-Id").Single()); // a malformed one is replaced, not echoed
    }

    private async Task RunEngineAsync(Guid id, TestDocumentStore store)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var engine = new ExecutionEngine(
            scope.ServiceProvider.GetRequiredService<VersoDbContext>(), new FakePdfPrinter(), new FakeDocxWriter(), new FakePdfCombiner(), store, new RecordingExecutionJobs(),
            TimeProvider.System, scope.ServiceProvider.GetRequiredService<ILogger<ExecutionEngine>>()); // logs go to the capture
        await engine.RunAsync(id, CancellationToken.None);
    }

    private async Task SendRawAsync(string key, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/workflows/{key}/executions", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>The marker in fields that end up in names, metadata, file names and validation results.</summary>
    private static JsonNode WithMarker(JsonNode data, string marker)
    {
        data["customer"]!["name"] = marker;
        data["invoice"]!["poNumber"] = marker;
        data["payment"]!["reference"] = marker;
        return data;
    }
}
