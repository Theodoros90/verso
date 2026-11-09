using System.Text.Json;
using System.Text.Json.Nodes;
using Verso.Application.Workflows;

namespace Verso.UnitTests.Workflows;

public sealed class GraphValidatorTests
{
    private static readonly Guid TemplateId = Guid.Parse("01a1c000-0000-7000-8000-000000000001");

    private static readonly Dictionary<Guid, TemplateRef> PublishedTemplate = new()
    {
        [TemplateId] = new TemplateRef(TemplateId, "Commercial invoice", new HashSet<int> { 1, 2 }),
    };

    /// <summary>The "Invoice standard" workflow from the M6 acceptance criteria: trigger → validate → generate PDF → archive.</summary>
    internal static JsonObject InvoiceStandard(Guid templateId, string version = "latest-published") => (JsonObject)JsonNode.Parse($$"""
        { "nodes": [
            { "id": "trigger", "type": "api-trigger", "title": "API trigger", "position": { "x": 0, "y": 80 }, "config": {} },
            { "id": "validate", "type": "validate", "title": "Validate invoice data", "position": { "x": 260, "y": 80 },
              "config": { "required": ["invoice.number", "customer.name"], "types": [ { "field": "invoice.date", "type": "date" }, { "field": "totals.gross", "type": "number" } ] } },
            { "id": "pdf", "type": "generate-pdf", "title": "Generate invoice PDF", "position": { "x": 520, "y": 80 },
              "config": { "templateId": "{{templateId}}", "templateVersion": "{{version}}", "fileName": "{{"{{"}}invoice.number{{"}}"}}.pdf" } },
            { "id": "archive", "type": "archive", "title": "Archive", "position": { "x": 780, "y": 80 },
              "config": { "folderPattern": "invoices/{{"{{"}}invoice.date|year{{"}}"}}", "retention": "7y", "metadata": [ { "key": "InvoiceNumber", "value": "{{"{{"}}invoice.number{{"}}"}}" } ] } } ],
          "edges": [
            { "id": "e1", "source": "trigger", "sourcePort": "out", "target": "validate", "targetPort": "in" },
            { "id": "e2", "source": "validate", "sourcePort": "valid", "target": "pdf", "targetPort": "in" },
            { "id": "e3", "source": "pdf", "sourcePort": "success", "target": "archive", "targetPort": "in" } ] }
        """)!;

    private static WorkflowValidationDto Validate(JsonNode definition, IReadOnlyDictionary<Guid, TemplateRef>? templates = null)
    {
        using var doc = JsonDocument.Parse(definition.ToJsonString());
        var (parsed, errors) = WorkflowDefinitionParser.Parse(doc.RootElement);
        Assert.True(parsed is not null, string.Join("; ", errors.Select(e => $"{e.Key}: {e.Value[0]}")));
        return GraphValidator.Validate(parsed!, templates ?? PublishedTemplate);
    }

    private static List<string> Codes(WorkflowValidationDto v, string severity = GraphValidator.Error) =>
        [.. v.Issues.Where(i => i.Severity == severity).Select(i => $"{i.NodeId}:{i.Code}")];

    [Fact]
    public void InvoiceStandard_IsValid_WithWarningsOnlyForTheOptionalPorts()
    {
        var result = Validate(InvoiceStandard(TemplateId));

        Assert.True(result.Valid);
        Assert.Empty(Codes(result));
        Assert.Equal(["validate:port.unconnected", "pdf:port.unconnected"], Codes(result, GraphValidator.Warning));
        Assert.Contains(result.Issues, i => i.Port == "invalid" && i.Message == "Invalid records will end the run as Rejected.");
    }

    [Fact]
    public void TemplateWithoutPublishedVersion_IsAnError()
    {
        var draftOnly = new Dictionary<Guid, TemplateRef> { [TemplateId] = new(TemplateId, "Commercial invoice", new HashSet<int>()) };

        var result = Validate(InvoiceStandard(TemplateId), draftOnly);

        Assert.False(result.Valid);
        var issue = Assert.Single(result.Issues, i => i.Code == "template.not_published");
        Assert.Equal("pdf", issue.NodeId);
        Assert.Equal("templateVersion", issue.Field);
    }

    [Fact]
    public void SpecificVersion_MustBePublished_AndTheTemplateMustExist()
    {
        Assert.Contains("pdf:template.not_published", Codes(Validate(InvoiceStandard(TemplateId, "3"))));
        Assert.True(Validate(InvoiceStandard(TemplateId, "2")).Valid);
        Assert.Contains("pdf:template.not_found", Codes(Validate(InvoiceStandard(Guid.NewGuid()))));
    }

    [Fact]
    public void DisconnectedGraph_ReportsEachProblemOnItsNode()
    {
        var def = InvoiceStandard(TemplateId);
        def["edges"]!.AsArray().RemoveAt(2); // pdf → archive

        var codes = Codes(Validate(def));

        Assert.Contains("pdf:port.unconnected", codes);   // required 'success'
        Assert.Contains("archive:node.unreachable", codes);
    }

