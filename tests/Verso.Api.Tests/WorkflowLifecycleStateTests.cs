using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Verso.Api.Tests;

/// <summary>Deactivating and activating a workflow (docs/api.md#workflows): runs stop, the workflow moves to the deactivated list, nothing is deleted, every change is audited.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class WorkflowLifecycleStateTests(MigratedDatabaseFixture fixture) : IDisposable
{
    private readonly HttpClient _client = fixture.Factory.CreateClient();
    private ExecutionTestKit Kit => new(fixture.Factory, _client);

    public void Dispose() => _client.Dispose();

    private async Task<(string Key, Guid Id)> PublishedWorkflowAsync()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync();
        var id = await Kit.WithDbAsync(db => db.Workflows.Where(w => w.Key == key).Select(w => w.Id).SingleAsync());
        return (key, id);
    }

    private async Task<JsonElement> PostAsync(Guid id, string action, HttpStatusCode expected)
    {
        using var response = await _client.PostAsync(new Uri($"/api/v1/workflows/{id}/{action}", UriKind.Relative), null);
        Assert.True(response.StatusCode == expected, await response.Content.ReadAsStringAsync());
        return await ExecutionTestKit.ReadAsync(response);
    }

    private async Task<List<string>> ListedKeysAsync(bool inactive)
    {
        using var response = await _client.GetAsync(new Uri($"/api/v1/workflows?pageSize=100&inactive={inactive.ToString().ToLowerInvariant()}", UriKind.Relative));
        var page = await ExecutionTestKit.ReadAsync(response);
        return [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString()!)];
    }

    [Fact]
    public async Task NewWorkflow_IsActive_AndListed()
    {
        var (key, id) = await PublishedWorkflowAsync();

        using var response = await _client.GetAsync(new Uri($"/api/v1/workflows/{id}", UriKind.Relative));
        Assert.True((await ExecutionTestKit.ReadAsync(response)).GetProperty("isActive").GetBoolean());
        Assert.Contains(key, await ListedKeysAsync(inactive: false));
        Assert.DoesNotContain(key, await ListedKeysAsync(inactive: true));
    }

    [Fact]
    public async Task Deactivated_RejectsNewRuns_AndMovesToTheDeactivatedList_UntilActivatedAgain()
    {
        var (key, id) = await PublishedWorkflowAsync();
        var runId = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample); // a run that exists before

        var deactivated = await PostAsync(id, "deactivate", HttpStatusCode.OK);

        Assert.False(deactivated.GetProperty("isActive").GetBoolean());
        using (var refused = await Kit.SubmitAsync(key, Guid.NewGuid().ToString(), ExecutionTestKit.Sample))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("workflow.inactive", (await ExecutionTestKit.ReadAsync(refused)).GetProperty("code").GetString());
        }

        Assert.DoesNotContain(key, await ListedKeysAsync(inactive: false));
        Assert.Contains(key, await ListedKeysAsync(inactive: true));
        Assert.Equal("Queued", (await Kit.GetExecutionAsync(runId)).GetProperty("status").GetString()); // history untouched
        Assert.Equal(1, await Kit.WithDbAsync(db => db.Workflows.CountAsync(w => w.Key == key))); // nothing deleted; the key stays taken

        var activated = await PostAsync(id, "activate", HttpStatusCode.OK);
        Assert.True(activated.GetProperty("isActive").GetBoolean());
        Assert.Contains(key, await ListedKeysAsync(inactive: false));
        using var accepted = await Kit.SubmitAsync(key, Guid.NewGuid().ToString(), ExecutionTestKit.Sample);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
    }

    [Fact]
    public async Task Deactivated_CanStillBeEdited()
    {
        var (_, id) = await PublishedWorkflowAsync();
        await PostAsync(id, "deactivate", HttpStatusCode.OK);

        using var start = await _client.PostAsync(new Uri($"/api/v1/workflows/{id}/draft", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Created, start.StatusCode);
    }

    [Fact]
    public async Task EveryChange_IsAudited_AndRepeatingOneIsANoOp()
    {
        var (_, id) = await PublishedWorkflowAsync();

        await PostAsync(id, "deactivate", HttpStatusCode.OK);
        await PostAsync(id, "deactivate", HttpStatusCode.OK); // already inactive: no second entry
        await PostAsync(id, "activate", HttpStatusCode.OK);
        await PostAsync(id, "activate", HttpStatusCode.OK);

        var actions = await Kit.WithDbAsync(db => db.AuditEntries.AsNoTracking()
            .Where(a => a.EntityType == "Workflow" && a.EntityId == id.ToString())
            .OrderBy(a => a.OccurredAt).Select(a => a.Action).ToListAsync());
        Assert.Equal(["WorkflowCreated", "WorkflowDraftSaved", "WorkflowPublished", "WorkflowDeactivated", "WorkflowActivated"], actions);
    }

    [Fact]
    public async Task UnknownWorkflow_IsNotFound()
    {
        var problem = await PostAsync(Guid.NewGuid(), "deactivate", HttpStatusCode.NotFound);
        Assert.Equal("workflow.not_found", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task OnlyWorkflowAuthors_MayChangeTheState()
    {
        var (_, id) = await PublishedWorkflowAsync();
        using var viewer = fixture.Factory.CreateClientAs("dev.viewer");

        using var response = await viewer.PostAsJsonAsync(new Uri($"/api/v1/workflows/{id}/deactivate", UriKind.Relative), new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
