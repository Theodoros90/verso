using System.Text.Json;
using System.Text.Json.Nodes;

namespace Verso.Api.Tests;

/// <summary>The Parse step in a real run: raw JSON / XML / CSV text in a field of the submitted data becomes the run's data.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ExecutionParseTests(MigratedDatabaseFixture fixture) : IDisposable
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

    private static JsonObject Raw(string text) => new() { ["content"] = text };

    private static List<string?> Statuses(JsonElement execution) =>
        [.. execution.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("status").GetString())];

    [Fact]
    public async Task JsonText_IsParsed_ThenValidatedRenderedAndArchived()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(parseConfig: JsonNode.Parse("""{ "source": "content", "format": "json", "encoding": "utf-8" }"""));
        var id = await Kit.SubmitNewAsync(key, Raw(ExecutionTestKit.Sample.ToJsonString()));

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(["Succeeded", "Succeeded", "Succeeded", "Succeeded", "Succeeded"], Statuses(done));
        var parse = done.GetProperty("steps")[1];
        Assert.Equal("parse", parse.GetProperty("type").GetString());
        Assert.Equal("success", parse.GetProperty("outputPort").GetString());
        Assert.Equal("json", parse.GetProperty("result").GetProperty("format").GetString());
        // The document is named from the parsed data (invoice.number), which only exists after parsing.
        Assert.Equal("INV-2026-003901.pdf", done.GetProperty("documents")[0].GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task XmlText_ReachesValidate_WithNumbersAsNumbers()
    {
        var rules = JsonNode.Parse("""{ "required": ["invoice.number"], "types": [ { "field": "totals.gross", "type": "number" }, { "field": "invoice.date", "type": "date" } ] }""");
        var key = await Kit.PublishInvoiceWorkflowAsync(rules, parseConfig: JsonNode.Parse("""{ "source": "content", "format": "auto", "encoding": "utf-8", "recordPath": "doc" }"""));
        var xml = "<doc><invoice><number>INV-1</number><date>2026-11-02</date></invoice><totals><gross>4797.28</gross></totals></doc>";
        var id = await Kit.SubmitNewAsync(key, Raw(xml));

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("xml", e.GetProperty("steps")[1].GetProperty("result").GetProperty("format").GetString());
        var validate = e.GetProperty("steps")[2];
        Assert.Equal("valid", validate.GetProperty("outputPort").GetString()); // 4797.28 passed the number check, so it was read as a number
        // The XML has no lines or customer, so the template can't be completed: the run ends Rejected at the PDF step, not at parsing.
        Assert.Equal("document.data_incomplete", e.GetProperty("failureCode").GetString());
    }

    [Fact]
    public async Task CsvText_WithOneRow_FlowsIntoValidate_AndFailsItsRules()
    {
        var rules = JsonNode.Parse("""{ "required": ["invoice_number", "customer"], "types": [ { "field": "gross", "type": "number" } ] }""");
        var key = await Kit.PublishInvoiceWorkflowAsync(rules, parseConfig: JsonNode.Parse("""{ "source": "content", "format": "csv", "encoding": "utf-8" }"""));
        var id = await Kit.SubmitNewAsync(key, Raw("invoice_number;customer;gross\nINV-1;;12,5\n"));

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Rejected", e.GetProperty("status").GetString());
        Assert.Equal("data.invalid", e.GetProperty("failureCode").GetString());
        var issues = e.GetProperty("steps")[2].GetProperty("result").GetProperty("issues").EnumerateArray().Select(i => (i.GetProperty("path").GetString(), i.GetProperty("rule").GetString())).ToList();
        Assert.Equal([("customer", "required"), ("gross", "type:number")], issues); // "12,5" stays text; the empty cell is missing
    }

    [Fact]
    public async Task UnparseableContent_EndsRejected_WithoutEchoingTheContent()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(parseConfig: JsonNode.Parse("""{ "source": "content", "format": "json", "encoding": "utf-8" }"""));
        var id = await Kit.SubmitNewAsync(key, Raw("""{ "customer": "Top Secret Ltd", oops }"""));

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Rejected", e.GetProperty("status").GetString());
        Assert.Equal("data.unparseable", e.GetProperty("failureCode").GetString());
        Assert.Equal(["Succeeded", "Succeeded", "Skipped", "Skipped", "Skipped"], Statuses(e));
        var parse = e.GetProperty("steps")[1];
        Assert.Equal("failed", parse.GetProperty("outputPort").GetString());
        Assert.Equal("parse.invalid", parse.GetProperty("result").GetProperty("issues")[0].GetProperty("code").GetString());
        Assert.DoesNotContain("Top Secret", e.GetRawText(), StringComparison.Ordinal);
        Assert.Empty(e.GetProperty("documents").EnumerateArray());
    }

    [Fact]
    public async Task MissingContentField_EndsRejected()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(parseConfig: JsonNode.Parse("""{ "source": "file", "format": "auto", "encoding": "utf-8" }"""));
        var id = await Kit.SubmitNewAsync(key, Raw("{}"));

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Rejected", e.GetProperty("status").GetString());
        Assert.Equal("parse.no_content", e.GetProperty("steps")[1].GetProperty("result").GetProperty("issues")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task ResumedRun_RebuildsTheParsedData_WithoutParsingStepRunningAgain()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(parseConfig: JsonNode.Parse("""{ "source": "content", "format": "json", "encoding": "utf-8" }"""));
        var id = await Kit.SubmitNewAsync(key, Raw(ExecutionTestKit.Sample.ToJsonString()));
        _store.FailWrites = true;

        await Kit.RunEngineAsync(id, _printer, _store, _jobs); // the PDF step fails once and a retry is scheduled
        Assert.Equal("Running", (await Kit.GetExecutionAsync(id)).GetProperty("status").GetString());

        _store.FailWrites = false;
        await Kit.RunEngineAsync(id, _printer, _store, _jobs); // resumes: Parse is restored, not run again

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(1, done.GetProperty("steps")[1].GetProperty("attempt").GetInt32());
        Assert.Equal("INV-2026-003901.pdf", done.GetProperty("documents")[0].GetProperty("fileName").GetString());
    }
}