    [Fact]
    public void Cycles_DuplicateOutputs_UnknownPortsAndNodes_AreErrors()
    {
        var def = InvoiceStandard(TemplateId);
        var edges = def["edges"]!.AsArray();
        edges.Add(JsonNode.Parse("""{ "id": "loop", "source": "pdf", "sourcePort": "failed", "target": "validate", "targetPort": "in" }"""));
        edges.Add(JsonNode.Parse("""{ "id": "dup", "source": "validate", "sourcePort": "valid", "target": "archive", "targetPort": "in" }"""));
        edges.Add(JsonNode.Parse("""{ "id": "bad", "source": "trigger", "sourcePort": "nope", "target": "archive", "targetPort": "in" }"""));
        edges.Add(JsonNode.Parse("""{ "id": "ghost", "source": "trigger", "sourcePort": "out", "target": "missing", "targetPort": "in" }"""));

        var issues = Validate(def).Issues.Where(i => i.Severity == GraphValidator.Error).ToList();

        Assert.Contains(issues, i => i.Code == "graph.cycle" && i.EdgeId == "loop");
        Assert.Contains(issues, i => i.Code == "edge.duplicate_output" && i.EdgeId == "dup");
        Assert.Contains(issues, i => i.Code == "edge.unknown_port" && i.EdgeId == "bad");
        Assert.Contains(issues, i => i.Code == "edge.unknown_node" && i.EdgeId == "ghost");
    }

    [Fact]
    public void Triggers_ExactlyOne()
    {
        var none = InvoiceStandard(TemplateId);
        none["nodes"]!.AsArray().RemoveAt(0);
        none["edges"]!.AsArray().RemoveAt(0);
        Assert.Contains(":graph.no_trigger", Codes(Validate(none)));

        var two = InvoiceStandard(TemplateId);
        two["nodes"]!.AsArray().Add(JsonNode.Parse("""{ "id": "t2", "type": "api-trigger", "title": "Second", "position": { "x": 0, "y": 300 }, "config": {} }"""));
        Assert.Contains("t2:graph.multiple_triggers", Codes(Validate(two)));
    }

    [Fact]
    public void Settings_AreCheckedAgainstTheNodeSchema()
    {
        var def = InvoiceStandard(TemplateId);
        var nodes = def["nodes"]!.AsArray();
        nodes[1]!["config"] = JsonNode.Parse("""{ "required": [], "types": [] }""");                                    // no rules
        nodes[2]!["config"]!["fileName"] = "";                                                                            // required
        nodes[3]!["config"]!["retention"] = "forever";                                                                     // not in enum
        nodes[3]!["config"]!["metadata"] = JsonNode.Parse("""[ { "key": "1bad", "value": "x" } ]""");                     // pattern
        nodes[3]!["config"]!["colour"] = "red";                                                                            // unknown

        var issues = Validate(def).Issues;

        Assert.Contains(issues, i => i.NodeId == "validate" && i.Code == "config.required" && i.Message.StartsWith("Add at least one rule", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.NodeId == "pdf" && i.Field == "fileName" && i.Message == "Output filename pattern is required.");
        Assert.Contains(issues, i => i.NodeId == "archive" && i.Field == "retention");
        Assert.Contains(issues, i => i.NodeId == "archive" && i.Field == "metadata[0].key");
        Assert.Contains(issues, i => i.NodeId == "archive" && i.Field == "colour" && i.Code == "config.unknown");
    }

    [Fact]
    public void UnknownNodeType_IsAnErrorOnThatNode()
    {
        var def = InvoiceStandard(TemplateId);
        def["nodes"]!.AsArray()[3]!["type"] = "send-email";

        Assert.Contains("archive:node.unknown_type", Codes(Validate(def)));
    }

    [Fact]
    public void Parser_RejectsBrokenStructure_WithFieldPaths()
    {
        using var doc = JsonDocument.Parse("""
            { "nodes": [ { "id": "a b", "type": "validate", "title": "x", "position": { "x": "1", "y": 0 }, "config": [] },
                         { "id": "n", "type": "validate", "title": "x", "position": { "x": 1, "y": 0 }, "config": {}, "extra": 1 },
                         { "id": "n", "type": "validate", "title": "x", "position": { "x": 1, "y": 0 }, "config": {} } ],
              "edges": [], "meta": {} }
            """);

        var (parsed, errors) = WorkflowDefinitionParser.Parse(doc.RootElement);

        Assert.Null(parsed);
        Assert.Contains("definition.meta", errors.Keys);
        Assert.Contains("definition.nodes[0].id", errors.Keys);
        Assert.Contains("definition.nodes[0].position", errors.Keys);
        Assert.Contains("definition.nodes[0].config", errors.Keys);
        Assert.Contains("definition.nodes[1].extra", errors.Keys);
        Assert.Contains("definition.nodes[2].id", errors.Keys); // duplicate
    }

    [Fact]
    public void Catalog_DefaultsSatisfyTheirOwnSchemas_ExceptRequiredUserInput()
    {
        foreach (var type in NodeCatalog.All)
        {
            var errors = ConfigSchemaValidator.Validate(type.ConfigSchema, type.Defaults);
            // generate-pdf has no template chosen yet; everything else is valid out of the box.
            var expected = type.Type == NodeCatalog.GeneratePdf ? ["templateId"] : Array.Empty<string>();
            Assert.Equal(expected, errors.Select(e => e.Field).ToArray());
        }
    }
}
