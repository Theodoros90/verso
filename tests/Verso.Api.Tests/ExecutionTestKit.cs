using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verso.Application.Documents;
using Verso.Application.Executions;
using Verso.Infrastructure.Documents;
using Verso.Infrastructure.Persistence;
using Verso.Rendering;

namespace Verso.Api.Tests;

/// <summary>
/// Helpers for execution tests: publish the invoice template and an "Invoice standard" workflow through the API,
/// submit data, and run the engine as the Worker would (with a fake printer; real printing is covered by
/// Verso.Pdf.Tests) against a real local document store in a temporary folder.
/// </summary>
internal sealed class ExecutionTestKit(VersoApiFactory factory, HttpClient client)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Validate rules used by the standard test workflow (the valid fixture passes all of them).</summary>
    public static readonly JsonNode StandardRules = JsonNode.Parse("""
        { "required": ["invoice.number", "invoice.date", "customer.name", "customer.address", "lines", "totals.gross"],
          "types": [
            { "field": "invoice.date", "type": "date" }, { "field": "invoice.dueDate", "type": "date" },
            { "field": "totals.gross", "type": "number" }, { "field": "customer.email", "type": "email" },
            { "field": "customer.vatNumber", "type": "eu-vat" }, { "field": "payment.iban", "type": "iban" } ] }
        """)!;

    public static string Fixture(string relative) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", relative));

    public static JsonNode Sample => JsonNode.Parse(Fixture("invoice/sample.json"))!;

    public static JsonNode Broken => JsonNode.Parse(Fixture("invoice/broken.json"))!;

    /// <summary>Publishes the invoice template (v1) and a workflow trigger → validate → generate-pdf → archive. Returns the workflow key.</summary>
    public async Task<string> PublishInvoiceWorkflowAsync(JsonNode? rules = null)
    {
        var templateId = await PublishInvoiceTemplateAsync();
        var key = "inv-" + Guid.NewGuid().ToString("N")[..12];
        using var create = await client.PostAsJsonAsync(new Uri("/api/v1/workflows", UriKind.Relative), new { key, name = "Invoice standard" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var workflowId = (await ReadAsync(create)).GetProperty("id").GetGuid();

        var definition = JsonNode.Parse($$"""
            { "nodes": [
                { "id": "trigger", "type": "api-trigger", "title": "API trigger", "position": { "x": 0, "y": 80 }, "config": {} },
                { "id": "validate", "type": "validate", "title": "Validate invoice data", "position": { "x": 260, "y": 80 }, "config": {} },
                { "id": "pdf", "type": "generate-pdf", "title": "Generate invoice PDF", "position": { "x": 520, "y": 80 },
                  "config": { "templateId": "{{templateId}}", "templateVersion": "latest-published", "fileName": "{{"{{"}}invoice.number{{"}}"}}.pdf" } },
                { "id": "archive", "type": "archive", "title": "Archive", "position": { "x": 780, "y": 80 },
                  "config": { "folderPattern": "invoices/{{"{{"}}invoice.date|year{{"}}"}}/{{"{{"}}invoice.date|month{{"}}"}}", "retention": "7y",
                              "metadata": [ { "key": "InvoiceNumber", "value": "{{"{{"}}invoice.number{{"}}"}}" }, { "key": "Customer", "value": "{{"{{"}}customer.name{{"}}"}}" } ] } } ],
              "edges": [
                { "id": "e1", "source": "trigger", "sourcePort": "out", "target": "validate", "targetPort": "in" },
                { "id": "e2", "source": "validate", "sourcePort": "valid", "target": "pdf", "targetPort": "in" },
                { "id": "e3", "source": "pdf", "sourcePort": "success", "target": "archive", "targetPort": "in" } ] }
            """)!;
        definition["nodes"]![1]!["config"] = (rules ?? StandardRules).DeepClone();

        using var draft = await client.GetAsync(new Uri($"/api/v1/workflows/{workflowId}/draft", UriKind.Relative));
        using var save = await SendAsync(HttpMethod.Put, $"/api/v1/workflows/{workflowId}/draft", draft.Headers.ETag!.ToString(), new JsonObject { ["definition"] = definition });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        using var publish = await SendAsync(HttpMethod.Post, $"/api/v1/workflows/{workflowId}/draft/publish", save.Headers.ETag!.ToString());
        Assert.True(publish.StatusCode == HttpStatusCode.OK, await publish.Content.ReadAsStringAsync());
        return key;
    }

    public async Task<Guid> PublishInvoiceTemplateAsync()
    {
        var key = "X" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        using var create = await client.PostAsJsonAsync(new Uri("/api/v1/templates", UriKind.Relative), new { key, name = "Commercial invoice" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = (await ReadAsync(create)).GetProperty("id").GetGuid();
        using var draft = await client.GetAsync(new Uri($"/api/v1/templates/{id}/draft", UriKind.Relative));
        var body = new JsonObject
        {
            ["pageSettings"] = new JsonObject { ["size"] = "A4", ["orientation"] = "portrait", ["marginsMm"] = new JsonObject { ["top"] = 18, ["right"] = 16, ["bottom"] = 18, ["left"] = 16 } },
            ["layout"] = JsonNode.Parse(Fixture("invoice/layout.json")),
            ["dataSchema"] = new JsonObject { ["type"] = "object" },
            ["sampleData"] = Sample,
        };
        using var save = await SendAsync(HttpMethod.Put, $"/api/v1/templates/{id}/draft", draft.Headers.ETag!.ToString(), body);
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        using var publish = await SendAsync(HttpMethod.Post, $"/api/v1/templates/{id}/draft/publish", save.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        return id;
    }

    public Task<HttpResponseMessage> SubmitAsync(string workflowKey, string? idempotencyKey, JsonNode? data)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/workflows/{workflowKey}/executions", UriKind.Relative))
        {
            Content = JsonContent.Create(new JsonObject { ["data"] = data?.DeepClone() }),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return client.SendAsync(request);
    }

    /// <summary>Submits and asserts 202; returns the execution id.</summary>
    public async Task<Guid> SubmitNewAsync(string workflowKey, JsonNode data)
    {
        using var response = await SubmitAsync(workflowKey, Guid.NewGuid().ToString(), data);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return (await ReadAsync(response)).GetProperty("executionId").GetGuid();
    }

    public async Task<JsonElement> GetExecutionAsync(Guid id)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/executions/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync(response);
    }

    /// <summary>One job run of the execution, as the Worker's Hangfire job would do it.</summary>
    public async Task RunEngineAsync(Guid executionId, IPdfPrinter printer, IDocumentStore store, RecordingExecutionJobs jobs, CancellationToken ct = default)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VersoDbContext>();
        await new ExecutionEngine(db, printer, store, jobs, TimeProvider.System, NullLogger<ExecutionEngine>.Instance).RunAsync(executionId, ct);
    }

    public async Task<T> WithDbAsync<T>(Func<VersoDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<VersoDbContext>());
    }

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), Json);

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string uri, string etag, JsonNode? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(uri, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return client.SendAsync(request);
    }
}

