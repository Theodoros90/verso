using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Verso.Api.Tests;

/// <summary>
/// The XML invoice batch sample (tests/fixtures/xml-batch, docs/samples/xml-invoice-batch.md): one XML file with three invoices goes
/// through Parse, For each, Map fields, Transform, Validate, two If / else steps, an Approval, two PDF templates, Combine PDFs and Archive.
/// Runs the real engine with a fake printer; the two-page layout is checked in real Chromium by Verso.Pdf.Tests.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class XmlInvoiceBatchSampleTests(MigratedDatabaseFixture fixture) : IDisposable
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

    private static string Fixture(string name) => ExecutionTestKit.Fixture("xml-batch/" + name);

    private async Task<JsonElement> SendJsonAsync(HttpMethod method, string uri, string? etag, JsonNode? body, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(method, new Uri(uri, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, text);
        // The body plus the response's ETag (as "_etag"), which the next save or publish needs as If-Match.
        var node = JsonNode.Parse(text)!.AsObject();
        node["_etag"] = response.Headers.ETag?.ToString();
        return JsonSerializer.Deserialize<JsonElement>(node.ToJsonString());
    }

    private async Task<Guid> PublishTemplateAsync(string layoutFixture, string name)
    {
        var key = "X" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var created = await SendJsonAsync(HttpMethod.Post, "/api/v1/templates", null, new JsonObject { ["key"] = key, ["name"] = name }, HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        var draft = await SendJsonAsync(HttpMethod.Get, $"/api/v1/templates/{id}/draft", null, null, HttpStatusCode.OK);
        var sample = JsonNode.Parse(Fixture("sample-data.json"))!.AsObject();
        sample.Remove("_comment");
        var body = new JsonObject
        {
            ["pageSettings"] = new JsonObject { ["size"] = "A4", ["orientation"] = "portrait", ["marginsMm"] = new JsonObject { ["top"] = 18, ["right"] = 16, ["bottom"] = 18, ["left"] = 16 } },
            ["layout"] = JsonNode.Parse(Fixture(layoutFixture)),
            ["dataSchema"] = new JsonObject { ["type"] = "object" },
            ["sampleData"] = sample,
        };
        var saved = await SendJsonAsync(HttpMethod.Put, $"/api/v1/templates/{id}/draft", draft.GetProperty("_etag").GetString(), body, HttpStatusCode.OK);
        await SendJsonAsync(HttpMethod.Post, $"/api/v1/templates/{id}/draft/publish", saved.GetProperty("_etag").GetString(), null, HttpStatusCode.OK);
        return id;
    }

    /// <summary>Publishes both templates and the sample workflow (as tools/samples/load-xml-invoice-batch.ps1 does). Returns the workflow key.</summary>
    private async Task<string> PublishSampleAsync()
    {
        var invoiceId = await PublishTemplateAsync("invoice.layout.json", "XML batch invoice");
        var noticeId = await PublishTemplateAsync("notice.layout.json", "XML batch VAT notice");
        var definition = JsonNode.Parse(Fixture("workflow.json").Replace("__INVOICE_TEMPLATE_ID__", invoiceId.ToString(), StringComparison.Ordinal)
            .Replace("__NOTICE_TEMPLATE_ID__", noticeId.ToString(), StringComparison.Ordinal))!.AsObject();
        definition.Remove("_comment");

        var key = "xml-" + Guid.NewGuid().ToString("N")[..12];
        var created = await SendJsonAsync(HttpMethod.Post, "/api/v1/workflows", null, new JsonObject { ["key"] = key, ["name"] = "XML invoice batch" }, HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        var draft = await SendJsonAsync(HttpMethod.Get, $"/api/v1/workflows/{id}/draft", null, null, HttpStatusCode.OK);
        var saved = await SendJsonAsync(HttpMethod.Put, $"/api/v1/workflows/{id}/draft", draft.GetProperty("_etag").GetString(), new JsonObject { ["definition"] = definition }, HttpStatusCode.OK);
        await SendJsonAsync(HttpMethod.Post, $"/api/v1/workflows/{id}/draft/publish", saved.GetProperty("_etag").GetString(), null, HttpStatusCode.OK);
        return key;
    }

    private Task<Guid> SubmitXmlAsync(string key, string xml) => Kit.SubmitNewAsync(key, new JsonObject { ["content"] = xml });

    private static List<JsonElement> Steps(JsonElement execution, string nodeId) =>
        [.. execution.GetProperty("steps").EnumerateArray().Where(s => s.GetProperty("nodeId").GetString() == nodeId)];

    private static List<JsonElement> Succeeded(JsonElement execution, string nodeId) =>
        [.. Steps(execution, nodeId).Where(s => s.GetProperty("status").GetString() == "Succeeded")];

    private static IEnumerable<string> FileNames(JsonElement execution) =>
        execution.GetProperty("documents").EnumerateArray().Select(d => d.GetProperty("fileName").GetString()!).Order(StringComparer.Ordinal);

    [Fact]
    public async Task TheBatch_MakesTheRightPdfsPerInvoice_AndWaitsForTheApprovalOfTheLargeOne()
    {
        var key = await PublishSampleAsync();
        var id = await SubmitXmlAsync(key, Fixture("invoices.xml"));

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        // Invoices 1 and 2 are finished; invoice 3 (over 10,000) stops at the approval before any PDF is made for it.
        var waiting = await Kit.GetExecutionAsync(id);
        Assert.Equal("AwaitingApproval", waiting.GetProperty("status").GetString());
        Assert.Equal(3, Steps(waiting, "loop").Single().GetProperty("result").GetProperty("count").GetInt32());
        Assert.Equal(["INV-2026-004101.pdf", "INV-2026-004102-pack.pdf", "INV-2026-004102-vat-notice.pdf", "INV-2026-004102.pdf"], FileNames(waiting));
        Assert.Equal(3, _printer.Calls); // invoice 1; invoice 2 and its notice

        // If / else: the domestic invoice archives its own PDF; the German one is combined (invoice + notice) and the pack is archived.
        // (Each loop item has its own step rows, including Pending ones for the paths it did not take.)
        Assert.Equal([1], Succeeded(waiting, "archive").Select(s => s.GetProperty("iteration").GetInt32()));      // invoice 1
        Assert.Equal([2], Succeeded(waiting, "combine").Select(s => s.GetProperty("iteration").GetInt32()));      // invoice 2
        Assert.Equal([2], Succeeded(waiting, "archive-pack").Select(s => s.GetProperty("iteration").GetInt32())); // invoice 2
        Assert.Equal(["no", "no", "yes"], Succeeded(waiting, "big").Select(s => s.GetProperty("outputPort").GetString()));
        Assert.Equal(["no", "yes"], Succeeded(waiting, "foreign").Select(s => s.GetProperty("outputPort").GetString()));
        Assert.Equal("Waiting", Assert.Single(Steps(waiting, "approve"), s => s.GetProperty("iteration").GetInt32() == 3).GetProperty("status").GetString());

        // A person with the Operator role approves (inside a For each the decision names the loop item); the run continues with invoice 3 and finishes.
        using var operatorClient = fixture.Factory.CreateClientAs("dev.operator");
        using var decision = await operatorClient.PostAsJsonAsync(new Uri($"/api/v1/executions/{id}/approvals/approve", UriKind.Relative), new { decision = "approved", comment = "Checked against the meter data", iteration = 3 });
        Assert.Equal(HttpStatusCode.Accepted, decision.StatusCode);
        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var done = await Kit.GetExecutionAsync(id);
        Assert.True(done.GetProperty("status").GetString() == "Succeeded", done.GetRawText());
        Assert.Equal(
            ["INV-2026-004101.pdf", "INV-2026-004102-pack.pdf", "INV-2026-004102-vat-notice.pdf", "INV-2026-004102.pdf", "INV-2026-004103.pdf"],
            FileNames(done));
        Assert.Equal(4, _printer.Calls);
        // Every invoice and the pack are archived; the invoice and notice that went into the pack are not archived on their own.
        var documents = done.GetProperty("documents").EnumerateArray().ToList();
        Assert.Equal(
            ["INV-2026-004101.pdf", "INV-2026-004102-pack.pdf", "INV-2026-004103.pdf"],
            documents.Where(d => d.GetProperty("archive").ValueKind == JsonValueKind.Object).Select(d => d.GetProperty("fileName").GetString()!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnInvoiceThatFailsValidation_RejectsTheRun_WithoutAnyPdfForIt()
    {
        var key = await PublishSampleAsync();
        var xml = Fixture("invoices.xml").Replace("ap@kestrel-logistics.example", "not-an-email", StringComparison.Ordinal);
        var id = await SubmitXmlAsync(key, xml);

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Rejected", e.GetProperty("status").GetString());
        Assert.Equal("invalid", Assert.Single(Steps(e, "validate")).GetProperty("outputPort").GetString());
        Assert.Empty(e.GetProperty("documents").EnumerateArray());
        Assert.Equal(0, _printer.Calls);
    }

    [Fact]
    public async Task ContentThatIsNotXml_RejectsTheRun()
    {
        var key = await PublishSampleAsync();
        var id = await SubmitXmlAsync(key, "<InvoiceBatch><Invoice></InvoiceBatch>");

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Rejected", e.GetProperty("status").GetString());
        Assert.Equal("failed", Assert.Single(Steps(e, "parse")).GetProperty("outputPort").GetString());
        Assert.Equal(0, _printer.Calls);
    }
}
