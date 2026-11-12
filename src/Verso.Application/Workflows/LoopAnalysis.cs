namespace Verso.Application.Workflows;

/// <summary>
/// One For each step and its loop. <see cref="BackEdge"/> is the connection that returns to the For each (null when the loop ends by its
/// paths simply ending); <see cref="BackEdgeCandidates"/> are all connections that lead back into it from its own loop (more than one, or
/// from a step that isn't allowed to, are validation errors). <see cref="Body"/> is every node reachable from the <c>each</c> output.
/// </summary>
public sealed record LoopInfo(
    WorkflowNode ForEach, WorkflowEdge? BackEdge, IReadOnlyList<WorkflowEdge> BackEdgeCandidates, IReadOnlySet<string> Body);

/// <summary>
/// Finds the loops of a workflow definition. The only cycle a workflow may have is the one a For each allows: a step of its loop connecting
/// back into the For each's input (as in the prototype, that step can't be a For each, If / else or Switch). Everything else that
/// loops is a validation error (<c>graph.cycle</c>).
/// </summary>
public static class LoopAnalysis
{
    public static IReadOnlyList<LoopInfo> Find(ParsedDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var nodes = definition.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var edges = definition.Edges.Where(e => nodes.ContainsKey(e.Source) && nodes.ContainsKey(e.Target)).ToList();
        var forEachNodes = definition.Nodes.Where(n => n.Type == NodeCatalog.ForEach).ToList();
        if (forEachNodes.Count == 0)
        {
            return [];
        }

        var candidates = forEachNodes.ToDictionary(
            f => f.Id,
            f =>
            {
                var reach = Reach(f.Id, edges, _ => false);
                return (IReadOnlyList<WorkflowEdge>)[.. edges.Where(e => e.Target == f.Id && e.TargetPort == "in" && e.Source != f.Id && reach.Contains(e.Source))];
            },
            StringComparer.Ordinal);
        var excluded = candidates.Values.SelectMany(c => c).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

        return [.. forEachNodes.Select(f =>
        {
            var backEdge = candidates[f.Id].FirstOrDefault(e => CanLoopBack(nodes[e.Source]));
            var start = edges.FirstOrDefault(e => e.Source == f.Id && e.SourcePort == "each");
            var body = start is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : Reach(start.Target, edges.Where(e => !excluded.Contains(e.Id)).ToList(), id => id == f.Id, includeStart: true);
            body.Remove(f.Id);
            return new LoopInfo(f, backEdge, candidates[f.Id], body);
        })];
    }

    /// <summary>Only a plain step can return to the For each: not a For each, an If / else or a Switch.</summary>
    public static bool CanLoopBack(WorkflowNode source) =>
        source.Type is not (NodeCatalog.ForEach or NodeCatalog.IfElse or NodeCatalog.SwitchRoute);

    /// <summary>The nodes reachable from <paramref name="start"/> (not including it unless something leads back), not expanding nodes for which <paramref name="stop"/> is true.</summary>
    public static HashSet<string> Reach(string start, IReadOnlyList<WorkflowEdge> edges, Func<string, bool> stop, bool includeStart = false)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        if (includeStart)
        {
            seen.Add(start);
            if (!stop(start))
            {
                queue.Enqueue(start);
            }
        }
        else
        {
            queue.Enqueue(start);
        }

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var edge in edges.Where(e => e.Source == id))
            {
                if (seen.Add(edge.Target) && !stop(edge.Target))
                {
                    queue.Enqueue(edge.Target);
                }
            }
        }

        return seen;
    }
}
