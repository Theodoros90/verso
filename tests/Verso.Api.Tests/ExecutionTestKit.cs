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
using Verso.Rendering.Docx;

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
    public async Task<string> PublishInvoiceWorkflowAsync(JsonNode? rules = null, JsonNode? trigger = null, JsonNode? parseConfig = null, JsonNode? mapConfig = null, JsonNode? transformConfig = null, JsonNode? mergeConfig = null, JsonNode? ifConfig = null, JsonNode? switchConfig = null, string switchPort = "mid", JsonNode? forEachConfig = null, bool forEachBackEdge = false, JsonNode? approvalConfig = null, bool approvalDeclinedToPdf = false, string? docxEditing = null, JsonNode? emailConfig = null, JsonNode? combineConfig = null, JsonNode? sendConfig = null)
    {
        var templateId = await PublishInvoiceTemplateAsync();
        var key = "inv-" + Guid.NewGuid().ToString("N")[..12];
        using var create = await client.PostAsJsonAsync(new Uri("/api/v1/workflows", UriKind.Relative), new { key, name = "Invoice standard" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var workflowId = (await ReadAsync(create)).GetProperty("id").GetGuid();

        var definition = JsonNode.Parse($$"""
            { "nodes": [
                { "id": "trigger", "type": "api-trigger", "title": "API trigger", "position": { "x": 0, "y": 80 }, "config": { "path": "/hooks/invoices", "auth": "hmac-sha256" } },
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
        if (docxEditing is not null)
        {
            // The document step is a Word step instead of a PDF step.
            var document = definition["nodes"]!.AsArray().Single(n => (string?)n!["id"] == "pdf")!;
            document["type"] = "generate-docx";
            document["config"]!["fileName"] = "{{invoice.number}}.docx";
            document["config"]!["protect"] = docxEditing;
        }

        if (emailConfig is not null)
        {
            // The document step is an HTML email step (no template involved).
            var document = definition["nodes"]!.AsArray().Single(n => (string?)n!["id"] == "pdf")!;
            document["type"] = "generate-email";
            document["config"] = emailConfig.DeepClone();
        }

        if (sendConfig is not null)
        {
            // trigger → validate → pdf → send → archive: the mail carries the PDF; Archive still stores it.
            var nodes = definition["nodes"]!.AsArray();
            var edges = definition["edges"]!.AsArray();
            nodes.Add(JsonNode.Parse("""{ "id": "send", "type": "send-email", "title": "Send email", "position": { "x": 780, "y": 200 }, "config": {} }""")!);
            nodes.Single(n => (string?)n!["id"] == "send")!["config"] = sendConfig.DeepClone();
            edges.Remove(edges.Single(e => (string?)e!["id"] == "e3"));
            edges.Add(JsonNode.Parse("""{ "id": "e3", "source": "pdf", "sourcePort": "success", "target": "send", "targetPort": "in" }"""));
            edges.Add(JsonNode.Parse("""{ "id": "e4", "source": "send", "sourcePort": "sent", "target": "archive", "targetPort": "in" }"""));
        }

        if (combineConfig is not null)
        {
            // trigger → validate → pdf → pdf2 → combine → archive: two PDFs from the same template, then one pack.
            var nodes = definition["nodes"]!.AsArray();
            var edges = definition["edges"]!.AsArray();
            var pdf = nodes.Single(n => (string?)n!["id"] == "pdf")!;
            var second = pdf.DeepClone();
            second["id"] = "pdf2";
            second["title"] = "Generate second PDF";
            second["config"]!["fileName"] = "{{invoice.number}}-copy.pdf";
            nodes.Add(second);
            nodes.Add(JsonNode.Parse("""{ "id": "combine", "type": "combine-pdfs", "title": "Combine PDFs", "position": { "x": 1040, "y": 80 }, "config": {} }""")!);
            nodes.Single(n => (string?)n!["id"] == "combine")!["config"] = combineConfig.DeepClone();
            edges.Remove(edges.Single(e => (string?)e!["id"] == "e3"));
            edges.Add(JsonNode.Parse("""{ "id": "e3", "source": "pdf", "sourcePort": "success", "target": "pdf2", "targetPort": "in" }"""));
            edges.Add(JsonNode.Parse("""{ "id": "e4", "source": "pdf2", "sourcePort": "success", "target": "combine", "targetPort": "in" }"""));
            edges.Add(JsonNode.Parse("""{ "id": "e5", "source": "combine", "sourcePort": "success", "target": "archive", "targetPort": "in" }"""));
        }

        definition["nodes"]![1]!["config"] = (rules ?? StandardRules).DeepClone();
        if (trigger is not null) definition["nodes"]![0] = trigger.DeepClone();
        if (parseConfig is not null)
        {
            // trigger → parse (success) → validate …
            definition["nodes"]!.AsArray().Insert(1, JsonNode.Parse("""{ "id": "parse", "type": "parse", "title": "Parse content", "position": { "x": 130, "y": 80 }, "config": {} }"""));
            definition["nodes"]![1]!["config"] = parseConfig.DeepClone();
            definition["edges"]![0]!["target"] = "parse";
            definition["edges"]!.AsArray().Add(JsonNode.Parse("""{ "id": "e0", "source": "parse", "sourcePort": "success", "target": "validate", "targetPort": "in" }"""));
        }

        if (mapConfig is not null)
        {
            // … → map-fields (success) → validate
            var map = JsonNode.Parse("""{ "id": "map", "type": "map-fields", "title": "Map fields", "position": { "x": 130, "y": 200 }, "config": {} }""")!;
            map["config"] = mapConfig.DeepClone();
            definition["nodes"]!.AsArray().Add(map);
            definition["edges"]!.AsArray().First(e => (string?)e!["target"] == "validate")!["target"] = "map";
            definition["edges"]!.AsArray().Add(JsonNode.Parse("""{ "id": "e9", "source": "map", "sourcePort": "success", "target": "validate", "targetPort": "in" }"""));
        }

        if (transformConfig is not null)
        {
            // … → transform (success) → validate
            var transform = JsonNode.Parse("""{ "id": "transform", "type": "transform", "title": "Transform data", "position": { "x": 130, "y": 320 }, "config": {} }""")!;
            transform["config"] = transformConfig.DeepClone();
            definition["nodes"]!.AsArray().Add(transform);
            definition["edges"]!.AsArray().First(e => (string?)e!["target"] == "validate")!["target"] = "transform";
            definition["edges"]!.AsArray().Add(JsonNode.Parse("""{ "id": "e8", "source": "transform", "sourcePort": "success", "target": "validate", "targetPort": "in" }"""));
        }

        if (mergeConfig is not null)
        {
            // … → merge-data (success) → validate
            var merge = JsonNode.Parse("""{ "id": "merge", "type": "merge-data", "title": "Merge data", "position": { "x": 130, "y": 440 }, "config": {} }""")!;
            merge["config"] = mergeConfig.DeepClone();
            definition["nodes"]!.AsArray().Add(merge);
            definition["edges"]!.AsArray().First(e => (string?)e!["target"] == "validate")!["target"] = "merge";
            definition["edges"]!.AsArray().Add(JsonNode.Parse("""{ "id": "e7", "source": "merge", "sourcePort": "success", "target": "validate", "targetPort": "in" }"""));
        }

        if (ifConfig is not null)
        {
            // … → if-else: yes → validate, no → (nothing: the run ends there)
            var branch = JsonNode.Parse("""{ "id": "branch", "type": "if-else", "title": "If / else", "position": { "x": 130, "y": 480 }, "config": {} }""")!;
            branch["config"] = ifConfig.DeepClone();
            definition["nodes"]!.AsArray().Add(branch);
            definition["edges"]!.AsArray().First(e => (string?)e!["target"] == "validate")!["target"] = "branch";
            definition["edges"]!.AsArray().Add(JsonNode.Parse("""{ "id": "e9", "source": "branch", "sourcePort": "yes", "target": "validate", "targetPort": "in" }"""));
        }

        if (switchConfig is not null)
        {
            // … → switch: its <switchPort> output → validate; the other outputs are left open (the run ends there)
            var route = JsonNode.Parse("""{ "id": "route", "type": "switch", "title": "Switch", "position": { "x": 130, "y": 520 }, "config": {} }""")!;
            route["config"] = switchConfig.DeepClone();
            definition["nodes"]!.AsArray().Add(route);
            definition["edges"]!.AsArray().First(e => (string?)e!["target"] == "validate")!["target"] = "route";
            definition["edges"]!.AsArray().Add(JsonNode.Parse($$"""{ "id": "e10", "source": "route", "sourcePort": "{{switchPort}}", "target": "validate", "targetPort": "in" }"""));
        }

        if (forEachConfig is not null)
        {
            // validate (valid) → for-each: its 'each' output → pdf → archive. With a back edge the pdf step returns to the for-each instead of archiving.
            var loop = JsonNode.Parse("""{ "id": "loop", "type": "for-each", "title": "For each line", "position": { "x": 400, "y": 200 }, "config": {} }""")!;
            loop["config"] = forEachConfig.DeepClone();
            var nodes = definition["nodes"]!.AsArray();
            var edges = definition["edges"]!.AsArray();
            nodes.Add(loop);
            edges.First(e => (string?)e!["id"] == "e2")!["target"] = "loop";
            edges.Add(JsonNode.Parse("""{ "id": "e11", "source": "loop", "sourcePort": "each", "target": "pdf", "targetPort": "in" }"""));
            if (forEachBackEdge)
            {
                nodes.Remove(nodes.First(n => (string?)n!["id"] == "archive"));
                edges.Remove(edges.First(e => (string?)e!["id"] == "e3"));
                edges.Add(JsonNode.Parse("""{ "id": "e12", "source": "pdf", "sourcePort": "success", "target": "loop", "targetPort": "in" }"""));
            }
        }

        if (approvalConfig is not null)
        {
            // validate (valid) → approval: approved → whatever validate led to (pdf, or the loop); declined → nothing (rejects the run), or pdf
            var approval = JsonNode.Parse("""{ "id": "approve", "type": "approval", "title": "Manual review", "position": { "x": 400, "y": 320 }, "config": {} }""")!;
            approval["config"] = approvalConfig.DeepClone();
            var edges = definition["edges"]!.AsArray();
            var after = edges.First(e => (string?)e!["id"] == "e2")!;
            var target = (string)after["target"]!;
            after["target"] = "approve";
            definition["nodes"]!.AsArray().Add(approval);
            edges.Add(JsonNode.Parse($$"""{ "id": "e13", "source": "approve", "sourcePort": "approved", "target": "{{target}}", "targetPort": "in" }"""));
            if (approvalDeclinedToPdf)
            {
                edges.Add(JsonNode.Parse("""{ "id": "e14", "source": "approve", "sourcePort": "rejected", "target": "pdf", "targetPort": "in" }"""));
            }
        }

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
    public async Task RunEngineAsync(Guid executionId, IPdfPrinter printer, IDocumentStore store, RecordingExecutionJobs jobs, IDocxWriter? docx = null, IPdfCombiner? combiner = null, IEmailSender? email = null, CancellationToken ct = default)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VersoDbContext>();
        await new ExecutionEngine(db, printer, docx ?? new FakeDocxWriter(), combiner ?? new FakePdfCombiner(), email ?? new FakeEmailSender(), store, jobs, TimeProvider.System, NullLogger<ExecutionEngine>.Instance).RunAsync(executionId, ct);
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

    public TestDocumentStore(string? root = null)
    {
        Root = root ?? Path.Combine(Path.GetTempPath(), "verso-test-store", Guid.NewGuid().ToString("N"));
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

/// <summary>Stands in for the SMTP relay: remembers every message, or fails as told.</summary>
internal sealed class FakeEmailSender : IEmailSender
{
    public List<EmailMessage> Sent { get; } = [];
    public EmailSendException? Failure { get; set; }
    /// <summary>Fails this many sends (with <see cref="Failure"/>) and then works.</summary>
    public int FailTimes { get; set; } = int.MaxValue;
    private int _failed;

    public Task<string> SendAsync(EmailMessage message, CancellationToken ct)
    {
        if (Failure is not null && _failed < FailTimes)
        {
            _failed++;
            throw Failure;
        }

        Sent.Add(message);
        return Task.FromResult($"<fake-{Sent.Count}@verso>");
    }
}

/// <summary>Stands in for the PDF combiner: returns small unique bytes, and remembers the titles it was given, in order, and the bookmarks setting.</summary>
internal sealed class FakePdfCombiner : IPdfCombiner
{
    private int _calls;

    public int Calls => _calls;
    public List<string> LastTitles { get; } = [];
    public bool? LastBookmarks { get; private set; }

    public CombinedPdf Combine(IReadOnlyList<PdfPart> parts, bool bookmarks)
    {
        var call = Interlocked.Increment(ref _calls);
        LastTitles.Clear();
        LastTitles.AddRange(parts.Select(p => p.Title));
        LastBookmarks = bookmarks;
        return new CombinedPdf(Encoding.ASCII.GetBytes($"%PDF-1.7\n% fake combined {call} parts={parts.Count} {Guid.NewGuid():N}\n%%EOF\n"), parts.Count);
    }
}

/// <summary>Stands in for the Word writer: returns small unique bytes per call, and remembers what it was asked to write.</summary>
internal sealed class FakeDocxWriter : IDocxWriter
{
    private int _calls;

    public int Calls => _calls;

    public byte[] Write(ResolvedDocument document, DocxProtection protection)
    {
        var call = Interlocked.Increment(ref _calls);
        return Encoding.ASCII.GetBytes($"PK fake docx {call} {protection} {Guid.NewGuid():N}");
    }
}
