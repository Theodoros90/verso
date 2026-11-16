using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Verso.Api.Tests;

/// <summary>
/// The account holder sample (tests/fixtures/account-holders, docs/samples/account-holders.md): one XML file of account holders goes through
/// Parse and For each; only holders whose AccountID starts with "C" get a PDF, which is archived. Real engine, fake printer.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class AccountHoldersSampleTests(MigratedDatabaseFixture fixture) : IDisposable
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

    private static string Fixture(string name) => ExecutionTestKit.Fixture("account-holders/" + name);

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

    private async Task<Guid> PublishTemplateAsync()
    {
        var key = "A" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var created = await SendJsonAsync(HttpMethod.Post, "/api/v1/templates", null, new JsonObject { ["key"] = key, ["name"] = "Account holder summary" }, HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        var draft = await SendJsonAsync(HttpMethod.Get, $"/api/v1/templates/{id}/draft", null, null, HttpStatusCode.OK);
        var body = new JsonObject
        {
            ["pageSettings"] = new JsonObject { ["size"] = "A4", ["orientation"] = "portrait", ["marginsMm"] = new JsonObject { ["top"] = 18, ["right"] = 16, ["bottom"] = 18, ["left"] = 16 } },
            ["layout"] = JsonNode.Parse(Fixture("layout.json")),
            ["dataSchema"] = new JsonObject { ["type"] = "object" },
            ["sampleData"] = JsonNode.Parse(Fixture("sample-data.json")),
        };
        var saved = await SendJsonAsync(HttpMethod.Put, $"/api/v1/templates/{id}/draft", draft.GetProperty("_etag").GetString(), body, HttpStatusCode.OK);
        await SendJsonAsync(HttpMethod.Post, $"/api/v1/templates/{id}/draft/publish", saved.GetProperty("_etag").GetString(), null, HttpStatusCode.OK);
        return id;
    }

    /// <summary>Publishes the template and the sample workflow (as tools/samples/load-account-holders.ps1 does). Returns the workflow key.</summary>
    private async Task<string> PublishSampleAsync()
    {
        var templateId = await PublishTemplateAsync();
        var definition = JsonNode.Parse(Fixture("workflow.json").Replace("__TEMPLATE_ID__", templateId.ToString(), StringComparison.Ordinal))!.AsObject();
        definition.Remove("_comment");

        var key = "acc-" + Guid.NewGuid().ToString("N")[..12];
        var created = await SendJsonAsync(HttpMethod.Post, "/api/v1/workflows", null, new JsonObject { ["key"] = key, ["name"] = "Account holder PDFs" }, HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        var draft = await SendJsonAsync(HttpMethod.Get, $"/api/v1/workflows/{id}/draft", null, null, HttpStatusCode.OK);
        var saved = await SendJsonAsync(HttpMethod.Put, $"/api/v1/workflows/{id}/draft", draft.GetProperty("_etag").GetString(), new JsonObject { ["definition"] = definition }, HttpStatusCode.OK);
        await SendJsonAsync(HttpMethod.Post, $"/api/v1/workflows/{id}/draft/publish", saved.GetProperty("_etag").GetString(), null, HttpStatusCode.OK);
        return key;
    }

    [Fact]
    public async Task OnlyAccountsStartingWithC_GetAnArchivedPdf()
    {
        var key = await PublishSampleAsync();
        var id = await Kit.SubmitNewAsync(key, new JsonObject { ["content"] = Fixture("Account.xml") });

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.True(e.GetProperty("status").GetString() == "Succeeded", e.GetRawText());
        var documents = e.GetProperty("documents").EnumerateArray().ToList();
        Assert.Equal(["C001.pdf", "C002.pdf", "C003.pdf", "C004.pdf"], documents.Select(d => d.GetProperty("fileName").GetString()!).Order(StringComparer.Ordinal));
        Assert.Equal(4, _printer.Calls);
        Assert.All(documents, d => Assert.Equal(JsonValueKind.Object, d.GetProperty("archive").ValueKind));
    }
}
