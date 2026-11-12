using System.Text.Json;
using System.Text.Json.Nodes;

namespace Verso.Api.Tests;

/// <summary>The Transform data step in a real run: rules calculate and reshape the submitted data before it is validated and printed.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ExecutionTransformDataTests(MigratedDatabaseFixture fixture) : IDisposable
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

    /// <summary>The synthetic invoice with the gross total and the invoice number left for the rules to supply.</summary>
    private static JsonObject Record(string net = "4031.33")
    {
        var record = (JsonObject)ExecutionTestKit.Sample.DeepClone();
        record["invoice"]!.AsObject().Remove("number");
        record["totals"]!.AsObject().Remove("gross");
        record["totals"]!["net"] = JsonNode.Parse(net);
        record["prefix"] = "inv";
        record["seq"] = "003901";
        return record;
    }

    private const string Rules = """
        { "rules": [
            { "target": "totals.gross", "operation": "add", "a": "totals.net", "b": "totals.vat" },
            { "target": "totals.gross", "operation": "round", "a": "totals.gross", "b": "2" },
            { "target": "invoice.number", "operation": "text", "text": "INV-2026-{{seq}}" } ] }
        """;

    private static readonly JsonNode DocumentRules = JsonNode.Parse("""
        { "required": ["invoice.number", "customer.name"], "types": [ { "field": "totals.gross", "type": "number" } ] }
        """)!;

    private static JsonElement Step(JsonElement execution, string type) =>
        execution.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("type").GetString() == type);

    [Fact]
    public async Task CalculatedFields_ReachValidateAndTheDocument()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(DocumentRules, transformConfig: JsonNode.Parse(Rules));
        var id = await Kit.SubmitNewAsync(key, Record());

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", e.GetProperty("status").GetString());
        Assert.Equal("success", Step(e, "transform").GetProperty("outputPort").GetString());
        Assert.Equal("valid", Step(e, "validate").GetProperty("outputPort").GetString());
        Assert.Equal("INV-2026-003901.pdf", e.GetProperty("documents")[0].GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task ARuleThatCantBeApplied_EndsRejected_WithoutEchoingValues()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(DocumentRules, transformConfig: JsonNode.Parse(Rules));
        var id = await Kit.SubmitNewAsync(key, Record("\"Top Secret\""));

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Rejected", e.GetProperty("status").GetString());
        Assert.Equal("data.untransformable", e.GetProperty("failureCode").GetString());
        var step = Step(e, "transform");
        Assert.Equal("failed", step.GetProperty("outputPort").GetString());
        var issue = step.GetProperty("result").GetProperty("issues")[0];
        Assert.Equal("transform.not_number", issue.GetProperty("code").GetString());
        Assert.Equal("rules[0]", issue.GetProperty("field").GetString());
        Assert.DoesNotContain("Top Secret", e.GetRawText(), StringComparison.Ordinal);
        Assert.Empty(e.GetProperty("documents").EnumerateArray());
    }

    [Fact]
    public async Task ResumedRun_RebuildsTheTransformedData()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(DocumentRules, transformConfig: JsonNode.Parse(Rules));
        var id = await Kit.SubmitNewAsync(key, Record());
        _store.FailWrites = true;

        await Kit.RunEngineAsync(id, _printer, _store, _jobs); // the PDF step fails once and a retry is scheduled
        _store.FailWrites = false;
        await Kit.RunEngineAsync(id, _printer, _store, _jobs); // resumes: the rules are restored, not run again

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal("INV-2026-003901.pdf", done.GetProperty("documents")[0].GetProperty("fileName").GetString());
        Assert.Equal(1, Step(done, "transform").GetProperty("attempt").GetInt32());
    }
}
