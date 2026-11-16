using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Verso.Api.Tests;

[Collection(ApiTestGroup.Name)]
public sealed class TemplateCategoryApiTests(MigratedDatabaseFixture fixture)
{
    private const string CategoryRoute = "/api/v1/document-categories";
    private const string TemplateRoute = "/api/v1/templates";
    private readonly HttpClient _client = fixture.Factory.CreateClient();

    [Fact]
    public async Task Create_WithCategory_ReturnsCategoryInDtoAndList()
    {
        var category = await CreateCategoryAsync();
        var template = await CreateTemplateAsync(category.Id);
        Assert.Equal(category.Id, template.GetProperty("categoryId").GetGuid());
        Assert.Equal(category.Name, template.GetProperty("categoryName").GetString());

        var listed = (await ListAsync($"categoryId={category.Id}")).Single();
        Assert.Equal(template.GetProperty("id").GetGuid(), listed.GetProperty("id").GetGuid());
        Assert.Equal(category.Id, listed.GetProperty("categoryId").GetGuid());
        Assert.Equal(category.Name, listed.GetProperty("categoryName").GetString());
    }

    [Fact]
    public async Task Create_WithoutCategory_StaysNull()
    {
        var template = await CreateTemplateAsync(null);
        Assert.Equal(JsonValueKind.Null, template.GetProperty("categoryId").ValueKind);
        Assert.Equal(JsonValueKind.Null, template.GetProperty("categoryName").ValueKind);
    }

    [Fact]
    public async Task Create_UnknownCategory_Returns400CategoryId()
    {
        using var response = await PostTemplateAsync(Guid.NewGuid());
        await AssertCategoryErrorAsync(response, "Unknown category.");
    }

    [Fact]
    public async Task Create_DeactivatedCategory_Returns400CategoryId()
    {
        var category = await CreateCategoryAsync();
        await DeactivateAsync(category.Id);
        using var response = await PostTemplateAsync(category.Id);
        await AssertCategoryErrorAsync(response, "This category is deactivated.");
    }

