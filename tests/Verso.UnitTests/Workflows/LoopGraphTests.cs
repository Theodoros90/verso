using System.Text.Json;
using System.Text.Json.Nodes;
using Verso.Application.Executions;
using Verso.Application.Workflows;

namespace Verso.UnitTests.Workflows;

/// <summary>The one loop a workflow may have: a For each that one plain step of its own steps connects back into.</summary>
public sealed class LoopGraphTests
{
    private static readonly Guid TemplateId = Guid.Parse("01a1c000-0000-7000-8000-000000000001");
    private static readonly Dictionary<Guid, TemplateRef> Templates = new() { [TemplateId] = new(TemplateId, "Commercial invoice", new HashSet<int> { 1 }) };

    private const string LoopNode = """{ "id": "loop", "type": "for-each", "title": "For each line", "position": { "x": 130, "y": 200 }, "config": { "field": "lines", "mode": "each", "item": "item" } }""";

    /// <summary>trigger → loop; loop.each → validate → pdf → archive. Optionally pdf also returns to the loop (archive is then dropped: it has no outputs to loop from).</summary>
    private static JsonObject Workflow(bool backEdge = false)
    {
        var definition = GraphValidatorTests.InvoiceStandard(TemplateId);
        var edges = definition["edges"]!.AsArray();
        definition["nodes"]!.AsArray().Add(JsonNode.Parse(LoopNode));
        edges.First(e => (string?)e!["id"] == "e1")!["target"] = "loop";
        edges.Add(JsonNode.Parse("""{ "id": "e9", "source": "loop", "sourcePort": "each", "target": "validate", "targetPort": "in" }"""));
        if (backEdge)
        {
            var nodes = definition["nodes"]!.AsArray();
            nodes.Remove(nodes.First(n => (string?)n!["id"] == "archive"));
            edges.Remove(edges.First(e => (string?)e!["id"] == "e3"));
            edges.Add(JsonNode.Parse("""{ "id": "e10", "source": "pdf", "sourcePort": "success", "target": "loop", "targetPort": "in" }"""));
        }

        return definition;
    }

    private static WorkflowValidationDto Validate(JsonNode definition)
    {
        using var doc = JsonDocument.Parse(definition.ToJsonString());
        var (parsed, errors) = WorkflowDefinitionParser.Parse(doc.RootElement);
        Assert.True(parsed is not null, string.Join("; ", errors.Select(e => $"{e.Key}: {e.Value[0]}")));
        return GraphValidator.Validate(parsed!, Templates);
    }

    private static List<string> Errors(WorkflowValidationDto v) =>
        [.. v.Issues.Where(i => i.Severity == GraphValidator.Error).Select(i => $"{i.NodeId}:{i.Code}")];

    [Fact]
    public void ALoop_WithoutABackEdge_IsValid_AndTheEmptyPathOnlyWarns()
    {
        var result = Validate(Workflow());

        Assert.True(result.Valid, string.Join("; ", Errors(result)));
        Assert.Contains(result.Issues, i => i.Severity == GraphValidator.Warning && i.NodeId == "loop" && i.Port == "empty");
    }

    [Fact]
    public void ALoop_WithOneBackEdgeFromAPlainStep_IsValid_NotACycle()
    {
        var result = Validate(Workflow(backEdge: true));

        Assert.True(result.Valid, string.Join("; ", Errors(result)));
        Assert.DoesNotContain(result.Issues, i => i.Code == "graph.cycle");
    }

    [Fact]
    public void TheEachOutput_MustBeConnected()
    {
        var definition = Workflow();
        var edges = definition["edges"]!.AsArray();
        edges.Remove(edges.First(e => (string?)e!["id"] == "e9"));

        var codes = Errors(Validate(definition));

        Assert.Contains("loop:port.unconnected", codes);
    }

