using System.Text.Json;
using System.Text.Json.Nodes;
using Verso.Application.Workflows;

namespace Verso.UnitTests.Workflows;

/// <summary>A Switch's outputs are its branches, so the graph checks read them from its settings.</summary>
public sealed class SwitchGraphTests
{
    private static readonly Guid TemplateId = Guid.Parse("01a1c000-0000-7000-8000-000000000001");

    private static readonly Dictionary<Guid, TemplateRef> Templates = new() { [TemplateId] = new(TemplateId, "Commercial invoice", new HashSet<int> { 1 }) };

    /// <summary>trigger → switch; its "email" output → validate → pdf → archive. "print" and "other" are left open.</summary>
    private static JsonObject Workflow(JsonNode? branches = null, string fromPort = "email")
    {
        var definition = GraphValidatorTests.InvoiceStandard(TemplateId);
        var sw = JsonNode.Parse("""
            { "id": "route", "type": "switch", "title": "Switch", "position": { "x": 130, "y": 200 },
              "config": { "field": "customer.deliveryPreference", "op": "equals", "other": true,
                "branches": [ { "id": "email", "label": "Email", "compare": "value", "value": "email" }, { "id": "print", "label": "Print", "compare": "value", "value": "print" } ] } }
            """)!;
        if (branches is not null)
        {
            sw["config"]!["branches"] = branches;
        }

        definition["nodes"]!.AsArray().Add(sw);
        definition["edges"]!.AsArray().First(e => (string?)e!["id"] == "e1")!["target"] = "route";
        definition["edges"]!.AsArray().Add(JsonNode.Parse($$"""{ "id": "e9", "source": "route", "sourcePort": "{{fromPort}}", "target": "validate", "targetPort": "in" }"""));
        return definition;
    }

    private static WorkflowValidationDto Validate(JsonNode definition)
    {
        using var doc = JsonDocument.Parse(definition.ToJsonString());
        var (parsed, errors) = WorkflowDefinitionParser.Parse(doc.RootElement);
        Assert.True(parsed is not null, string.Join("; ", errors.Select(e => $"{e.Key}: {e.Value[0]}")));
        return GraphValidator.Validate(parsed!, Templates);
    }

    [Fact]
    public void BranchOutputs_CanBeConnected_AndOpenOnesOnlyWarn()
    {
        var result = Validate(Workflow());

        Assert.DoesNotContain(result.Issues, i => i.Severity == GraphValidator.Error);
        Assert.Equal(["print", "other"], result.Issues.Where(i => i.NodeId == "route" && i.Code == "port.unconnected").Select(i => i.Port));
    }

    [Fact]
    public void OtherOutput_CanBeConnected() =>
        Assert.DoesNotContain(Validate(Workflow(fromPort: "other")).Issues, i => i.Severity == GraphValidator.Error);

    [Fact]
    public void AConnectionFromARemovedBranch_IsAnUnknownPort()
    {
        var result = Validate(Workflow(fromPort: "portal"));

        Assert.Contains(result.Issues, i => i.Code == "edge.unknown_port" && i.NodeId == "route" && i.Port == "portal");
        Assert.False(result.Valid);
    }

    [Fact]
    public void OtherIsNotAnOutput_WhenItIsOff()
    {
        var definition = Workflow(fromPort: "other");
        definition["nodes"]!.AsArray().Single(n => (string?)n!["id"] == "route")!["config"]!["other"] = false;

        Assert.Contains(Validate(definition).Issues, i => i.Code == "edge.unknown_port" && i.Port == "other");
    }

    [Fact]
    public void BranchSettings_AreChecked()
    {
        var branches = JsonNode.Parse("""
            [ { "id": "a", "label": "A", "compare": "value", "value": "" }, { "id": "a", "label": "B", "compare": "value", "value": "x" } ]
            """)!;

        var result = Validate(Workflow(branches, fromPort: "a"));

        Assert.Equal(["branches[0].value", "branches[1].id"], result.Issues.Where(i => i.Severity == GraphValidator.Error && i.NodeId == "route").Select(i => i.Field));
    }
}