    [Fact]
    public async Task SetCategory_ChangesClearsAndIsIdempotent()
    {
        var a = await CreateCategoryAsync();
        var b = await CreateCategoryAsync();
        var id = (await CreateTemplateAsync(null)).GetProperty("id").GetGuid();

        using var toA = await PutCategoryAsync(_client, id, a.Id);
        Assert.Equal(HttpStatusCode.OK, toA.StatusCode);
        Assert.Equal(a.Name, (await ReadAsync(toA)).GetProperty("categoryName").GetString());
        using var toAAgain = await PutCategoryAsync(_client, id, a.Id);
        Assert.Equal(HttpStatusCode.OK, toAAgain.StatusCode);
        Assert.Equal(["TemplateCreated", "TemplateCategoryChanged"], await AuditActionsAsync(id));

        using var toB = await PutCategoryAsync(_client, id, b.Id);
        Assert.Equal(b.Id, (await ReadAsync(toB)).GetProperty("categoryId").GetGuid());
        using var cleared = await PutCategoryAsync(_client, id, null);
        var dto = await ReadAsync(cleared);
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("categoryId").ValueKind);
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("categoryName").ValueKind);

        Assert.Equal(["TemplateCreated", "TemplateCategoryChanged", "TemplateCategoryChanged", "TemplateCategoryChanged"], await AuditActionsAsync(id));

        using var unknown = await PutCategoryAsync(_client, id, Guid.NewGuid());
        await AssertCategoryErrorAsync(unknown, "Unknown category.");
        using var missing = await PutCategoryAsync(_client, Guid.NewGuid(), a.Id);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task SetCategory_DeactivatedCategoryOnTemplateThatAlreadyHasIt_ListStillShowsName()
    {
        var category = await CreateCategoryAsync();
        var other = await CreateCategoryAsync();
        var id = (await CreateTemplateAsync(category.Id)).GetProperty("id").GetGuid();
        await DeactivateAsync(category.Id);

        using var same = await PutCategoryAsync(_client, id, category.Id);
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Equal(["TemplateCreated"], await AuditActionsAsync(id));

        var listed = (await ListAsync($"categoryId={category.Id}")).Single();
        Assert.Equal(category.Name, listed.GetProperty("categoryName").GetString());

        // Moving away works; moving back to the deactivated category does not.
        using var away = await PutCategoryAsync(_client, id, other.Id);
        Assert.Equal(HttpStatusCode.OK, away.StatusCode);
        using var back = await PutCategoryAsync(_client, id, category.Id);
        await AssertCategoryErrorAsync(back, "This category is deactivated.");
    }

    [Fact]
    public async Task List_FilterByCategory_AndNone()
    {
        var category = await CreateCategoryAsync();
        var inCategory = (await CreateTemplateAsync(category.Id)).GetProperty("id").GetGuid();
        var without = (await CreateTemplateAsync(null)).GetProperty("id").GetGuid();

        var byCategory = (await ListAsync($"categoryId={category.Id}")).Select(t => t.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([inCategory], byCategory);

        var none = (await ListAsync("categoryId=none&pageSize=100")).Select(t => t.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(without, none);
        Assert.DoesNotContain(inCategory, none);
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("")]
    public async Task List_FilterMalformed_Returns400(string value)
    {
        using var response = await _client.GetAsync(new Uri($"{TemplateRoute}?categoryId={value}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadAsync(response);
        Assert.True(problem.GetProperty("errors").TryGetProperty("categoryId", out _));
    }

    [Fact]
    public async Task CategoryTemplateCount_CountsAssignedTemplates()
    {
        var category = await CreateCategoryAsync();
        await CreateTemplateAsync(category.Id);
        var second = (await CreateTemplateAsync(null)).GetProperty("id").GetGuid();
        using var assigned = await PutCategoryAsync(_client, second, category.Id);
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);

        using var list = await _client.GetAsync(new Uri($"{CategoryRoute}?includeInactive=true", UriKind.Relative));
        var row = (await ReadAsync(list)).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == category.Id);
        Assert.Equal(2, row.GetProperty("templateCount").GetInt32());
    }

    [Fact]
    public async Task SetCategory_AsReader_Returns403()
    {
        var id = (await CreateTemplateAsync(null)).GetProperty("id").GetGuid();
        using var reader = fixture.Factory.CreateClientAs("dev.viewer");
        using var response = await PutCategoryAsync(reader, id, null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private sealed record CategoryRef(Guid Id, string Name);

    private async Task<CategoryRef> CreateCategoryAsync()
    {
        var name = $"TC-{Guid.NewGuid():N}"[..20];
        using var response = await _client.PostAsJsonAsync(new Uri(CategoryRoute, UriKind.Relative), new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return new CategoryRef((await ReadAsync(response)).GetProperty("id").GetGuid(), name);
    }

    private async Task DeactivateAsync(Guid id)
    {
        using var response = await _client.PostAsync(new Uri($"{CategoryRoute}/{id}/deactivate", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private Task<HttpResponseMessage> PostTemplateAsync(Guid? categoryId) =>
        _client.PostAsJsonAsync(new Uri(TemplateRoute, UriKind.Relative),
            new { key = $"CAT-{Guid.NewGuid():N}"[..16].ToUpperInvariant(), name = "Categorised", categoryId });

    private async Task<JsonElement> CreateTemplateAsync(Guid? categoryId)
    {
        using var response = await PostTemplateAsync(categoryId);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync(response);
    }

    private static Task<HttpResponseMessage> PutCategoryAsync(HttpClient client, Guid id, Guid? categoryId) =>
        client.PutAsJsonAsync(new Uri($"{TemplateRoute}/{id}/category", UriKind.Relative), new { categoryId });

    private async Task<List<JsonElement>> ListAsync(string query)
    {
        using var response = await _client.GetAsync(new Uri($"{TemplateRoute}?{query}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await ReadAsync(response)).GetProperty("items").EnumerateArray()];
    }

    private async Task<List<string?>> AuditActionsAsync(Guid id)
    {
        using var audit = await _client.GetAsync(new Uri($"/api/v1/audit?entityType=Template&entityId={id}", UriKind.Relative));
        return [.. (await ReadAsync(audit)).GetProperty("items").EnumerateArray().Select(e => e.GetProperty("action").GetString())];
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private static async Task AssertCategoryErrorAsync(HttpResponseMessage response, string message)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadAsync(response);
        Assert.Equal("validation", problem.GetProperty("code").GetString());
        Assert.Equal(message, problem.GetProperty("errors").GetProperty("categoryId")[0].GetString());
    }
}
