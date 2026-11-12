using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Verso.Api.Tests;

/// <summary>M6 acceptance through the real API and database.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class WorkflowApiTests(MigratedDatabaseFixture fixture)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client = fixture.Factory.CreateClient();

    [Fact]
    public async Task NodeTypes_ListsTheCatalog_WithPortsSchemaAndDefaults()
    {
        using var response = await _client.GetAsync(new Uri("/api/v1/node-types", UriKind.Relative));
        var types = (await ReadAsync(response)).EnumerateArray().ToList();

        Assert.Equal(["api-trigger", "erp-connector", "file-upload", "sftp-folder", "scheduled-trigger", "manual-trigger", "folder-input", "parse", "map-fields", "transform", "validate", "generate-pdf", "archive"], types.Select(t => t.GetProperty("type").GetString()));
        var validate = types.Single(t => t.GetProperty("type").GetString() == "validate");
        Assert.Equal("process", validate.GetProperty("category").GetString());
        Assert.Equal(["valid", "invalid"], validate.GetProperty("outputs").EnumerateArray().Select(p => p.GetProperty("id").GetString()));
        Assert.False(validate.GetProperty("outputs")[1].GetProperty("required").GetBoolean());
        Assert.Equal("object", validate.GetProperty("configSchema").GetProperty("type").GetString());
        Assert.Equal("latest-published", types.Single(t => t.GetProperty("type").GetString() == "generate-pdf").GetProperty("defaults").GetProperty("templateVersion").GetString());
    }

    [Fact]
    public async Task Create_StartsWithATriggerOnlyDraft_AndValidatesTheKey()
    {
        var key = NewKey();
        using var created = await _client.PostAsJsonAsync(new Uri("/api/v1/workflows", UriKind.Relative), new { key, name = "Invoice standard" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await ReadAsync(created)).GetProperty("id").GetGuid();

        var draft = await GetDraftAsync(id);
        var nodes = draft.Body.GetProperty("definition").GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal("api-trigger", Assert.Single(nodes).GetProperty("type").GetString());

        using var duplicate = await _client.PostAsJsonAsync(new Uri("/api/v1/workflows", UriKind.Relative), new { key, name = "Again" });
        Assert.Equal("workflow.key_taken", (await ReadAsync(duplicate)).GetProperty("code").GetString());

        using var badKey = await _client.PostAsJsonAsync(new Uri("/api/v1/workflows", UriKind.Relative), new { key = "Invoice Standard", name = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, badKey.StatusCode);
        Assert.True((await ReadAsync(badKey)).GetProperty("errors").TryGetProperty("key", out _));
    }

    [Fact]
    public async Task AcceptanceScenario_InvoiceStandard_DraftTemplateRejected_ThenPublished_AndRoundTripsExactly()
    {
        var (templateId, templateEtag) = await CreateInvoiceTemplateAsync();
        var id = await CreateWorkflowAsync();
        var definition = InvoiceStandard(templateId);

        // Save: graph problems don't block saving a draft.
        var save = await PutDraftAsync(id, (await GetDraftAsync(id)).ETag, definition);
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        // Reload: positions and settings come back exactly as saved.
        var reloaded = await GetDraftAsync(id);
        Assert.True(JsonNode.DeepEquals(definition, JsonNode.Parse(reloaded.Body.GetProperty("definition").GetRawText())));

        // The template has only a draft: publishing the workflow is rejected, with the issue on the PDF node.
        using (var rejected = await PublishAsync(id, reloaded.ETag))
        {
            var problem = await ReadAsync(rejected);
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            Assert.Equal("workflow.invalid", problem.GetProperty("code").GetString());
            Assert.Contains(problem.GetProperty("issues").EnumerateArray(), i =>
                i.GetProperty("code").GetString() == "template.not_published" && i.GetProperty("nodeId").GetString() == "pdf");
        }

        // Publish the template, then the workflow publishes as version 1.
        using (var publishTemplate = await SendWithIfMatchAsync(HttpMethod.Post, $"/api/v1/templates/{templateId}/draft/publish", templateEtag))
        {
            Assert.Equal(HttpStatusCode.OK, publishTemplate.StatusCode);
        }

        using var published = await PublishAsync(id, reloaded.ETag);
        var version = await ReadAsync(published);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        Assert.Equal(1, version.GetProperty("versionNumber").GetInt32());
        Assert.Equal("Published", version.GetProperty("status").GetString());
        Assert.Equal(64, version.GetProperty("contentHash").GetString()!.Length);

        // Published versions are frozen: no draft until a new one is started.
        using (var noDraft = await _client.GetAsync(new Uri($"/api/v1/workflows/{id}/draft", UriKind.Relative)))
        {
            Assert.Equal("workflow.no_draft", (await ReadAsync(noDraft)).GetProperty("code").GetString());
        }

        using var started = await _client.PostAsync(new Uri($"/api/v1/workflows/{id}/draft", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        Assert.True(JsonNode.DeepEquals(definition, JsonNode.Parse((await ReadAsync(started)).GetProperty("definition").GetRawText())));

        // History and audit
        using var versions = await _client.GetAsync(new Uri($"/api/v1/workflows/{id}/versions", UriKind.Relative));
        Assert.Equal(["Draft", "Published"], (await ReadAsync(versions)).EnumerateArray().Select(v => v.GetProperty("status").GetString()));
        using var audit = await _client.GetAsync(new Uri($"/api/v1/audit?entityType=Workflow&entityId={id}", UriKind.Relative));
        Assert.Equal(["WorkflowCreated", "WorkflowDraftSaved", "WorkflowPublished", "WorkflowDraftCreated"],
            (await ReadAsync(audit)).GetProperty("items").EnumerateArray().Select(a => a.GetProperty("action").GetString()));
    }

    [Fact]
    public async Task Validate_ReportsPerNodeIssues_ForSavedOrUnsavedDefinitions()
    {
        var (templateId, _) = await CreateInvoiceTemplateAsync();
        var id = await CreateWorkflowAsync();

        // Saved draft (trigger only): its required output isn't connected.
        using (var saved = await _client.PostAsync(new Uri($"/api/v1/workflows/{id}/draft/validate", UriKind.Relative), null))
        {
            var result = await ReadAsync(saved);
            Assert.False(result.GetProperty("valid").GetBoolean());
            Assert.Contains(result.GetProperty("issues").EnumerateArray(), i => i.GetProperty("nodeId").GetString() == "trigger" && i.GetProperty("code").GetString() == "port.unconnected");
        }

        // Unsaved edits: disconnected archive node.
        var definition = InvoiceStandard(templateId);
        definition["edges"]!.AsArray().RemoveAt(2);
        using var unsaved = await _client.PostAsJsonAsync(new Uri($"/api/v1/workflows/{id}/draft/validate", UriKind.Relative), new { definition });
        var issues = (await ReadAsync(unsaved)).GetProperty("issues").EnumerateArray().ToList();
        Assert.Contains(issues, i => i.GetProperty("nodeId").GetString() == "archive" && i.GetProperty("code").GetString() == "node.unreachable");
        Assert.Contains(issues, i => i.GetProperty("nodeId").GetString() == "pdf" && i.GetProperty("port").GetString() == "success");
    }

    [Fact]
    public async Task MapFields_WarnsAboutTemplateFieldsNoRowMaps_ReadFromThePublishedTemplate()
    {
        var model = JsonNode.Parse("""
            { "type": "object", "properties": {
                "invoice": { "type": "object", "properties": { "number": { "type": "string" } } },
                "customer": { "type": "object", "properties": { "name": { "type": "string" } } },
                "lines": { "type": "array", "items": { "type": "object", "properties": { "amount": { "type": "number" } } } } } }
            """);
        var (templateId, etag) = await CreateInvoiceTemplateAsync(model);
        var id = await CreateWorkflowAsync();
        var definition = InvoiceStandard(templateId);
        var map = JsonNode.Parse("""
            { "id": "map", "type": "map-fields", "title": "Map fields", "position": { "x": 130, "y": 200 },
              "config": { "rows": [ { "from": "no", "to": "invoice.number", "transform": "as-is" } ] } }
            """)!;
        definition["nodes"]!.AsArray().Add(map);
        definition["edges"]!.AsArray().First(e => (string?)e!["target"] == "validate")!["target"] = "map";
        definition["edges"]!.AsArray().Add(JsonNode.Parse("""{ "id": "e9", "source": "map", "sourcePort": "success", "target": "validate", "targetPort": "in" }"""));

        async Task<string?> WarningAsync()
        {
            using var response = await _client.PostAsJsonAsync(new Uri($"/api/v1/workflows/{id}/draft/validate", UriKind.Relative), new { definition });
            return (await ReadAsync(response)).GetProperty("issues").EnumerateArray()
                .FirstOrDefault(i => i.GetProperty("code").GetString() == "map.unmapped") is { ValueKind: JsonValueKind.Object } w ? w.GetProperty("message").GetString() : null;
        }

        // A template with no published version has no model to compare with yet.
        Assert.Null(await WarningAsync());

        using (var publishTemplate = await SendWithIfMatchAsync(HttpMethod.Post, $"/api/v1/templates/{templateId}/draft/publish", etag))
        {
            Assert.Equal(HttpStatusCode.OK, publishTemplate.StatusCode);
        }

        var message = await WarningAsync();
        Assert.NotNull(message);
        Assert.Contains("customer.name, lines", message, StringComparison.Ordinal);
        Assert.DoesNotContain("invoice.number", message, StringComparison.Ordinal);
        Assert.Contains("Commercial invoice", message, StringComparison.Ordinal);

        // Mapping the rest silences it.
        map["config"]!["rows"]!.AsArray().Add(JsonNode.Parse("""{ "from": "n", "to": "customer.name", "transform": "as-is" }"""));
        map["config"]!["rows"]!.AsArray().Add(JsonNode.Parse("""{ "from": "l", "to": "lines", "transform": "as-is" }"""));
        Assert.Null(await WarningAsync());
    }

    [Fact]
    public async Task TransformData_NeedsRules_EachWithWhatItsOperationReads()
    {
        var (templateId, _) = await CreateInvoiceTemplateAsync(null);
        var id = await CreateWorkflowAsync();
        var definition = InvoiceStandard(templateId);
        var transform = JsonNode.Parse("""{ "id": "transform", "type": "transform", "title": "Transform data", "position": { "x": 130, "y": 200 }, "config": { "rules": [] } }""")!;
        definition["nodes"]!.AsArray().Add(transform);
        definition["edges"]!.AsArray().First(e => (string?)e!["target"] == "validate")!["target"] = "transform";
        definition["edges"]!.AsArray().Add(JsonNode.Parse("""{ "id": "e8", "source": "transform", "sourcePort": "success", "target": "validate", "targetPort": "in" }"""));

        async Task<List<JsonElement>> ErrorsAsync()
        {
            using var response = await _client.PostAsJsonAsync(new Uri($"/api/v1/workflows/{id}/draft/validate", UriKind.Relative), new { definition });
            return [.. (await ReadAsync(response)).GetProperty("issues").EnumerateArray()
                .Where(i => i.GetProperty("nodeId").GetString() == "transform" && i.GetProperty("severity").GetString() == "error")
                .Select(i => i.Clone())];
        }

        Assert.Contains(await ErrorsAsync(), i => i.GetProperty("code").GetString() == "config.required");

        transform["config"] = JsonNode.Parse("""
            { "rules": [ { "target": "totals.gross", "operation": "add", "a": "totals.net", "b": "" },
                         { "target": "totals.vat", "operation": "round", "a": "totals.vat", "b": "" } ] }
            """);
        var errors = await ErrorsAsync();
        var error = Assert.Single(errors);
        Assert.Equal("rules[0]", error.GetProperty("field").GetString());
        Assert.Contains("Rule 1", error.GetProperty("message").GetString(), StringComparison.Ordinal);

        transform["config"]!["rules"]![0]!["b"] = "totals.vat";
        Assert.Empty(await ErrorsAsync());
    }

    [Fact]
    public async Task Save_RejectsStructurallyBrokenDefinitions_AndRequiresIfMatch()
    {
        var id = await CreateWorkflowAsync();
        var etag = (await GetDraftAsync(id)).ETag;

        using (var noIfMatch = await PutDraftAsync(id, null, InvoiceStandard(Guid.NewGuid())))
        {
            Assert.Equal(HttpStatusCode.PreconditionRequired, noIfMatch.StatusCode);
        }

        using (var broken = await PutDraftAsync(id, etag, JsonNode.Parse("""{ "nodes": [ { "id": "x" } ], "edges": [] }""")!))
        {
            Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
            Assert.True((await ReadAsync(broken)).GetProperty("errors").TryGetProperty("definition.nodes[0].type", out _));
        }

        using (var ok = await PutDraftAsync(id, etag, InvoiceStandard(Guid.NewGuid())))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var stale = await PutDraftAsync(id, etag, InvoiceStandard(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task List_ShowsPublishStateAndDrafts()
    {
        var id = await CreateWorkflowAsync();

        using var response = await _client.GetAsync(new Uri("/api/v1/workflows?pageSize=100", UriKind.Relative));
        var item = (await ReadAsync(response)).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == id);

        Assert.True(item.GetProperty("hasDraft").GetBoolean());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("latestPublishedVersion").ValueKind);
    }

    // ----- Helpers -----

    private static string NewKey() => "wf-" + Guid.NewGuid().ToString("N")[..10];

    private static JsonObject InvoiceStandard(Guid templateId) => (JsonObject)JsonNode.Parse($$"""
        { "nodes": [
            { "id": "trigger", "type": "api-trigger", "title": "API trigger", "position": { "x": 0, "y": 80 }, "config": { "path": "/hooks/invoices", "auth": "hmac-sha256" } },
            { "id": "validate", "type": "validate", "title": "Validate invoice data", "position": { "x": 262.5, "y": 80 },
              "config": { "required": ["invoice.number", "customer.name"], "types": [ { "field": "invoice.date", "type": "date" } ] } },
            { "id": "pdf", "type": "generate-pdf", "title": "Generate invoice PDF", "position": { "x": 520, "y": 96.25 },
              "config": { "templateId": "{{templateId}}", "templateVersion": "latest-published", "fileName": "{{"{{"}}invoice.number{{"}}"}}.pdf" } },
            { "id": "archive", "type": "archive", "title": "Archive", "position": { "x": 780, "y": 80 },
              "config": { "folderPattern": "invoices/{{"{{"}}invoice.date|year{{"}}"}}", "retention": "10y", "metadata": [ { "key": "InvoiceNumber", "value": "{{"{{"}}invoice.number{{"}}"}}" } ] } } ],
          "edges": [
            { "id": "e1", "source": "trigger", "sourcePort": "out", "target": "validate", "targetPort": "in" },
            { "id": "e2", "source": "validate", "sourcePort": "valid", "target": "pdf", "targetPort": "in" },
            { "id": "e3", "source": "pdf", "sourcePort": "success", "target": "archive", "targetPort": "in" } ] }
        """)!;

    private async Task<Guid> CreateWorkflowAsync()
    {
        using var response = await _client.PostAsJsonAsync(new Uri("/api/v1/workflows", UriKind.Relative), new { key = NewKey(), name = "Invoice standard" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>A template with the invoice layout saved in its draft (not published). Returns the draft ETag.</summary>
    private async Task<(Guid Id, string ETag)> CreateInvoiceTemplateAsync(JsonNode? dataSchema = null)
    {
        var key = "W" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        using var create = await _client.PostAsJsonAsync(new Uri("/api/v1/templates", UriKind.Relative), new { key, name = "Commercial invoice" });
        var id = (await ReadAsync(create)).GetProperty("id").GetGuid();
        using var draft = await _client.GetAsync(new Uri($"/api/v1/templates/{id}/draft", UriKind.Relative));
        var body = new JsonObject
        {
            ["pageSettings"] = new JsonObject { ["size"] = "A4", ["orientation"] = "portrait", ["marginsMm"] = new JsonObject { ["top"] = 18, ["right"] = 16, ["bottom"] = 18, ["left"] = 16 } },
            ["layout"] = JsonNode.Parse(Fixture("invoice/layout.json")),
            ["dataSchema"] = dataSchema ?? new JsonObject { ["type"] = "object" },
            ["sampleData"] = JsonNode.Parse(Fixture("invoice/sample.json")),
        };
        var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"/api/v1/templates/{id}/draft", UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("If-Match", draft.Headers.ETag!.ToString());
        using var saved = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        return (id, saved.Headers.ETag!.ToString());
    }

    private static string Fixture(string relative) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", relative));

    private async Task<(JsonElement Body, string ETag)> GetDraftAsync(Guid id)
    {
        using var response = await _client.GetAsync(new Uri($"/api/v1/workflows/{id}/draft", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync(response), response.Headers.ETag!.ToString());
    }

    private Task<HttpResponseMessage> PutDraftAsync(Guid id, string? etag, JsonNode definition)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"/api/v1/workflows/{id}/draft", UriKind.Relative))
        {
            Content = JsonContent.Create(new JsonObject { ["definition"] = definition.DeepClone() }),
        };
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> PublishAsync(Guid id, string etag) => SendWithIfMatchAsync(HttpMethod.Post, $"/api/v1/workflows/{id}/draft/publish", etag);

    private Task<HttpResponseMessage> SendWithIfMatchAsync(HttpMethod method, string uri, string etag)
    {
        var request = new HttpRequestMessage(method, new Uri(uri, UriKind.Relative));
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return _client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), Json);
}
