using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Verso.Api.Tests;

[Collection(ApiTestGroup.Name)]
public sealed class TemplateApiTests(MigratedDatabaseFixture fixture)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client = fixture.Factory.CreateClient();

    // ----- The M2 acceptance scenario, end to end -----

    [Fact]
    public async Task AcceptanceScenario_CreateEditPublishRevisePublish_WithHistoryAndAudit()
    {
        // Create template → it starts with an empty draft.
        var template = await CreateTemplateAsync();
        var id = template.GetProperty("id").GetGuid();
        var (draft, etag0) = await GetDraftAsync(id);
        Assert.Equal("Draft", draft.GetProperty("status").GetString());

        // Save draft twice; each save returns a new ETag.
        using var save1 = await PutDraftAsync(id, etag0, DraftBody("first"));
        Assert.Equal(HttpStatusCode.OK, save1.StatusCode);
        var etag1 = ETagOf(save1);
        Assert.NotEqual(etag0, etag1);

        using var save2 = await PutDraftAsync(id, etag1, DraftBody("second"));
        Assert.Equal(HttpStatusCode.OK, save2.StatusCode);
        var etag2 = ETagOf(save2);
        Assert.NotEqual(etag1, etag2);
        Assert.Equal("second", (await ReadAsync(save2)).GetProperty("layout").GetProperty("marker").GetString());

        // A stale If-Match is rejected and changes nothing.
        using var stale = await PutDraftAsync(id, etag1, DraftBody("lost update"));
        await AssertProblemAsync(stale, HttpStatusCode.PreconditionFailed, "precondition.failed");
        Assert.Equal("second", (await GetDraftAsync(id)).Draft.GetProperty("layout").GetProperty("marker").GetString());

        // Publish → v1, frozen.
        using var publish1 = await PublishAsync(id, etag2);
        Assert.Equal(HttpStatusCode.OK, publish1.StatusCode);
        var v1 = await ReadAsync(publish1);
        Assert.Equal(1, v1.GetProperty("versionNumber").GetInt32());
        Assert.Equal("Published", v1.GetProperty("status").GetString());
        Assert.Matches("^[0-9a-f]{64}$", v1.GetProperty("contentHash").GetString());

        // PUT after publishing → 409: published versions can't be changed.
        using var putPublished = await PutDraftAsync(id, ETagOf(publish1), DraftBody("edit published"));
        await AssertProblemAsync(putPublished, HttpStatusCode.Conflict, "template.no_draft");
        using var v1Again = await _client.GetAsync(new Uri($"/api/v1/templates/{id}/versions/{v1.GetProperty("versionId")}", UriKind.Relative));
        Assert.Equal("second", (await ReadAsync(v1Again)).GetProperty("layout").GetProperty("marker").GetString());

        // Second draft (copied from v1) → edit → publish v2.
        using var start = await _client.PostAsync(new Uri($"/api/v1/templates/{id}/draft", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Created, start.StatusCode);
        Assert.Equal("second", (await ReadAsync(start)).GetProperty("layout").GetProperty("marker").GetString());
        using var save3 = await PutDraftAsync(id, ETagOf(start), DraftBody("third"));
        using var publish2 = await PublishAsync(id, ETagOf(save3));
        Assert.Equal(2, (await ReadAsync(publish2)).GetProperty("versionNumber").GetInt32());

        // History lists both versions, newest first.
        using var history = await _client.GetAsync(new Uri($"/api/v1/templates/{id}/versions", UriKind.Relative));
        var versions = (await ReadAsync(history)).EnumerateArray().ToList();
        Assert.Equal([2, 1], versions.Select(v => v.GetProperty("versionNumber").GetInt32()));
        Assert.All(versions, v => Assert.Equal("Published", v.GetProperty("status").GetString()));
        Assert.NotEqual(versions[0].GetProperty("contentHash").GetString(), versions[1].GetProperty("contentHash").GetString());

        // Audit shows each successful action, in order, by the development user. The rejected save isn't there.
        using var audit = await _client.GetAsync(new Uri($"/api/v1/audit?entityType=Template&entityId={id}", UriKind.Relative));
        var entries = (await ReadAsync(audit)).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(
            ["TemplateCreated", "TemplateDraftSaved", "TemplateDraftSaved", "TemplatePublished",
             "TemplateDraftCreated", "TemplateDraftSaved", "TemplatePublished"],
            entries.Select(e => e.GetProperty("action").GetString()));
        Assert.All(entries, e => Assert.Equal("dev.user", e.GetProperty("actorId").GetString()));
        Assert.Equal(2, entries[^1].GetProperty("details").GetProperty("versionNumber").GetInt32());
    }

    // ----- Focused rules -----

    [Fact]
    public async Task Create_Returns201WithLocation_AndTheTemplate()
    {
        var key = NewKey();
        using var response = await _client.PostAsJsonAsync(new Uri("/api/v1/templates", UriKind.Relative), new { key, name = "Commercial invoice" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal($"/api/v1/templates/{body.GetProperty("id")}", response.Headers.Location!.OriginalString);
        Assert.Equal(key, body.GetProperty("key").GetString());
        Assert.Equal("Invoice", body.GetProperty("documentType").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("latestPublishedVersion").ValueKind);
        Assert.Equal(JsonValueKind.String, body.GetProperty("draftVersionId").ValueKind);
    }

    [Fact]
    public async Task Create_RejectsDuplicateKeys()
    {
        var key = NewKey();
        await CreateTemplateAsync(key);

        using var response = await _client.PostAsJsonAsync(new Uri("/api/v1/templates", UriKind.Relative), new { key, name = "Again" });

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "template.key_taken");
    }

    [Fact]
    public async Task Create_ValidatesFields()
    {
        using var response = await _client.PostAsJsonAsync(new Uri("/api/v1/templates", UriKind.Relative), new { key = "lower case", name = "", documentType = "Letter" });

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
        var errors = problem.GetProperty("errors");
        Assert.True(errors.TryGetProperty("key", out _));
        Assert.True(errors.TryGetProperty("name", out _));
        Assert.True(errors.TryGetProperty("documentType", out _));
    }

    [Fact]
    public async Task SaveDraft_RequiresIfMatch()
    {
        var id = (await CreateTemplateAsync()).GetProperty("id").GetGuid();

        using var response = await PutDraftAsync(id, etag: null, DraftBody("x"));

        await AssertProblemAsync(response, HttpStatusCode.PreconditionRequired, "precondition.required");
    }

    [Fact]
    public async Task SaveDraft_ValidatesContent()
    {
        var id = (await CreateTemplateAsync()).GetProperty("id").GetGuid();
        var (_, etag) = await GetDraftAsync(id);
        var body = DraftBody("x");
        body["pageSettings"]!["size"] = "Letter";
        body["layout"] = new JsonObject { ["layoutSchemaVersion"] = 2 };
        body["sampleData"] = new JsonArray();

        using var response = await PutDraftAsync(id, etag, body);

        var errors = (await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation")).GetProperty("errors");
        Assert.True(errors.TryGetProperty("pageSettings.size", out _));
        Assert.True(errors.TryGetProperty("layout.layoutSchemaVersion", out _));
        Assert.True(errors.TryGetProperty("sampleData", out _));
    }

    [Fact]
    public async Task Publish_WithStaleETag_IsRejected_AndNothingIsPublished()
    {
        var id = (await CreateTemplateAsync()).GetProperty("id").GetGuid();
        var (_, etag0) = await GetDraftAsync(id);
        using var save = await PutDraftAsync(id, etag0, DraftBody("x"));

        using var response = await PublishAsync(id, etag0);

        await AssertProblemAsync(response, HttpStatusCode.PreconditionFailed, "precondition.failed");
        using var template = await _client.GetAsync(new Uri($"/api/v1/templates/{id}", UriKind.Relative));
        Assert.Equal(JsonValueKind.Null, (await ReadAsync(template)).GetProperty("latestPublishedVersion").ValueKind);
    }

    [Fact]
    public async Task StartDraft_IsRejected_WhenADraftAlreadyExists()
    {
        var id = (await CreateTemplateAsync()).GetProperty("id").GetGuid();

        using var response = await _client.PostAsync(new Uri($"/api/v1/templates/{id}/draft", UriKind.Relative), null);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "template.draft_exists");
    }

    [Fact]
    public async Task GetDraft_After_Publish_Is409_NoDraft()
    {
        var id = (await CreateTemplateAsync()).GetProperty("id").GetGuid();
        var (_, etag) = await GetDraftAsync(id);
        using var publish = await PublishAsync(id, etag);

        using var response = await _client.GetAsync(new Uri($"/api/v1/templates/{id}/draft", UriKind.Relative));

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "template.no_draft");
    }

    [Fact]
    public async Task UnknownTemplate_Is404()
    {
        using var response = await _client.GetAsync(new Uri($"/api/v1/templates/{Guid.NewGuid()}/versions", UriKind.Relative));

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "template.not_found");
    }

    [Fact]
    public async Task List_PagesTemplatesByKey_AndShowsPublishState()
    {
        var created = await CreateTemplateAsync();
        var id = created.GetProperty("id").GetGuid();
        var (_, etag) = await GetDraftAsync(id);
        using var publish = await PublishAsync(id, etag);

        using var response = await _client.GetAsync(new Uri("/api/v1/templates?page=1&pageSize=100", UriKind.Relative));
        var page = await ReadAsync(response);

        Assert.Equal(100, page.GetProperty("pageSize").GetInt32());
        var item = page.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == id);
        Assert.Equal(1, item.GetProperty("latestPublishedVersion").GetInt32());
        Assert.False(item.GetProperty("hasDraft").GetBoolean());
        var keys = page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString()!).ToList();
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
    }

    // ----- Helpers -----

    private static string NewKey() => "T" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    private async Task<JsonElement> CreateTemplateAsync(string? key = null)
    {
        using var response = await _client.PostAsJsonAsync(new Uri("/api/v1/templates", UriKind.Relative), new { key = key ?? NewKey(), name = "Test invoice" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync(response);
    }

    private async Task<(JsonElement Draft, string ETag)> GetDraftAsync(Guid id)
    {
        using var response = await _client.GetAsync(new Uri($"/api/v1/templates/{id}/draft", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync(response), ETagOf(response));
    }

    private Task<HttpResponseMessage> PutDraftAsync(Guid id, string? etag, JsonObject body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"/api/v1/templates/{id}/draft", UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> PublishAsync(Guid id, string etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/templates/{id}/draft/publish", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return _client.SendAsync(request);
    }

    private static JsonObject DraftBody(string marker) => new()
    {
        ["pageSettings"] = new JsonObject
        {
            ["size"] = "A4",
            ["orientation"] = "portrait",
            ["marginsMm"] = new JsonObject { ["top"] = 18, ["right"] = 16, ["bottom"] = 18, ["left"] = 16 },
        },
        ["layout"] = new JsonObject { ["layoutSchemaVersion"] = 1, ["marker"] = marker },
        ["dataSchema"] = new JsonObject { ["type"] = "object" },
        ["sampleData"] = new JsonObject { ["invoice"] = new JsonObject { ["number"] = "INV-2026-000001" } },
    };

    private static string ETagOf(HttpResponseMessage response) =>
        response.Headers.ETag?.ToString() ?? throw new InvalidOperationException($"No ETag on {(int)response.StatusCode} response.");

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Json);

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await ReadAsync(response);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        return problem;
    }
}
