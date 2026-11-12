using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Verso.Api.Tests;

/// <summary>The Approval step in a real run: the run parks until someone holding the approver role decides, then continues.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ExecutionApprovalTests(MigratedDatabaseFixture fixture) : IDisposable
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

    private static JsonObject Config(string approver = "operator") => new() { ["approver"] = approver, ["escalateAfter"] = "24h" };

    private static JsonElement Step(JsonElement execution, string type) =>
        execution.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("type").GetString() == type);

    private async Task<Guid> WaitingRunAsync(string approver = "operator", bool declinedToPdf = false)
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(approvalConfig: Config(approver), approvalDeclinedToPdf: declinedToPdf);
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);
        await Kit.RunEngineAsync(id, _printer, _store, _jobs);
        return id;
    }

    private static Task<HttpResponseMessage> DecideAsync(HttpClient client, Guid id, string decision, string? comment = null) =>
        client.PostAsJsonAsync(new Uri($"/api/v1/executions/{id}/approvals/approve", UriKind.Relative), new { decision, comment });

    [Fact]
    public async Task TheRun_StopsAtTheApproval_UntilSomeoneDecides()
    {
        var id = await WaitingRunAsync();

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("AwaitingApproval", e.GetProperty("status").GetString());
        var step = Step(e, "approval");
        Assert.Equal("Waiting", step.GetProperty("status").GetString());
        Assert.Equal("operator", step.GetProperty("result").GetProperty("approver").GetString());
        Assert.Equal("Pending", Step(e, "generate-pdf").GetProperty("status").GetString());
        Assert.Equal(0, _printer.Calls);

        // a redelivered job does nothing while the run waits
        await Kit.RunEngineAsync(id, _printer, _store, _jobs);
        Assert.Equal("AwaitingApproval", (await Kit.GetExecutionAsync(id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Approving_QueuesTheRun_WhichThenFinishes()
    {
        var id = await WaitingRunAsync();
        using var approver = fixture.Factory.CreateClientAs("dev.operator");

        using var response = await DecideAsync(approver, id, "approved", "Looks right");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = await Kit.GetExecutionAsync(id);
        Assert.Equal("Queued", queued.GetProperty("status").GetString());
        var decided = Step(queued, "approval");
        Assert.Equal("approved", decided.GetProperty("outputPort").GetString());
        Assert.Equal("Omar Operator", decided.GetProperty("result").GetProperty("decidedByName").GetString());
        Assert.Equal("Looks right", decided.GetProperty("result").GetProperty("comment").GetString());

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(1, _printer.Calls);
        Assert.Equal(1, Step(done, "approval").GetProperty("attempt").GetInt32()); // the approval wasn't asked again
        Assert.Equal(1, done.GetProperty("documents").GetArrayLength());
    }

    [Fact]
    public async Task Declining_WithNoDeclinedPath_RejectsTheRun()
    {
        var id = await WaitingRunAsync();
        using var approver = fixture.Factory.CreateClientAs("dev.operator");

        using var response = await DecideAsync(approver, id, "declined", "Wrong customer");
        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Rejected", e.GetProperty("status").GetString());
        Assert.Equal("approval.declined", e.GetProperty("failureCode").GetString());
        Assert.Equal(0, _printer.Calls);
    }

    [Fact]
    public async Task Declining_WithADeclinedPath_ContinuesDownIt()
    {
        var id = await WaitingRunAsync(declinedToPdf: true);
        using var approver = fixture.Factory.CreateClientAs("dev.operator");

        using var response = await DecideAsync(approver, id, "declined");
        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", e.GetProperty("status").GetString());
        Assert.Equal("rejected", Step(e, "approval").GetProperty("outputPort").GetString());
        Assert.Equal(1, _printer.Calls);
    }

    [Theory]
    [InlineData("dev.viewer")]
    [InlineData("dev.auditor")]
    [InlineData("dev.template-author")]
    public async Task SomeoneWithoutTheApproverRole_IsRefused_AndTheRunKeepsWaiting(string user)
    {
        var id = await WaitingRunAsync(); // for operators
        using var other = fixture.Factory.CreateClientAs(user);

        using var response = await DecideAsync(other, id, "approved");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("AwaitingApproval", (await Kit.GetExecutionAsync(id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task AnAdministrator_CanAlwaysDecide()
    {
        var id = await WaitingRunAsync("auditor");
        using var admin = fixture.Factory.CreateClientAs("dev.admin");

        using var response = await DecideAsync(admin, id, "approved");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task ADecisionIsMadeOnce_AndNeedsAValidDecision()
    {
        var id = await WaitingRunAsync();
        using var approver = fixture.Factory.CreateClientAs("dev.operator");

        using var invalid = await DecideAsync(approver, id, "maybe");
        using var first = await DecideAsync(approver, id, "approved");
        using var second = await DecideAsync(approver, id, "declined");
        using var unknown = await approver.PostAsJsonAsync(new Uri($"/api/v1/executions/{id}/approvals/nope", UriKind.Relative), new { decision = "approved" });

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task TheDecisionIsAudited_WithoutTheComment()
    {
        var id = await WaitingRunAsync();
        using var approver = fixture.Factory.CreateClientAs("dev.operator");
        (await DecideAsync(approver, id, "approved", "secret-looking note")).Dispose();

        var entries = await Kit.WithDbAsync(async db =>
            await db.AuditEntries.AsNoTracking().Where(a => a.EntityId == id.ToString() && a.Action.StartsWith("ExecutionApproval")).ToListAsync());

        Assert.Contains(entries, a => a.Action == "ExecutionApprovalRequested");
        Assert.Contains(entries, a => a.Action == "ExecutionApprovalDecided" && a.Details!.Contains("\"decision\":\"approved\"", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, a => a.Details!.Contains("secret-looking", StringComparison.Ordinal));
    }
}
