using System.Text.Json;
using System.Text.Json.Nodes;

namespace Verso.Api.Tests;

/// <summary>The For each step in a real run: the steps after it run once per item, each item with its own step rows and its own document.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ExecutionForEachTests(MigratedDatabaseFixture fixture) : IDisposable
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

    private static JsonObject Config(string mode = "each", string field = "lines") => new() { ["field"] = field, ["mode"] = mode, ["item"] = "item" };

    private static List<JsonElement> Steps(JsonElement execution, string type) =>
        [.. execution.GetProperty("steps").EnumerateArray().Where(s => s.GetProperty("type").GetString() == type)];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EachItem_RunsTheStepsAfterIt_WithItsOwnRowsAndDocument(bool backEdge)
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(forEachConfig: Config(), forEachBackEdge: backEdge);
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample); // three lines

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", e.GetProperty("status").GetString());
        var loop = Assert.Single(Steps(e, "for-each"));
        Assert.Equal("each", loop.GetProperty("outputPort").GetString());
        Assert.Equal(3, loop.GetProperty("result").GetProperty("count").GetInt32());
        Assert.Equal(0, loop.GetProperty("iteration").GetInt32());
        var pdfs = Steps(e, "generate-pdf");
        Assert.Equal([1, 2, 3], pdfs.Select(s => s.GetProperty("iteration").GetInt32()));
        Assert.All(pdfs, s => Assert.Equal("Succeeded", s.GetProperty("status").GetString()));
        var documents = e.GetProperty("documents").EnumerateArray().ToList();
        Assert.Equal([1, 2, 3], documents.Select(d => d.GetProperty("iteration").GetInt32()).Order());
        Assert.Equal(3, _printer.Calls);
        Assert.Equal(backEdge ? 0 : 3, Steps(e, "archive").Count(s => s.GetProperty("status").GetString() == "Succeeded"));
        Assert.Equal(backEdge ? 0 : 3, documents.Count(d => d.GetProperty("archive").ValueKind == JsonValueKind.Object));
    }

    [Fact]
    public async Task EmptyList_TakesTheEmptyPath_AndTheLoopStepsAreSkipped()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(forEachConfig: Config(field: "nothing"));
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", e.GetProperty("status").GetString());
        Assert.Equal("empty", Assert.Single(Steps(e, "for-each")).GetProperty("outputPort").GetString());
        Assert.Equal("Skipped", Assert.Single(Steps(e, "generate-pdf")).GetProperty("status").GetString());
        Assert.Empty(e.GetProperty("documents").EnumerateArray());
        Assert.Equal(0, _printer.Calls);
    }

    [Fact]
    public async Task SplitMode_EachItemIsItsOwnRecord_AndAnIncompleteOneRejectsTheRun()
    {
        // a line has no invoice.number, so the document can't be made: a data problem in item 1, and item 2 never runs
        var key = await Kit.PublishInvoiceWorkflowAsync(forEachConfig: Config("split"));
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Rejected", e.GetProperty("status").GetString());
        Assert.Equal("document.data_incomplete", e.GetProperty("failureCode").GetString());
        var pdf = Assert.Single(Steps(e, "generate-pdf"));
        Assert.Equal(1, pdf.GetProperty("iteration").GetInt32());
        Assert.Equal("failed", pdf.GetProperty("outputPort").GetString());
        Assert.Equal(0, _printer.Calls);
    }

    [Fact]
    public async Task ARetriedRun_ResumesInTheItemThatFailed_AndKeepsFinishedItems()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(forEachConfig: Config(), forEachBackEdge: true);
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);
        var writes = 0;
        _store.AfterWrite = _ =>
        {
            if (++writes == 1)
            {
                _store.FailWrites = true; // after the first item's document, storing fails
            }
        };

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);
        _store.FailWrites = false;
        _store.AfterWrite = null;
        var halfway = await Kit.GetExecutionAsync(id);
        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        var pdfs = Steps(done, "generate-pdf");
        Assert.Equal([1, 2, 3], pdfs.Select(s => s.GetProperty("iteration").GetInt32()));
        Assert.Equal(1, pdfs[0].GetProperty("attempt").GetInt32()); // item 1 was not run again
        Assert.Equal(3, done.GetProperty("documents").GetArrayLength());
        Assert.NotEqual("Succeeded", halfway.GetProperty("status").GetString());
    }

    [Fact]
    public async Task TooManyItems_FailTheStepWithoutRunningAny()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(forEachConfig: Config());
        var data = ExecutionTestKit.Sample;
        data["lines"] = new JsonArray([.. Enumerable.Range(0, 201).Select(i => (JsonNode)new JsonObject { ["n"] = i })]);
        var id = await Kit.SubmitNewAsync(key, data);

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Failed", e.GetProperty("status").GetString());
        Assert.Equal("foreach.too_many_items", e.GetProperty("failureCode").GetString());
        Assert.Equal(0, _printer.Calls);
    }
}