/// <summary>Stands in for Chromium: returns a small PDF-like byte array per call (unique, so re-prints are detectable).</summary>
internal sealed class FakePdfPrinter : IPdfPrinter
{
    private int _calls;

    public int Calls => _calls;

    public Task<PdfOutput> PrintAsync(PrintDocument document, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _calls);
        return Task.FromResult(new PdfOutput(Encoding.ASCII.GetBytes($"%PDF-1.7\n% fake document {call} {Guid.NewGuid():N}\n%%EOF\n"), 0, 1));
    }
}

/// <summary>A real local file store in a temporary folder, with switches to simulate outages and Worker shutdowns.</summary>
internal sealed class TestDocumentStore : IDocumentStore, IDisposable
{
    private readonly LocalFileDocumentStore _inner;

    public TestDocumentStore()
    {
        Root = Path.Combine(Path.GetTempPath(), "verso-test-store", Guid.NewGuid().ToString("N"));
        _inner = new LocalFileDocumentStore(Options.Create(new DocumentStoreOptions { RootPath = Root }));
    }

    public string Root { get; }

    /// <summary>Writes throw <see cref="IOException"/> while true (a storage outage).</summary>
    public bool FailWrites { get; set; }

    /// <summary>Called after a successful write with its key; may cancel a token to simulate the Worker stopping.</summary>
    public Action<string>? AfterWrite { get; set; }

    public string Provider => _inner.Provider;

    public async Task WriteAsync(string key, byte[] content, CancellationToken ct)
    {
        if (FailWrites)
        {
            throw new IOException("Simulated storage outage.");
        }

        await _inner.WriteAsync(key, content, ct);
        AfterWrite?.Invoke(key);
        ct.ThrowIfCancellationRequested();
    }

    public Task<byte[]?> ReadAsync(string key, CancellationToken ct) => _inner.ReadAsync(key, ct);

    public Task DeleteAsync(string key, CancellationToken ct) => _inner.DeleteAsync(key, ct);

    public string[] Files(string prefix) =>
        Directory.Exists(Path.Combine(Root, prefix)) ? Directory.GetFiles(Path.Combine(Root, prefix), "*", SearchOption.AllDirectories) : [];

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

internal sealed class RecordingExecutionJobs : IExecutionJobs
{
    public ConcurrentQueue<Guid> Enqueued { get; } = new();
    public ConcurrentQueue<(Guid Id, TimeSpan Delay)> Scheduled { get; } = new();

    public void Enqueue(Guid executionId) => Enqueued.Enqueue(executionId);

    public void Schedule(Guid executionId, TimeSpan delay) => Scheduled.Enqueue((executionId, delay));
}
