using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Verso.Api.Tests;

/// <summary>Configuration packages (ADR 0010): export from one place, import into another, through the real API and database.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ConfigurationApiTests(MigratedDatabaseFixture fixture)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client = fixture.Factory.CreateClient();

    [Fact]
    public async Task Export_CarriesTemplatesAndWorkflows_WithTemplateKeysInsteadOfIds()
    {
        var (templateId, templateKey) = await CreatePublishedTemplateAsync();
        var workflowId = await CreateWorkflowAsync(templateId);

        var package = await ExportAsync([templateId], [workflowId], "Test");

        Assert.Equal("Verso configuration package", package.GetProperty("product").GetString());
        Assert.Equal(1, package.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Test", package.GetProperty("environment").GetString());
        var template = Assert.Single(package.GetProperty("templates").EnumerateArray());
        Assert.Equal(templateKey, template.GetProperty("key").GetString());
        Assert.True(template.GetProperty("published").GetBoolean());
        var pdf = Assert.Single(package.GetProperty("workflows").EnumerateArray())
            .GetProperty("definition").GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == "pdf").GetProperty("config");
        Assert.Equal(templateKey, pdf.GetProperty("templateKey").GetString());
        Assert.False(pdf.TryGetProperty("templateId", out _));
    }

    [Fact]
    public async Task Export_UnknownId_IsNotFound_AndUnknownEnvironmentIsRejected()
    {
        using var missing = await PostAsync("/api/v1/configuration/export", new { templateIds = new[] { Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("configuration.item_not_found", (await ReadAsync(missing)).GetProperty("code").GetString());

        using var badEnvironment = await PostAsync("/api/v1/configuration/export", new { environment = "Staging" });
        Assert.Equal(HttpStatusCode.BadRequest, badEnvironment.StatusCode);
    }

    [Fact]
    public async Task Import_CreatesTemplateAndWorkflow_RemapsTheTemplate_AndRepeatingItChangesNothing()
    {
        var (templateId, templateKey) = await CreatePublishedTemplateAsync();
        var workflowId = await CreateWorkflowAsync(templateId);
        var package = JsonNode.Parse((await ExportAsync([templateId], [workflowId], "Development")).GetRawText())!;

        // Pretend it arrives in another environment: new keys, so nothing exists yet.
        var newTemplateKey = "I" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var newWorkflowKey = "wf-" + Guid.NewGuid().ToString("N")[..10];
        package["templates"]![0]!["key"] = newTemplateKey;
        package["workflows"]![0]!["key"] = newWorkflowKey;
        package["workflows"]![0]!["definition"]!["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "pdf")!["config"]!["templateKey"] = newTemplateKey;

        using var dryRun = await PostAsync("/api/v1/configuration/import?dryRun=true", new { package });
        var preview = await ReadAsync(dryRun);
        Assert.Equal(HttpStatusCode.OK, dryRun.StatusCode);
        Assert.True(preview.GetProperty("dryRun").GetBoolean());
        Assert.Equal(["created", "created"], preview.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("outcome").GetString()));
        Assert.Empty(await FindWorkflowsAsync(newWorkflowKey)); // a dry run changes nothing

        using var applied = await PostAsync("/api/v1/configuration/import", new { package });
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal(["template", "workflow"], (await ReadAsync(applied)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("kind").GetString()));

        // The imported template is published (version 1), and the imported workflow points at the new template's id.
        var imported = Assert.Single(await FindAsync("/api/v1/templates", newTemplateKey));
        Assert.Equal(1, imported.GetProperty("latestPublishedVersion").GetInt32());

        var workflow = Assert.Single(await FindWorkflowsAsync(newWorkflowKey));
        Assert.True(workflow.GetProperty("hasDraft").GetBoolean()); // workflows arrive as drafts, never published
        using var draft = await _client.GetAsync(new Uri($"/api/v1/workflows/{workflow.GetProperty("id").GetGuid()}/draft", UriKind.Relative));
        var config = (await ReadAsync(draft)).GetProperty("definition").GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == "pdf").GetProperty("config");
        Assert.Equal(imported.GetProperty("id").GetGuid().ToString(), config.GetProperty("templateId").GetString());
        Assert.False(config.TryGetProperty("templateKey", out _));

        // Importing the same package again: the template is identical, the workflow still has its draft.
        using var again = await PostAsync("/api/v1/configuration/import", new { package });
        var outcomes = (await ReadAsync(again)).GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("kind").GetString()!, i => i.GetProperty("outcome").GetString());
        Assert.Equal("unchanged", outcomes["template"]);
        Assert.Equal("skipped", outcomes["workflow"]);

        // The existing source items are untouched by all of this.
        Assert.Equal(templateKey, (await ExportAsync([templateId], [], null)).GetProperty("templates")[0].GetProperty("key").GetString());
    }

    [Fact]
    public async Task Import_ChangedTemplate_BecomesTheNextPublishedVersion_AndAnExistingDraftIsNeverOverwritten()
    {
        var (templateId, _) = await CreatePublishedTemplateAsync();
        var package = JsonNode.Parse((await ExportAsync([templateId], [], null)).GetRawText())!;
        SetTitle(package, "Tax invoice");

        using var updated = await PostAsync("/api/v1/configuration/import", new { package });
        var item = Assert.Single((await ReadAsync(updated)).GetProperty("items").EnumerateArray());
        Assert.Equal("updated", item.GetProperty("outcome").GetString());
        Assert.Contains("version 2", item.GetProperty("message").GetString());

        // Someone starts a draft; the next import must not touch it.
        using var started = await _client.PostAsync(new Uri($"/api/v1/templates/{templateId}/draft", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        SetTitle(package, "Credit note");
        using var skipped = await PostAsync("/api/v1/configuration/import", new { package });
        Assert.Equal("skipped", (await ReadAsync(skipped)).GetProperty("items")[0].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Import_ReferenceToAMissingTemplate_IsAWarning_AndTheWorkflowStillImports()
    {
        var (templateId, _) = await CreatePublishedTemplateAsync();
        var workflowId = await CreateWorkflowAsync(templateId);
        var package = JsonNode.Parse((await ExportAsync([], [workflowId], null)).GetRawText())!;
        var key = "wf-" + Guid.NewGuid().ToString("N")[..10];
        package["workflows"]![0]!["key"] = key;
        package["workflows"]![0]!["definition"]!["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "pdf")!["config"]!["templateKey"] = "NOPE-NOT-HERE";

        using var response = await PostAsync("/api/v1/configuration/import", new { package });
        var result = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("NOPE-NOT-HERE", Assert.Single(result.GetProperty("warnings").EnumerateArray()).GetString());
        Assert.Single(await FindWorkflowsAsync(key));
    }

    [Fact]
    public async Task Import_Rejects_ForeignFiles_BadItems_AndDuplicates_WithoutChangingAnything()
    {
        using var foreign = await PostAsync("/api/v1/configuration/import", new { package = new { product = "Something else", schemaVersion = 1 } });
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.True((await ReadAsync(foreign)).GetProperty("errors").TryGetProperty("package", out _));

        var goodKey = "wf-" + Guid.NewGuid().ToString("N")[..10];
        var package = new JsonObject
        {
            ["product"] = "Verso configuration package",
            ["schemaVersion"] = 1,
            ["templates"] = new JsonArray(),
            ["workflows"] = new JsonArray
            {
                new JsonObject { ["key"] = goodKey, ["name"] = "Fine", ["definition"] = JsonNode.Parse("""{"nodes":[],"edges":[]}""") },
                new JsonObject { ["key"] = "Bad Key", ["name"] = "", ["definition"] = JsonNode.Parse("""{"nodes":[{"id":"x"}],"edges":[]}""") },
                new JsonObject { ["key"] = goodKey, ["name"] = "Again", ["definition"] = JsonNode.Parse("""{"nodes":[],"edges":[]}""") },
            },
        };
        using var bad = await PostAsync("/api/v1/configuration/import", new { package });
        var errors = (await ReadAsync(bad)).GetProperty("errors");

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.True(errors.TryGetProperty("package.workflows[1].key", out _));
        Assert.True(errors.TryGetProperty("package.workflows[1].name", out _));
        Assert.True(errors.TryGetProperty("package.workflows[2].key", out _));
        Assert.Empty(await FindWorkflowsAsync(goodKey)); // all or nothing

        using var unsupported = await PostAsync("/api/v1/configuration/import", new { package = new { product = "Verso configuration package", schemaVersion = 2 } });
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
    }

    [Fact]
    public async Task Import_CanLeaveWorkflowsOut()
    {
        var (templateId, _) = await CreatePublishedTemplateAsync();
        var workflowId = await CreateWorkflowAsync(templateId);
        var package = JsonNode.Parse((await ExportAsync([templateId], [workflowId], null)).GetRawText())!;
        var key = "wf-" + Guid.NewGuid().ToString("N")[..10];
        package["workflows"]![0]!["key"] = key;

        using var response = await PostAsync("/api/v1/configuration/import", new { package, includeWorkflows = false });

        Assert.Equal(["template"], (await ReadAsync(response)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("kind").GetString()));
        Assert.Empty(await FindWorkflowsAsync(key));
    }

    [Fact]
    public async Task ExportAndImport_AreAudited()
    {
        var (templateId, _) = await CreatePublishedTemplateAsync();
        var package = JsonNode.Parse((await ExportAsync([templateId], [], "Test")).GetRawText())!;
        package["templates"]![0]!["key"] = "A" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        using var _ = await PostAsync("/api/v1/configuration/import", new { package });

        using var audit = await _client.GetAsync(new Uri("/api/v1/audit?entityType=Configuration&newestFirst=true&pageSize=10", UriKind.Relative));
        var actions = (await ReadAsync(audit)).GetProperty("items").EnumerateArray().Select(a => a.GetProperty("action").GetString()).ToList();

        Assert.Contains("ConfigurationExported", actions);
        Assert.Contains("ConfigurationImported", actions);
    }

    [Fact]
    public async Task Import_NeedsAdministrator_ButExportOnlyNeedsARole()
    {
        using var author = fixture.Factory.CreateClientAs("dev.workflow-author");
        using var viewer = fixture.Factory.CreateClientAs("dev.viewer");
        var body = JsonContent.Create(new { package = new { product = "Verso configuration package", schemaVersion = 1 } });

        using var denied = await author.PostAsync(new Uri("/api/v1/configuration/import", UriKind.Relative), body);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var export = await viewer.PostAsync(new Uri("/api/v1/configuration/export", UriKind.Relative), JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
    }

    // ----- Helpers -----

    private static void SetTitle(JsonNode package, string text) =>
        package["templates"]![0]!["layout"]!["header"]!["elements"]!.AsArray().Single(e => e!["id"]!.GetValue<string>() == "title")!["props"]!["text"] = text;

    private async Task<HttpResponseMessage> PostAsync(string uri, object body) =>
        await _client.PostAsJsonAsync(new Uri(uri, UriKind.Relative), body);

    private async Task<JsonElement> ExportAsync(Guid[] templateIds, Guid[] workflowIds, string? environment)
    {
        using var response = await PostAsync("/api/v1/configuration/export", new { templateIds, workflowIds, environment });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync(response);
    }

    private Task<List<JsonElement>> FindWorkflowsAsync(string key) => FindAsync("/api/v1/workflows", key);

    /// <summary>Lists are paged (the database is shared by every test), so look through all the pages.</summary>
    private async Task<List<JsonElement>> FindAsync(string path, string key)
    {
        var found = new List<JsonElement>();
        for (var page = 1; ; page++)
        {
            using var response = await _client.GetAsync(new Uri($"{path}?page={page}&pageSize=100", UriKind.Relative));
            var body = await ReadAsync(response);
            var items = body.GetProperty("items").EnumerateArray().ToList();
            found.AddRange(items.Where(i => i.GetProperty("key").GetString() == key));
            if (found.Count > 0 || page * 100 >= body.GetProperty("total").GetInt32())
            {
                return found;
            }
        }
    }

    /// <summary>A template with the invoice layout, published as version 1.</summary>
    private async Task<(Guid Id, string Key)> CreatePublishedTemplateAsync()
    {
        var key = "C" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        using var create = await PostAsync("/api/v1/templates", new { key, name = "Commercial invoice" });
        var id = (await ReadAsync(create)).GetProperty("id").GetGuid();
        using var draft = await _client.GetAsync(new Uri($"/api/v1/templates/{id}/draft", UriKind.Relative));
        var body = new JsonObject
        {
            ["pageSettings"] = new JsonObject { ["size"] = "A4", ["orientation"] = "portrait", ["marginsMm"] = new JsonObject { ["top"] = 18, ["right"] = 16, ["bottom"] = 18, ["left"] = 16 } },
            ["layout"] = JsonNode.Parse(Fixture("invoice/layout.json")),
            ["dataSchema"] = new JsonObject { ["type"] = "object" },
            ["sampleData"] = JsonNode.Parse(Fixture("invoice/sample.json")),
        };
        var save = new HttpRequestMessage(HttpMethod.Put, new Uri($"/api/v1/templates/{id}/draft", UriKind.Relative)) { Content = JsonContent.Create(body) };
        save.Headers.TryAddWithoutValidation("If-Match", draft.Headers.ETag!.ToString());
        using var saved = await _client.SendAsync(save);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var publish = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/templates/{id}/draft/publish", UriKind.Relative));
        publish.Headers.TryAddWithoutValidation("If-Match", saved.Headers.ETag!.ToString());
        using var published = await _client.SendAsync(publish);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        return (id, key);
    }

    private async Task<Guid> CreateWorkflowAsync(Guid templateId)
    {
        var key = "wf-" + Guid.NewGuid().ToString("N")[..10];
        using var create = await PostAsync("/api/v1/workflows", new { key, name = "Invoice standard" });
        var id = (await ReadAsync(create)).GetProperty("id").GetGuid();
        using var draft = await _client.GetAsync(new Uri($"/api/v1/workflows/{id}/draft", UriKind.Relative));
        var definition = JsonNode.Parse($$"""
            { "nodes": [
                { "id": "trigger", "type": "api-trigger", "title": "API trigger", "position": { "x": 0, "y": 80 }, "config": { "path": "/hooks/invoices", "auth": "hmac-sha256" } },
                { "id": "pdf", "type": "generate-pdf", "title": "Generate invoice PDF", "position": { "x": 260, "y": 80 },
                  "config": { "templateId": "{{templateId}}", "templateVersion": "latest-published", "fileName": "{{"{{"}}invoice.number{{"}}"}}.pdf" } } ],
              "edges": [ { "id": "e1", "source": "trigger", "sourcePort": "out", "target": "pdf", "targetPort": "in" } ] }
            """);
        var save = new HttpRequestMessage(HttpMethod.Put, new Uri($"/api/v1/workflows/{id}/draft", UriKind.Relative))
        {
            Content = JsonContent.Create(new JsonObject { ["definition"] = definition }),
        };
        save.Headers.TryAddWithoutValidation("If-Match", draft.Headers.ETag!.ToString());
        using var saved = await _client.SendAsync(save);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        return id;
    }

    private static string Fixture(string relative) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", relative));

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), Json);
}