    [Fact]
    public void BackEdgeFromAnIfElse_IsRejected()
    {
        var definition = Workflow();
        definition["nodes"]!.AsArray().Add(JsonNode.Parse("""
            { "id": "cond", "type": "if-else", "title": "If / else", "position": { "x": 400, "y": 200 },
              "config": { "conditions": [ { "join": "and", "field": "item.qty", "op": "is-not-empty", "compare": "value", "value": "" } ] } }
            """));
        var edges = definition["edges"]!.AsArray();
        edges.First(e => (string?)e!["id"] == "e9")!["target"] = "cond";
        edges.Add(JsonNode.Parse("""{ "id": "e11", "source": "cond", "sourcePort": "yes", "target": "validate", "targetPort": "in" }"""));
        edges.Add(JsonNode.Parse("""{ "id": "e12", "source": "cond", "sourcePort": "no", "target": "loop", "targetPort": "in" }"""));

        Assert.Contains("cond:loop.back_edge_source", Errors(Validate(definition)));
    }

    [Fact]
    public void ASecondBackEdge_IsRejected()
    {
        var definition = Workflow(backEdge: true);
        definition["edges"]!.AsArray().Add(JsonNode.Parse("""{ "id": "e11", "source": "validate", "sourcePort": "invalid", "target": "loop", "targetPort": "in" }"""));

        var result = Validate(definition);

        // e10 (pdf) comes first in the list of connections, so the later one is the extra
        Assert.Contains(result.Issues, i => i.Code == "loop.multiple_back_edges" && i.EdgeId == "e11");
    }

    [Fact]
    public void ALoopThatIsNotAForEach_IsStillACycle()
    {
        var definition = GraphValidatorTests.InvoiceStandard(TemplateId);
        definition["edges"]!.AsArray().Add(JsonNode.Parse("""{ "id": "e9", "source": "pdf", "sourcePort": "failed", "target": "validate", "targetPort": "in" }"""));

        Assert.Contains("validate:graph.cycle", Errors(Validate(definition)));
    }

    [Fact]
    public void AForEachInsideAnotherForEach_IsRejected()
    {
        var definition = Workflow();
        definition["nodes"]!.AsArray().Add(JsonNode.Parse(LoopNode.Replace("\"loop\"", "\"inner\"")));
        var edges = definition["edges"]!.AsArray();
        edges.First(e => (string?)e!["id"] == "e9")!["target"] = "inner";
        edges.Add(JsonNode.Parse("""{ "id": "e11", "source": "inner", "sourcePort": "each", "target": "validate", "targetPort": "in" }"""));

        Assert.Contains("inner:loop.nested", Errors(Validate(definition)));
    }

    [Fact]
    public void AStepReachedBothInsideAndOutsideTheLoop_IsRejected()
    {
        var definition = Workflow();
        definition["edges"]!.AsArray().Add(JsonNode.Parse("""{ "id": "e11", "source": "loop", "sourcePort": "empty", "target": "validate", "targetPort": "in" }"""));

        Assert.Contains("validate:loop.shared_step", Errors(Validate(definition)));
    }

    [Fact]
    public void LoopAnalysis_FindsTheBodyAndTheBackEdge()
    {
        using var doc = JsonDocument.Parse(Workflow(backEdge: true).ToJsonString());
        var definition = WorkflowDefinitionParser.Parse(doc.RootElement).Definition!;

        var loop = Assert.Single(LoopAnalysis.Find(definition));

        Assert.Equal("e10", loop.BackEdge?.Id);
        Assert.Equal(["pdf", "validate"], loop.Body.Order());
    }

    [Fact]
    public void ThePlan_CountsLoopStepsForTheFirstItem_AndIgnoresTheBackEdgeInTheOrder()
    {
        using var doc = JsonDocument.Parse(Workflow(backEdge: true).ToJsonString());
        var plan = ExecutionPlan.Create(WorkflowDefinitionParser.Parse(doc.RootElement).Definition!);

        Assert.Equal([("trigger", 0), ("loop", 0), ("validate", 1), ("pdf", 1)], plan.Steps.Select(s => (s.NodeId, s.Iteration)));
        Assert.Equal("loop", plan.Next("pdf", "success")!.Id);
    }
}
