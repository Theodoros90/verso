using System.Text.Json;

namespace Verso.Application.Workflows;

/// <summary>A problem in a workflow graph, attached to a node (and port or setting) where possible.</summary>
public sealed record WorkflowIssueDto(string Severity, string Code, string Message, string? NodeId = null, string? EdgeId = null, string? Port = null, string? Field = null);

public sealed record WorkflowValidationDto(bool Valid, IReadOnlyList<WorkflowIssueDto> Issues);

/// <summary>What the validator needs to know about a referenced template. Fields: its data model (latest published version), for the Map fields check.</summary>
public sealed record TemplateRef(Guid Id, string Name, IReadOnlySet<int> PublishedVersions, IReadOnlyList<string>? Fields = null);

/// <summary>What the validator needs to know about an approved folder location.</summary>
public sealed record FolderLocationRef(string Key, string Name, bool Input, bool Output, bool Enabled);

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

    public static WorkflowValidationDto Validate(
        ParsedDefinition definition, IReadOnlyDictionary<Guid, TemplateRef> templates, IReadOnlyDictionary<string, FolderLocationRef>? locations = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(templates);
        locations ??= new Dictionary<string, FolderLocationRef>();
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

            if (types.TryGetValue(edge.Source, out var sourceType) && NodeCatalog.OutputsOf(sourceType, nodes[edge.Source].Config).All(p => p.Id != edge.SourcePort))
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

        // Loops: a For each may be returned to by one step of its own loop. Those connections are not cycles; everything else that loops is.
        var loops = LoopAnalysis.Find(definition);
        var loopEdges = loops.SelectMany(l => l.BackEdgeCandidates).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var list in outgoing.Values)
        {
            list.RemoveAll(e => loopEdges.Contains(e.Id));
        }

        foreach (var loop in loops)
        {
            var forEachTitle = loop.ForEach.Title;
            foreach (var edge in loop.BackEdgeCandidates)
            {
                if (!LoopAnalysis.CanLoopBack(nodes[edge.Source]))
                {
                    issues.Add(new(Error, "loop.back_edge_source", $"'{nodes[edge.Source].Title}' can't connect back into '{forEachTitle}': only a plain step can end an item (not a For each, If / else or Switch). Connect the last plain step of the loop instead, or leave the path unconnected.", NodeId: edge.Source, EdgeId: edge.Id));
                }
                else if (edge.Id != loop.BackEdge?.Id)
                {
                    issues.Add(new(Error, "loop.multiple_back_edges", $"'{forEachTitle}' already has a step connected back into it; a loop has one.", NodeId: edge.Source, EdgeId: edge.Id));
                }
            }

            foreach (var inner in loops.Where(o => o != loop && loop.Body.Contains(o.ForEach.Id)))
            {
                issues.Add(new(Error, "loop.nested", $"'{inner.ForEach.Title}' is inside the loop of '{forEachTitle}'; a For each can't be inside another For each.", NodeId: inner.ForEach.Id));
            }
        }

        // A step must be inside one loop or outside all of them: reaching it from both would run it with and without an item.
        if (loops.Count > 0 && triggers.Count == 1)
        {
            var allEdges = definition.Edges.ToList();
            foreach (var loop in loops)
            {
                var outside = LoopAnalysis.Reach(
                    triggers[0].Id, [.. allEdges.Where(e => !loopEdges.Contains(e.Id) && !(e.Source == loop.ForEach.Id && e.SourcePort == Executions.ForEach.Each))], _ => false, includeStart: true);
                foreach (var shared in loop.Body.Where(outside.Contains))
                {
                    issues.Add(new(Error, "loop.shared_step", $"'{nodes[shared].Title}' can be reached both inside and outside the loop of '{loop.ForEach.Title}'; keep the loop's steps separate from the rest of the workflow.", NodeId: shared));
                }
            }
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

            foreach (var port in NodeCatalog.OutputsOf(type, node.Config).Where(p => !usedOutputs.Contains((node.Id, p.Id))))
            {
                issues.Add(port.Required
                    ? new(Error, "port.unconnected", $"Connect '{port.Label}' to the next step.", NodeId: node.Id, Port: port.Id)
                    : new(Warning, "port.unconnected", UnconnectedOptional(type, port), NodeId: node.Id, Port: port.Id));
            }

            foreach (var e in ConfigSchemaValidator.Validate(type.ConfigSchema, node.Config))
            {
                issues.Add(new(Error, e.Code, e.Message, NodeId: node.Id, Field: e.Field));
            }

            issues.AddRange(NodeRules(node, type, templates, MappingTarget(definition, templates)));
            if (type.Type == NodeCatalog.FolderInput)
            {
                issues.AddRange(FolderRules(node, locations));
            }
        }

        return new WorkflowValidationDto(issues.All(i => i.Severity != Error), issues);
    }

    /// <summary>The template a Map fields step prepares data for: the first one a Generate PDF step uses (the designer shows the same one).</summary>
    private static TemplateRef? MappingTarget(ParsedDefinition definition, IReadOnlyDictionary<Guid, TemplateRef> templates) =>
        definition.Nodes
            .Where(n => NodeCatalog.IsDocumentStep(n.Type) && n.Config.TryGetProperty("templateId", out var t) && t.ValueKind == JsonValueKind.String)
            .Select(n => Guid.TryParse(n.Config.GetProperty("templateId").GetString(), out var g) && templates.TryGetValue(g, out var template) ? template : null)
            .FirstOrDefault(t => t is not null);

    private static string UnconnectedOptional(NodeTypeDto type, PortDto port) => (type.Type, port.Id) switch
    {
        (NodeCatalog.Validate, "invalid") => "Invalid records will end the run as Rejected.",
        (NodeCatalog.ParseContent, "failed") => "Content that can't be parsed will end the run as Rejected.",
        (NodeCatalog.MapFields, "failed") => "A value that can't be transformed will end the run as Rejected.",
        (NodeCatalog.TransformData, "failed") => "A rule that can't be applied will end the run as Rejected.",
        (NodeCatalog.MergeData, "failed") => "A record that can't be merged will end the run as Rejected.",
        (NodeCatalog.ApprovalStep, "rejected") => "A declined approval will end the run as Rejected.",
        (NodeCatalog.ForEach, "empty") => "An empty list ends the run here: nothing is repeated.",
        (NodeCatalog.IfElse, "yes") => "Records that meet the condition end the run here.",
        (NodeCatalog.IfElse, "no") => "Records that don't meet the condition end the run here.",
        (NodeCatalog.GeneratePdf or NodeCatalog.GenerateDocx, "failed") => "Data that can't make a complete document will end the run as Rejected.",
        _ => $"'{port.Label}' isn't connected; the run ends there.",
    };

    private static string Setting(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

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
    private static IEnumerable<WorkflowIssueDto> NodeRules(WorkflowNode node, NodeTypeDto type, IReadOnlyDictionary<Guid, TemplateRef> templates, TemplateRef? mappingTarget)
    {
        var c = node.Config;
        switch (type.Type)
        {
            case NodeCatalog.ApiTrigger:
                foreach (var (field, label) in new[] { ("expectedSchema", "The expected JSON schema"), ("samplePayload", "The sample payload") })
                {
                    if (c.TryGetProperty(field, out var text) && text.ValueKind == JsonValueKind.String
                        && text.GetString()!.Trim().Length > 0 && !IsJsonObject(text.GetString()!))
                    {
                        yield return new(Error, "config.invalid", $"{label} must be a JSON object.", NodeId: node.Id, Field: field);
                    }
                }

                break;

            case NodeCatalog.Validate:
                var rules = Count(c, "required") + Count(c, "types");
                if (rules == 0)
                {
                    yield return new(Error, "config.required", "Add at least one rule (a required field or a type check).", NodeId: node.Id);
                }

                break;

            case NodeCatalog.MapFields:
                if (Count(c, "rows") == 0)
                {
                    yield return new(Error, "config.required", "Add at least one mapping.", NodeId: node.Id);
                    break;
                }

                if (mappingTarget?.Fields is { Count: > 0 } fields && Executions.FieldMapper.Unmapped(c, fields) is { Count: > 0 } unmapped)
                {
                    yield return new(Warning, "map.unmapped",
                        $"No mapping for {string.Join(", ", unmapped)}, which '{mappingTarget.Name}' uses. That's fine if the data already has these fields under that name.", NodeId: node.Id);
                }

                break;

            case NodeCatalog.TransformData:
                if (Count(c, "rules") == 0)
                {
                    yield return new(Error, "config.required", "Add at least one rule.", NodeId: node.Id);
                    break;
                }

                var ruleNumber = 0;
                foreach (var rule in c.GetProperty("rules").EnumerateArray())
                {
                    ruleNumber++;
                    if (Executions.DataTransformer.Incomplete(Setting(rule, "operation"), Setting(rule, "target"), Setting(rule, "a"), Setting(rule, "b"), Setting(rule, "text")) is { } problem)
                    {
                        yield return new(Error, "config.invalid", $"Rule {ruleNumber}: {problem}", NodeId: node.Id, Field: $"rules[{ruleNumber - 1}]");
                    }
                }

                break;

            case NodeCatalog.MergeData:
                foreach (var problem in Executions.DataMerger.Incomplete(c))
                {
                    yield return new(Error, "config.invalid", problem, NodeId: node.Id);
                }

                break;

            case NodeCatalog.IfElse:
                foreach (var (field, problem) in Executions.IfElse.Incomplete(c))
                {
                    yield return new(Error, "config.invalid", problem, NodeId: node.Id, Field: field);
                }

                break;

            case NodeCatalog.SwitchRoute:
                foreach (var (field, problem) in Executions.Switch.Incomplete(c))
                {
                    yield return new(Error, "config.invalid", problem, NodeId: node.Id, Field: field);
                }

                break;

            case NodeCatalog.GeneratePdf:
            case NodeCatalog.GenerateDocx:
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

    /// <summary>
    /// Folder input: each of the three folders is a typed absolute path (shape checked here; the Worker validates it
    /// where it lives) or, in workflows saved before paths could be typed, an approved location with a subfolder that
    /// stays inside it. Processed and failed files must not land where the trigger scans (they would be picked up again).
    /// </summary>
    private static IEnumerable<WorkflowIssueDto> FolderRules(WorkflowNode node, IReadOnlyDictionary<string, FolderLocationRef> locations)
    {
        var c = node.Config;
        string Text(string name) => c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

        foreach (var (role, input, label, title) in new[]
                 {
                     ("source", true, "source", "Source folder"), ("processed", false, "processed files", "Processed files"), ("failed", false, "failed files", "Failed files"),
                 })
        {
            var path = Text(role + "Path");
            var key = Text(role + "Location");
            if (path.Length > 0)
            {
                if (Ingestion.FolderPaths.AbsolutePathProblem(path) is { } pathProblem)
                {
                    yield return new(Error, "folder.path_invalid", pathProblem, NodeId: node.Id, Field: role + "Path");
                }

                continue;
            }

            if (key.Length == 0)
            {
                yield return new(Error, "config.required", $"{title} is required: type the folder's path.", NodeId: node.Id, Field: role + "Path");
                continue;
            }

            // A workflow saved when folders were chosen from approved locations.
            if (!locations.TryGetValue(key, out var location))
            {
                yield return new(Error, "folder.location_unknown", $"'{key}' isn't an approved location. Type the folder's path instead.", NodeId: node.Id, Field: role + "Location");
            }
            else if (!location.Enabled)
            {
                yield return new(Error, "folder.location_disabled", $"'{location.Name}' is currently not available on the Worker. Type a path instead, or ask an administrator.", NodeId: node.Id, Field: role + "Location");
            }
            else if (input ? !location.Input : !location.Output)
            {
                yield return new(Error, "folder.location_purpose", $"'{location.Name}' isn't approved for {label}.", NodeId: node.Id, Field: role + "Location");
            }

            if (Ingestion.FolderPaths.SubfolderProblem(Ingestion.FolderPaths.NormalizeSubfolder(Text(role + "Subfolder"))) is { } problem)
            {
                yield return new(Error, "folder.subfolder_invalid", problem, NodeId: node.Id, Field: role + "Subfolder");
            }
        }

        var include = c.TryGetProperty("includeSubfolders", out var inc) && inc.ValueKind == JsonValueKind.True;
        const string Again = "Files moved here would be picked up again. Choose a folder outside the scanned folder";
        if (Ingestion.FolderInputSettings.Parse(c) is { } settings)
        {
            foreach (var (role, destination) in new[] { ("processed", settings.Processed), ("failed", settings.Failed) })
            {
                var typed = Text("sourcePath").Length > 0 && Text(role + "Path").Length > 0;
                var loops = typed
                    ? Ingestion.FolderPaths.PathWouldRescan(Text("sourcePath"), Text(role + "Path"), include)
                    : Ingestion.FolderPaths.WouldRescan(settings.Source, destination, include);
                if (loops)
                {
                    yield return new(Error, "folder.destination_scanned", Again + (include ? " (subfolders are included)." : "."),
                        NodeId: node.Id, Field: Text(role + "Path").Length > 0 ? role + "Path" : role + "Subfolder");
                }
            }
        }
    }

    private static int Count(JsonElement config, string name) =>
        config.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.GetArrayLength() : 0;
}
