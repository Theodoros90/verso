using System.Text.Json;
using System.Text.Json.Nodes;

namespace Verso.Api.Tests;

/// <summary>The Switch step in a real run: the run leaves by the first matching branch, and only the "mid" path (connected) goes on to a document.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ExecutionSwitchTests(MigratedDatabaseFixture fixture) : IDisposable
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

    /// <summary>Gross above 10000 is "high", above the given mid limit "mid", anything else "other". Only "mid" is connected.</summary>
    private static JsonObject Config(string midLimit) => new()
    {
        ["field"] = "totals.gross",
        ["op"] = "greater-than",
        ["other"] = true,
        ["branches"] = new JsonArray(
            new JsonObject { ["id"] = "high", ["label"] = "High", ["compare"] = "value", ["value"] = "10000" },
            new JsonObject { ["id"] = "mid", ["label"] = "Mid", ["compare"] = "value", ["value"] = midLimit }),
    };

    private static JsonElement Step(JsonElement execution, string type) =>
        execution.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("type").GetString() == type);

    [Fact]
    public async Task MatchingBranch_ContinuesToTheDocument()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(switchConfig: Config("1000")); // the sample's gross is 4797.28
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", e.GetProperty("status").GetString());
        var step = Step(e, "switch");
        Assert.Equal("mid", step.GetProperty("outputPort").GetString());
        Assert.Equal("mid", step.GetProperty("result").GetProperty("branch").GetString());
        Assert.Single(e.GetProperty("documents").EnumerateArray());
    }

    [Fact]
    public async Task NoMatchingBranch_TakesOther_WhichEndsTheRunHere()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(switchConfig: Config("9000"));
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", e.GetProperty("status").GetString());
        Assert.Equal("other", Step(e, "switch").GetProperty("outputPort").GetString());
        Assert.Empty(e.GetProperty("documents").EnumerateArray());
        Assert.Equal("Skipped", Step(e, "validate").GetProperty("status").GetString());
    }

    [Fact]
    public async Task WithoutOther_NoMatchEndsTheRunToo_AndNeverEchoesTheValue()
    {
        var config = Config("9000");
        config["other"] = false;
        var key = await Kit.PublishInvoiceWorkflowAsync(switchConfig: config);
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", e.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, Step(e, "switch").GetProperty("outputPort").ValueKind);
        Assert.DoesNotContain("4797.28", Step(e, "switch").GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResumedRun_KeepsThePathAlreadyTaken()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(switchConfig: Config("1000"));
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);
        _store.FailWrites = true;

        await Kit.RunEngineAsync(id, _printer, _store, _jobs); // the PDF step fails once and a retry is scheduled
        _store.FailWrites = false;
        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(1, Step(done, "switch").GetProperty("attempt").GetInt32());
    }
}
