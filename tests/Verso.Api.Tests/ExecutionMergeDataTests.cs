using System.Text.Json;
using System.Text.Json.Nodes;

namespace Verso.Api.Tests;

/// <summary>The Merge data step in a real run: a lookup table typed into the step enriches the submitted data before it is validated and printed.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ExecutionMergeDataTests(MigratedDatabaseFixture fixture) : IDisposable
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

    /// <summary>The synthetic invoice with the customer's name left for the lookup to supply (only an id is submitted).</summary>
    private static JsonObject Record(string customerId = "C-1001")
    {
        var record = (JsonObject)ExecutionTestKit.Sample.DeepClone();
        record["customer"]!.AsObject().Remove("name");
        record["customer"]!["id"] = customerId;
        return record;
    }

    private static JsonObject Config(string strategy = "stop") => new JsonObject
    {
        ["key"] = "customer.id", ["lookupKey"] = "id", ["source"] = "", ["into"] = "customer", ["overwrite"] = false, ["strategy"] = strategy,
        ["lookup"] = """[ { "id": "C-1001", "name": "Kestrel Logistics Ltd" }, { "id": "C-1002", "name": "Osprey Freight Ltd" } ]""",
    };

    private static readonly JsonNode DocumentRules = JsonNode.Parse("""{ "required": ["invoice.number", "customer.name"], "types": [] }""")!;

    private static JsonElement Step(JsonElement execution, string type) =>
        execution.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("type").GetString() == type);

    [Fact]
    public async Task LookedUpFields_ReachValidateAndTheDocument()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(DocumentRules, mergeConfig: Config());
        var id = await Kit.SubmitNewAsync(key, Record());

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", e.GetProperty("status").GetString());
        var merge = Step(e, "merge-data");
        Assert.Equal("success", merge.GetProperty("outputPort").GetString());
        Assert.Equal(1, merge.GetProperty("result").GetProperty("matched").GetInt32());
        Assert.Equal("valid", Step(e, "validate").GetProperty("outputPort").GetString());
    }

    [Fact]
    public async Task NoMatchWithStop_EndsRejected_WithoutEchoingTheKey()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(DocumentRules, mergeConfig: Config());
        var id = await Kit.SubmitNewAsync(key, Record("C-SECRET-9"));

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Rejected", e.GetProperty("status").GetString());
        Assert.Equal("data.unmergeable", e.GetProperty("failureCode").GetString());
        var step = Step(e, "merge-data");
        Assert.Equal("failed", step.GetProperty("outputPort").GetString());
        Assert.Equal("merge.unmatched", step.GetProperty("result").GetProperty("issues")[0].GetProperty("code").GetString());
        Assert.DoesNotContain("C-SECRET-9", e.GetRawText(), StringComparison.Ordinal);
        Assert.Empty(e.GetProperty("documents").EnumerateArray());
    }

    [Fact]
    public async Task ResumedRun_RebuildsTheMergedData()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(DocumentRules, mergeConfig: Config());
        var id = await Kit.SubmitNewAsync(key, Record());
        _store.FailWrites = true;

        await Kit.RunEngineAsync(id, _printer, _store, _jobs); // the PDF step fails once and a retry is scheduled
        _store.FailWrites = false;
        await Kit.RunEngineAsync(id, _printer, _store, _jobs); // resumes: the merge is restored, not run again

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(1, Step(done, "merge-data").GetProperty("attempt").GetInt32());
    }
}
