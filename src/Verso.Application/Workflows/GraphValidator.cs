using System.Text.Json;

namespace Verso.Application.Workflows;

/// <summary>A problem in a workflow graph, attached to a node (and port or setting) where possible.</summary>
public sealed record WorkflowIssueDto(string Severity, string Code, string Message, string? NodeId = null, string? EdgeId = null, string? Port = null, string? Field = null);

public sealed record WorkflowValidationDto(bool Valid, IReadOnlyList<WorkflowIssueDto> Issues);

/// <summary>What the validator needs to know about a referenced template.</summary>
public sealed record TemplateRef(Guid Id, string Name, IReadOnlySet<int> PublishedVersions);

/// <summary>
/// Server-side graph validation (plan §5.4, M6): exactly one trigger; known node types and ports; one edge per
/// output port; no cycles; every node reachable from the trigger; required ports connected; settings valid
/// against the node's schema; referenced template versions exist and are published. Errors block publishing;
/// warnings (e.g. an unconnected optional 'invalid' port) don't.
/// </summary>
public static class GraphValidator
{
    public const string Error = "error";
    public const string Warning = "warning";

    public static WorkflowValidationDto Validate(ParsedDefinition definition, IReadOnlyDictionary<Guid, TemplateRef> templates)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(templates);
        var issues = new List<WorkflowIssueDto>();
        var nodes = definition.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var types = new Dictionary<string, NodeTypeDto>(StringComparer.Ordinal);

        foreach (var node in definition.Nodes)
        {
            if (NodeCatalog.Find(node.Type) is { } type)
            {
                types[node.Id] = type;
            }
            else
            {
                issues.Add(new(Error, "node.unknown_type", $"Unknown node type '{node.Type}'.", NodeId: node.Id));
            }
        }

        // Trigger
        var triggers = definition.Nodes.Where(n => types.TryGetValue(n.Id, out var t) && t.Category == "input").ToList();
        if (triggers.Count == 0)
        {
            issues.Add(new(Error, "graph.no_trigger", "Add a trigger: every workflow starts with one."));
        }
        else if (triggers.Count > 1)
        {
            foreach (var extra in triggers.Skip(1))
            {
                issues.Add(new(Error, "graph.multiple_triggers", "A workflow has exactly one trigger.", NodeId: extra.Id));
            }
        }

        // Edges
        var outgoing = new Dictionary<string, List<WorkflowEdge>>(StringComparer.Ordinal);
        var usedOutputs = new HashSet<(string, string)>();
        var connectedInputs = new HashSet<(string, string)>();
        foreach (var edge in definition.Edges)
        {
            if (!nodes.ContainsKey(edge.Source) || !nodes.ContainsKey(edge.Target))
            {
                issues.Add(new(Error, "edge.unknown_node", "This connection points to a node that doesn't exist.", EdgeId: edge.Id));
                continue;
            }

            if (edge.Source == edge.Target)
            {
                issues.Add(new(Error, "graph.cycle", "A node can't connect to itself.", NodeId: edge.Source, EdgeId: edge.Id));
                continue;
            }

            if (types.TryGetValue(edge.Source, out var sourceType) && sourceType.Outputs.All(p => p.Id != edge.SourcePort))
            {
                issues.Add(new(Error, "edge.unknown_port", $"'{nodes[edge.Source].Title}' has no output '{edge.SourcePort}'.", NodeId: edge.Source, EdgeId: edge.Id, Port: edge.SourcePort));
                continue;
            }

            if (types.TryGetValue(edge.Target, out var targetType) && targetType.Inputs.All(p => p.Id != edge.TargetPort))
            {
                issues.Add(new(Error, "edge.unknown_port", $"'{nodes[edge.Target].Title}' has no input '{edge.TargetPort}'.", NodeId: edge.Target, EdgeId: edge.Id, Port: edge.TargetPort));
                continue;
            }

            if (!usedOutputs.Add((edge.Source, edge.SourcePort)))
            {
                issues.Add(new(Error, "edge.duplicate_output", $"Output '{edge.SourcePort}' already has a connection; each output leads to one next step.", NodeId: edge.Source, EdgeId: edge.Id, Port: edge.SourcePort));
                continue;
            }

            connectedInputs.Add((edge.Target, edge.TargetPort));
            if (!outgoing.TryGetValue(edge.Source, out var list))
            {
                outgoing[edge.Source] = list = [];
            }

            list.Add(edge);
        }

        // Cycles (depth-first search, three colours)
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var reportedCycle = false;
        foreach (var node in definition.Nodes)
        {
            if (!reportedCycle && Visit(node.Id))
            {
                reportedCycle = true;
            }
        }

