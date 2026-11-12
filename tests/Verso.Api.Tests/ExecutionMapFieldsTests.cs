using System.Text.Json;
using System.Text.Json.Nodes;

namespace Verso.Api.Tests;

/// <summary>The Map fields step in a real run: submitted source data is renamed and transformed into the template's data model.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ExecutionMapFieldsTests(MigratedDatabaseFixture fixture) : IDisposable
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

    /// <summary>An ERP-style record: different names, text where the model has numbers and dates.</summary>
    private static JsonObject ErpRecord(string gross = "4797.284")
    {
        // The synthetic invoice, minus the four fields the mapping supplies; everything else reaches the template unmapped.
        var record = (JsonObject)ExecutionTestKit.Sample.DeepClone();
        record["invoice"]!.AsObject().Remove("number");
        record["invoice"]!.AsObject().Remove("date");
        record["customer"]!.AsObject().Remove("name");
        record["totals"]!.AsObject().Remove("gross");
        record["no"] = "inv-2026-003901";
        record["issued"] = "02/11/2026";
        record["customerName"] = "  Kestrel Logistics Ltd ";
        record["amounts"] = new JsonObject { ["gross"] = gross };
        return record;
    }

    private const string Rows = """
        { "rows": [
            { "from": "no", "to": "invoice.number", "transform": "uppercase" },
            { "from": "issued", "to": "invoice.date", "transform": "date" },
            { "from": "customerName", "to": "customer.name", "transform": "trim" },
            { "from": "amounts.gross", "to": "totals.gross", "transform": "round2" } ] }
        """;

    private static readonly JsonNode DocumentRules = JsonNode.Parse("""
        { "required": ["invoice.number", "customer.name"], "types": [ { "field": "invoice.date", "type": "date" }, { "field": "totals.gross", "type": "number" } ] }
        """)!;

    private static JsonElement Step(JsonElement execution, string type) =>
        execution.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("type").GetString() == type);

    [Fact]
    public async Task MappedFields_ReachValidateAndTheDocument_AndUnmappedFieldsStay()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(DocumentRules, mapConfig: JsonNode.Parse(Rows));
        var id = await Kit.SubmitNewAsync(key, ErpRecord());

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", e.GetProperty("status").GetString());
        Assert.Equal("success", Step(e, "map-fields").GetProperty("outputPort").GetString());
        // The type checks passed, so the date was reformatted and the amount became a number; the file is named from the mapped number.
        Assert.Equal("valid", Step(e, "validate").GetProperty("outputPort").GetString());
        Assert.Equal("INV-2026-003901.pdf", e.GetProperty("documents")[0].GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task ValueThatCantBeTransformed_EndsRejected_WithoutEchoingIt()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(DocumentRules, mapConfig: JsonNode.Parse(Rows));
        var id = await Kit.SubmitNewAsync(key, ErpRecord("Top Secret"));

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Rejected", e.GetProperty("status").GetString());
        Assert.Equal("data.unmappable", e.GetProperty("failureCode").GetString());
        var map = Step(e, "map-fields");
        Assert.Equal("failed", map.GetProperty("outputPort").GetString());
        var issue = map.GetProperty("result").GetProperty("issues")[0];
        Assert.Equal("map.transform", issue.GetProperty("code").GetString());
        Assert.Equal("rows[3]", issue.GetProperty("field").GetString());
        Assert.DoesNotContain("Top Secret", e.GetRawText(), StringComparison.Ordinal);
        Assert.Empty(e.GetProperty("documents").EnumerateArray());
    }

    [Fact]
    public async Task ResumedRun_RebuildsTheMappedData()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(DocumentRules, mapConfig: JsonNode.Parse(Rows));
        var id = await Kit.SubmitNewAsync(key, ErpRecord());
        _store.FailWrites = true;

        await Kit.RunEngineAsync(id, _printer, _store, _jobs); // the PDF step fails once and a retry is scheduled
        _store.FailWrites = false;
        await Kit.RunEngineAsync(id, _printer, _store, _jobs); // resumes: the mapping is restored, not run again

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal("INV-2026-003901.pdf", done.GetProperty("documents")[0].GetProperty("fileName").GetString());
        Assert.Equal(1, Step(done, "map-fields").GetProperty("attempt").GetInt32());
    }
}
