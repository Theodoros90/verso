using Verso.Application.Workflows;
using Verso.Domain.Executions;

namespace Verso.Application.Executions;

/// <summary>
/// How a published (validated) workflow definition runs: steps in run order, and which node follows an output port.
/// A node's handler chooses one output port and each port has at most one edge, so a run follows one path, except after a Parallel branches step, which runs its two paths one after the other (ADR 0009).
/// </summary>
public sealed class ExecutionPlan
{
    private readonly Dictionary<(string Node, string Port), string> _next;

    private ExecutionPlan(ParsedDefinition definition, WorkflowNode trigger, IReadOnlyList<WorkflowNode> ordered, IReadOnlyList<LoopInfo> loops)
    {
        Definition = definition;
        Trigger = trigger;
        Ordered = ordered;
        Loops = loops;
        _next = definition.Edges
            .GroupBy(e => (e.Source, e.SourcePort))
            .ToDictionary(g => g.Key, g => g.First().Target);
    }

    public ParsedDefinition Definition { get; }
    public WorkflowNode Trigger { get; }

    /// <summary>All nodes, topologically sorted (ties keep definition order). Used as the step sequence.</summary>
    public IReadOnlyList<WorkflowNode> Ordered { get; }

    /// <summary>The For each loops. A step in a loop is created for item 1; the other items add their rows as they run.</summary>
    public IReadOnlyList<LoopInfo> Loops { get; }

    public IReadOnlyList<PlannedStep> Steps => [.. Ordered.Select(n => new PlannedStep(n.Id, n.Type, n.Title, Loops.Any(l => l.Body.Contains(n.Id)) ? 1 : 0))];

    public WorkflowNode Node(string id) => Ordered.Single(n => n.Id == id);

    /// <summary>The node connected to <paramref name="port"/> of <paramref name="nodeId"/>, or null at the end of a path.</summary>
    public WorkflowNode? Next(string nodeId, string port) =>
        _next.TryGetValue((nodeId, port), out var target) ? Node(target) : null;

    public static ExecutionPlan Create(ParsedDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var trigger = definition.Nodes.SingleOrDefault(n => NodeCatalog.Find(n.Type)?.Category == "input")
            ?? throw new InvalidOperationException("The workflow definition has no single trigger; published versions always have one.");

        // Kahn's algorithm; the queue is kept in definition order so the result is stable.
        var index = definition.Nodes.Select((n, i) => (n.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        var incoming = definition.Nodes.ToDictionary(n => n.Id, _ => 0, StringComparer.Ordinal);
        var loops = LoopAnalysis.Find(definition);
        var loopEdges = loops.SelectMany(l => l.BackEdgeCandidates).Select(e => e.Id).ToHashSet(StringComparer.Ordinal); // returns to a For each aren't ordering constraints
        var forward = definition.Edges.Where(e => incoming.ContainsKey(e.Target) && incoming.ContainsKey(e.Source) && !loopEdges.Contains(e.Id)).ToList();
        foreach (var edge in forward)
        {
            incoming[edge.Target]++;
        }

        var ready = new SortedSet<int>(definition.Nodes.Where(n => incoming[n.Id] == 0).Select(n => index[n.Id]));
        var ordered = new List<WorkflowNode>();
        while (ready.Count > 0)
        {
            var node = definition.Nodes[ready.Min];
            ready.Remove(ready.Min);
            ordered.Add(node);
            foreach (var edge in forward.Where(e => e.Source == node.Id))
            {
                if (--incoming[edge.Target] == 0)
                {
                    ready.Add(index[edge.Target]);
                }
            }
        }

        if (ordered.Count != definition.Nodes.Count)
        {
            throw new InvalidOperationException("The workflow definition has a cycle; published versions never do.");
        }

        return new ExecutionPlan(definition, trigger, ordered, loops);
    }
}