        bool Visit(string id)
        {
            state[id] = 1;
            foreach (var edge in outgoing.GetValueOrDefault(id) ?? [])
            {
                var s = state.GetValueOrDefault(edge.Target);
                if (s == 1)
                {
                    issues.Add(new(Error, "graph.cycle", "This connection creates a loop; workflows run in one direction.", NodeId: edge.Target, EdgeId: edge.Id));
                    return true;
                }

                if (s == 0 && Visit(edge.Target))
                {
                    return true;
                }
            }

            state[id] = 2;
            return false;
        }

        // Reachability from the trigger
        if (triggers.Count == 1)
        {
            var reached = new HashSet<string>(StringComparer.Ordinal) { triggers[0].Id };
            var queue = new Queue<string>([triggers[0].Id]);
            while (queue.Count > 0)
            {
                foreach (var edge in outgoing.GetValueOrDefault(queue.Dequeue()) ?? [])
                {
                    if (reached.Add(edge.Target))
                    {
                        queue.Enqueue(edge.Target);
                    }
                }
            }

            foreach (var node in definition.Nodes.Where(n => !reached.Contains(n.Id)))
            {
                issues.Add(new(Error, "node.unreachable", "Not connected to the trigger: this step would never run.", NodeId: node.Id));
            }
        }

        // Ports and settings per node
        foreach (var node in definition.Nodes)
        {
            if (!types.TryGetValue(node.Id, out var type))
            {
                continue;
            }

            foreach (var port in type.Outputs.Where(p => !usedOutputs.Contains((node.Id, p.Id))))
            {
                issues.Add(port.Required
                    ? new(Error, "port.unconnected", $"Connect '{port.Label}' to the next step.", NodeId: node.Id, Port: port.Id)
                    : new(Warning, "port.unconnected", UnconnectedOptional(type, port), NodeId: node.Id, Port: port.Id));
            }

            foreach (var e in ConfigSchemaValidator.Validate(type.ConfigSchema, node.Config))
            {
                issues.Add(new(Error, e.Code, e.Message, NodeId: node.Id, Field: e.Field));
            }

            issues.AddRange(NodeRules(node, type, templates));
        }

        return new WorkflowValidationDto(issues.All(i => i.Severity != Error), issues);
    }

    private static string UnconnectedOptional(NodeTypeDto type, PortDto port) => (type.Type, port.Id) switch
    {
        (NodeCatalog.Validate, "invalid") => "Invalid records will end the run as Rejected.",
        (NodeCatalog.GeneratePdf, "failed") => "Data that can't make a complete document will end the run as Rejected.",
        _ => $"'{port.Label}' isn't connected; the run ends there.",
    };

    private static bool IsJsonObject(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Rules a schema can't express.</summary>
    private static IEnumerable<WorkflowIssueDto> NodeRules(WorkflowNode node, NodeTypeDto type, IReadOnlyDictionary<Guid, TemplateRef> templates)
    {
        var c = node.Config;
        switch (type.Type)
        {
            case NodeCatalog.ApiTrigger:
                if (c.TryGetProperty("samplePayload", out var sample) && sample.ValueKind == JsonValueKind.String
                    && sample.GetString()!.Trim().Length > 0 && !IsJsonObject(sample.GetString()!))
                {
                    yield return new(Error, "config.invalid", "The sample payload must be a JSON object.", NodeId: node.Id, Field: "samplePayload");
                }

                break;

            case NodeCatalog.Validate:
                var rules = Count(c, "required") + Count(c, "types");
                if (rules == 0)
                {
                    yield return new(Error, "config.required", "Add at least one rule (a required field or a type check).", NodeId: node.Id);
                }

                break;

            case NodeCatalog.GeneratePdf:
                if (!c.TryGetProperty("templateId", out var idElement) || idElement.ValueKind != JsonValueKind.String
                    || !Guid.TryParse(idElement.GetString(), out var templateId))
                {
                    break; // reported by the schema check
                }

                if (!templates.TryGetValue(templateId, out var template))
                {
                    yield return new(Error, "template.not_found", "The selected template no longer exists.", NodeId: node.Id, Field: "templateId");
                    break;
                }

                var version = c.TryGetProperty("templateVersion", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (version == NodeCatalog.LatestPublished)
                {
                    if (template.PublishedVersions.Count == 0)
                    {
                        yield return new(Error, "template.not_published",
                            $"'{template.Name}' has no published version yet. Publish the template first; drafts can't be used in workflows.",
                            NodeId: node.Id, Field: "templateVersion");
                    }
                }
                else if (int.TryParse(version, out var number) && !template.PublishedVersions.Contains(number))
                {
                    yield return new(Error, "template.not_published", $"'{template.Name}' has no published version {number}.", NodeId: node.Id, Field: "templateVersion");
                }

                break;
        }
    }

    private static int Count(JsonElement config, string name) =>
        config.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.GetArrayLength() : 0;
}
