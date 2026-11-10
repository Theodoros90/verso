using System.Net;
using System.Text.Json;

namespace Verso.Api.Tests;

/// <summary>The audit trail's filters (M9 audit screen): action, actor, entity, date range, newest first.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class AuditApiTests(MigratedDatabaseFixture fixture)
{
    [Fact]
    public async Task Filters_ByActionActorEntityAndDate_NewestFirstOnRequest()
    {
        // Two actors: a template author creates a template; the admin (default client) runs a workflow.
        using var author = fixture.Factory.CreateClientAs("dev.template-author");
        var templateKey = "AU" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        using (var created = await author.PostAsync(new Uri("/api/v1/templates", UriKind.Relative),
                   System.Net.Http.Json.JsonContent.Create(new { key = templateKey, name = "Audit filter test" })))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using var client = fixture.Factory.CreateClient();
        var kit = new ExecutionTestKit(fixture.Factory, client);
        var key = await kit.PublishInvoiceWorkflowAsync();
        var id = await kit.SubmitNewAsync(key, ExecutionTestKit.Sample);

        var byAuthor = await ListAsync(client, "actorId=dev.template-author&action=TemplateCreated&newestFirst=true");
        Assert.All(byAuthor, e => Assert.Equal("dev.template-author", e.GetProperty("actorId").GetString()));
        Assert.Equal("TemplateCreated", byAuthor[0].GetProperty("action").GetString());
        Assert.Equal(templateKey, byAuthor[0].GetProperty("details").GetProperty("key").GetString()); // newest first: ours

        var forRun = await ListAsync(client, $"entityType=Execution&entityId={id}");
        Assert.Equal(["ExecutionSubmitted"], forRun.Select(e => e.GetProperty("action").GetString()));

        var sequences = (await ListAsync(client, "newestFirst=true&pageSize=20")).Select(e => e.GetProperty("sequence").GetInt64()).ToList();
        Assert.Equal(sequences.OrderDescending(), sequences);

        var future = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("o"));
        Assert.Empty(await ListAsync(client, $"from={future}"));
        using var bad = await client.GetAsync(new Uri($"/api/v1/audit?from={future}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("o"))}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        using var actions = await client.GetAsync(new Uri("/api/v1/audit/actions", UriKind.Relative));
        var names = JsonSerializer.Deserialize<string[]>(await actions.Content.ReadAsStringAsync())!;
        Assert.Contains("TemplateCreated", names);
        Assert.Contains("ExecutionSubmitted", names);
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
    }

    private static async Task<List<JsonElement>> ListAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/audit?{query}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await ExecutionTestKit.ReadAsync(response)).GetProperty("items").EnumerateArray()];
    }
}
