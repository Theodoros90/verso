using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Verso.Api.Tests;

[Collection(ApiTestGroup.Name)]
public sealed class PreviewApiTests(MigratedDatabaseFixture fixture)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly int[] NotAnObject = [1, 2];
    private readonly HttpClient _client = fixture.Factory.CreateClient();

    [Fact]
    public async Task Preview_WithSampleData_RendersTheInvoice_WithoutIssues()
    {
        var id = await CreateInvoiceTemplateAsync();

        using var response = await _client.PostAsJsonAsync(PreviewUri(id), new { });
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = body.GetProperty("html").GetString()!;
        Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
        Assert.Contains("Kestrel Logistics Ltd", html, StringComparison.Ordinal);
        Assert.Contains("€5,516.69", html, StringComparison.Ordinal);
        Assert.Contains("02 Dec 2026", html, StringComparison.Ordinal);
        Assert.Empty(body.GetProperty("issues").EnumerateArray());
    }

    [Fact]
    public async Task Preview_WithSuppliedData_ShowsPlaceholders_AndListsIssues()
    {
        var id = await CreateInvoiceTemplateAsync();

        using var response = await _client.PostAsJsonAsync(PreviewUri(id), new { data = new { invoice = new { number = "INV-X" }, lines = Array.Empty<object>() } });
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = body.GetProperty("html").GetString()!;
        Assert.Contains("INV-X", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"v-missing\">{{customer.name}}</span>", html, StringComparison.Ordinal);
        var issues = body.GetProperty("issues").EnumerateArray().ToList();
        Assert.Contains(issues, i =>
            i.GetProperty("code").GetString() == "binding.missing"
            && i.GetProperty("path").GetString() == "customer.name"
            && i.GetProperty("severity").GetString() == "error"
            && i.GetProperty("elementId").GetString() == "billto");
    }

    [Fact]
    public async Task Preview_WithoutBody_UsesSampleData()
    {
        var id = await CreateInvoiceTemplateAsync();

        using var response = await _client.PostAsync(PreviewUri(id), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Kestrel Logistics Ltd", (await ReadAsync(response)).GetProperty("html").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_RejectsNonObjectData()
    {
        var id = await CreateInvoiceTemplateAsync();

        using var response = await _client.PostAsJsonAsync(PreviewUri(id), new { data = NotAnObject });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await ReadAsync(response)).GetProperty("errors").TryGetProperty("data", out _));
    }

    [Fact]
    public async Task Preview_OfUnknownTemplate_Is404()
    {
        using var response = await _client.PostAsync(PreviewUri(Guid.NewGuid()), null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SaveDraft_RejectsInvalidLayouts_WithFieldPaths()
    {
        var id = await CreateTemplateAsync();
        var etag = await DraftETagAsync(id);
        var body = DraftBody(JsonNode.Parse("""
            { "layoutSchemaVersion": 1, "body": { "blocks": [
              { "id": "img", "type": "image", "props": { "src": "https://evil.example/logo.png" } },
              { "id": "t", "type": "text", "props": { "text": "x", "style": { "color": "red" } } } ] } }
            """)!, JsonNode.Parse("{}")!);

        using var response = await PutDraftAsync(id, etag, body);
        var problem = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", problem.GetProperty("code").GetString());
        var errors = problem.GetProperty("errors");
        Assert.True(errors.TryGetProperty("layout.body.blocks[0].props.src", out _));
        Assert.True(errors.TryGetProperty("layout.body.blocks[1].props.style.color", out _));
    }

    // ----- Helpers -----

    private static Uri PreviewUri(Guid id) => new($"/api/v1/templates/{id}/draft/preview/html", UriKind.Relative);

    private static string Fixture(string relative) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", relative));

    private async Task<Guid> CreateInvoiceTemplateAsync()
    {
        var id = await CreateTemplateAsync();
        var etag = await DraftETagAsync(id);
        var sample = JsonNode.Parse(Fixture("invoice/sample.json"))!;
        using var save = await PutDraftAsync(id, etag, DraftBody(JsonNode.Parse(Fixture("invoice/layout.json"))!, sample));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        return id;
    }

    private async Task<Guid> CreateTemplateAsync()
    {
        var key = "P" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        using var response = await _client.PostAsJsonAsync(new Uri("/api/v1/templates", UriKind.Relative), new { key, name = "Preview test" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private async Task<string> DraftETagAsync(Guid id)
    {
        using var response = await _client.GetAsync(new Uri($"/api/v1/templates/{id}/draft", UriKind.Relative));
        return response.Headers.ETag!.ToString();
    }

    private Task<HttpResponseMessage> PutDraftAsync(Guid id, string etag, JsonObject body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"/api/v1/templates/{id}/draft", UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return _client.SendAsync(request);
    }

    private static JsonObject DraftBody(JsonNode layout, JsonNode sampleData) => new()
    {
        ["pageSettings"] = new JsonObject
        {
            ["size"] = "A4",
            ["orientation"] = "portrait",
            ["marginsMm"] = new JsonObject { ["top"] = 18, ["right"] = 16, ["bottom"] = 18, ["left"] = 16 },
        },
        ["layout"] = layout,
        ["dataSchema"] = new JsonObject { ["type"] = "object" },
        ["sampleData"] = sampleData,
    };

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Json);
}
