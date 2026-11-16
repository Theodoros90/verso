using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Verso.Api.Tests;

[Collection(ApiTestGroup.Name)]
public sealed class DocumentCategoryApiTests(MigratedDatabaseFixture fixture)
{
    private const string Route = "/api/v1/document-categories";
    private readonly HttpClient _client = fixture.Factory.CreateClient();

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(40, prefix.Length + 9)];

    [Fact]
    public async Task Create_ThenList_ReturnsItSortedByName()
    {
        var prefix = Unique("Sort");
        await CreateAsync($"{prefix} b");
        var a = await CreateAsync($"{prefix} a");
        Assert.Equal(0, a.GetProperty("templateCount").GetInt32());
        Assert.True(a.GetProperty("isActive").GetBoolean());

        var names = (await ListAsync()).Select(c => c.GetProperty("name").GetString()!).Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        Assert.Equal([$"{prefix} a", $"{prefix} b"], names);
    }

    [Fact]
    public async Task Create_DuplicateNameDifferentCase_Returns409NameTaken()
    {
        var name = Unique("Dup");
        await CreateAsync(name);
        using var response = await _client.PostAsJsonAsync(new Uri(Route, UriKind.Relative), new { name = $"  {name.ToUpperInvariant()} " });
        await AssertProblemAsync(response, HttpStatusCode.Conflict, "category.name_taken");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Create_Blank_Returns400Name(string? name)
    {
        using var response = await _client.PostAsJsonAsync(new Uri(Route, UriKind.Relative), new { name });
        await AssertNameErrorAsync(response);
    }

    [Fact]
    public async Task Create_Over100Chars_Returns400Name()
    {
        using var response = await _client.PostAsJsonAsync(new Uri(Route, UriKind.Relative), new { name = new string('x', 101) });
        await AssertNameErrorAsync(response);
    }

    [Fact]
    public async Task Rename_ToExistingName_Returns409()
    {
        var first = Unique("RenA");
        var second = Unique("RenB");
        await CreateAsync(first);
        var other = await CreateAsync(second);
        using var response = await _client.PutAsJsonAsync(new Uri($"{Route}/{other.GetProperty("id").GetGuid()}", UriKind.Relative), new { name = first.ToLowerInvariant() });
        await AssertProblemAsync(response, HttpStatusCode.Conflict, "category.name_taken");
    }

    [Fact]
    public async Task Rename_ToOwnNameWithOtherCase_Succeeds()
    {
        var name = Unique("Own");
        var created = await CreateAsync(name);
        using var response = await _client.PutAsJsonAsync(new Uri($"{Route}/{created.GetProperty("id").GetGuid()}", UriKind.Relative), new { name = name.ToUpperInvariant() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(name.ToUpperInvariant(), (await ReadAsync(response)).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Rename_UnknownId_Returns404()
    {
        using var response = await _client.PutAsJsonAsync(new Uri($"{Route}/{Guid.NewGuid()}", UriKind.Relative), new { name = "Whatever" });
        await AssertProblemAsync(response, HttpStatusCode.NotFound, "category.not_found");
    }

    [Fact]
    public async Task Deactivate_HidesFromDefaultList_ShownWithIncludeInactive_AndActivateRestores()
    {
        var created = await CreateAsync(Unique("Off"));
        var id = created.GetProperty("id").GetGuid();

        using var off = await _client.PostAsync(new Uri($"{Route}/{id}/deactivate", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False((await ReadAsync(off)).GetProperty("isActive").GetBoolean());

        Assert.DoesNotContain(await ListAsync(), c => c.GetProperty("id").GetGuid() == id);
        var all = await ListAsync(includeInactive: true);
        Assert.False(all.Single(c => c.GetProperty("id").GetGuid() == id).GetProperty("isActive").GetBoolean());

        using var on = await _client.PostAsync(new Uri($"{Route}/{id}/activate", UriKind.Relative), null);
        Assert.True((await ReadAsync(on)).GetProperty("isActive").GetBoolean());
        Assert.Contains(await ListAsync(), c => c.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task Deactivate_Twice_WritesOneAuditEntry()
    {
        var created = await CreateAsync(Unique("Aud"));
        var id = created.GetProperty("id").GetGuid();
        using var first = await _client.PostAsync(new Uri($"{Route}/{id}/deactivate", UriKind.Relative), null);
        using var second = await _client.PostAsync(new Uri($"{Route}/{id}/deactivate", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using var audit = await _client.GetAsync(new Uri($"/api/v1/audit?entityType=DocumentCategory&entityId={id}", UriKind.Relative));
        var actions = (await ReadAsync(audit)).GetProperty("items").EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Equal(["CategoryCreated", "CategoryDeactivated"], actions);
    }

    [Fact]
    public async Task Writes_AsReader_Return403_ListAllowed()
    {
        var created = await CreateAsync(Unique("Rdr"));
        var id = created.GetProperty("id").GetGuid();
        using var reader = fixture.Factory.CreateClientAs("dev.viewer");

        using var list = await reader.GetAsync(new Uri(Route, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var post = await reader.PostAsJsonAsync(new Uri(Route, UriKind.Relative), new { name = "Nope" });
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        using var put = await reader.PutAsJsonAsync(new Uri($"{Route}/{id}", UriKind.Relative), new { name = "Nope" });
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        using var off = await reader.PostAsync(new Uri($"{Route}/{id}/deactivate", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Forbidden, off.StatusCode);
        using var on = await reader.PostAsync(new Uri($"{Route}/{id}/activate", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Forbidden, on.StatusCode);
    }

    private async Task<JsonElement> CreateAsync(string name)
    {
        using var response = await _client.PostAsJsonAsync(new Uri(Route, UriKind.Relative), new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync(response);
    }

    private async Task<List<JsonElement>> ListAsync(bool includeInactive = false)
    {
        using var response = await _client.GetAsync(new Uri($"{Route}?includeInactive={includeInactive.ToString().ToLowerInvariant()}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await ReadAsync(response)).EnumerateArray()];
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private static async Task AssertNameErrorAsync(HttpResponseMessage response)
    {
        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
        Assert.True(problem.GetProperty("errors").TryGetProperty("name", out _));
    }

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await ReadAsync(response);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        return problem;
    }
}
