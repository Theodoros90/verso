using System.Text.Json;
using System.Text.Json.Nodes;

namespace Verso.Api.Tests;

/// <summary>The If / else step in a real run: the run takes the Yes or the No path, and only the path taken runs.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ExecutionIfElseTests(MigratedDatabaseFixture fixture) : IDisposable
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

    /// <summary>Yes (gross above the limit) continues to validate, generate and archive; No ends the run there.</summary>
    private static JsonObject Config(string limit) => new()
    {
        ["conditions"] = new JsonArray(
            new JsonObject { ["field"] = "totals.gross", ["op"] = "greater-than", ["value"] = limit },
            new JsonObject { ["field"] = "customer.name", ["op"] = "is-not-empty", ["value"] = "" }),
        ["yesLabel"] = "Large",
        ["noLabel"] = "Small",
    };

    private static JsonElement Step(JsonElement execution, string type) =>
        execution.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("type").GetString() == type);

    [Fact]
    public async Task YesPath_ContinuesToTheDocument()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(ifConfig: Config("1000")); // the sample's gross is 4797.28
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", e.GetProperty("status").GetString());
        var step = Step(e, "if-else");
        Assert.Equal("yes", step.GetProperty("outputPort").GetString());
        Assert.True(step.GetProperty("result").GetProperty("matched").GetBoolean());
        Assert.Single(e.GetProperty("documents").EnumerateArray());
    }

    [Fact]
    public async Task NoPath_EndsTheRunWithoutADocument()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(ifConfig: Config("10000"));
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", e.GetProperty("status").GetString());
        Assert.Equal("no", Step(e, "if-else").GetProperty("outputPort").GetString());
        Assert.Empty(e.GetProperty("documents").EnumerateArray());
        Assert.Equal("Skipped", Step(e, "validate").GetProperty("status").GetString()); // the Yes path never ran
        Assert.Equal("Skipped", Step(e, "generate-pdf").GetProperty("status").GetString());
    }

    [Fact]
    public async Task ResumedRun_KeepsThePathAlreadyTaken()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(ifConfig: Config("1000"));
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);
        _store.FailWrites = true;

        await Kit.RunEngineAsync(id, _printer, _store, _jobs); // the PDF step fails once and a retry is scheduled
        _store.FailWrites = false;
        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(1, Step(done, "if-else").GetProperty("attempt").GetInt32());
    }
}
