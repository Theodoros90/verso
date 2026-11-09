using System.Text.Json;
using System.Text.RegularExpressions;

namespace Verso.Application.Workflows;

public sealed record NodePosition(double X, double Y);

public sealed record WorkflowNode(string Id, string Type, string Title, NodePosition Position, JsonElement Config);

public sealed record WorkflowEdge(string Id, string Source, string SourcePort, string Target, string TargetPort);

public sealed record ParsedDefinition(IReadOnlyList<WorkflowNode> Nodes, IReadOnlyList<WorkflowEdge> Edges);

/// <summary>
/// Structural parsing of a workflow definition (plan §4):
/// <c>{ nodes: [{ id, type, title, position: { x, y }, config }], edges: [{ id, source, sourcePort, target, targetPort }] }</c>.
/// Structure errors reject a save; graph problems (unknown types, missing connections, invalid settings) are
/// reported by <see cref="GraphValidator"/> as issues, so an unfinished draft can still be saved.
/// </summary>
public static partial class WorkflowDefinitionParser
{
    public const int MaxDefinitionChars = 500_000;
    public const int MaxNodes = 200;
    public const int MaxEdges = 400;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdPattern();

    public static (ParsedDefinition? Definition, IReadOnlyDictionary<string, string[]> Errors) Parse(JsonElement root)
    {
        var errors = new Dictionary<string, List<string>>();
        void Error(string path, string message)
        {
            if (!errors.TryGetValue(path, out var list))
            {
                errors[path] = list = [];
            }

            list.Add(message);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            Error("definition", "Must be a JSON object with 'nodes' and 'edges'.");
            return (null, Freeze(errors));
        }

        if (root.GetRawText().Length > MaxDefinitionChars)
        {
            Error("definition", $"At most {MaxDefinitionChars:N0} characters.");
            return (null, Freeze(errors));
        }

        AllowOnly(root, "definition", Error, "nodes", "edges");
        var nodes = new List<WorkflowNode>();
        var edges = new List<WorkflowEdge>();
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var edgeIds = new HashSet<string>(StringComparer.Ordinal);

        if (!root.TryGetProperty("nodes", out var nodesElement) || nodesElement.ValueKind != JsonValueKind.Array)
        {
            Error("definition.nodes", "Must be an array.");
        }
        else if (nodesElement.GetArrayLength() > MaxNodes)
        {
            Error("definition.nodes", $"At most {MaxNodes} nodes.");
        }
        else
        {
            var i = 0;
            foreach (var n in nodesElement.EnumerateArray())
            {
                var path = $"definition.nodes[{i++}]";
                if (n.ValueKind != JsonValueKind.Object)
                {
                    Error(path, "Must be an object.");
                    continue;
                }

                AllowOnly(n, path, Error, "id", "type", "title", "position", "config");
                var id = Id(n, "id", path, Error);
                var type = Str(n, "type", path, Error, 1, 64);
                var title = Str(n, "title", path, Error, 0, 100);
                NodePosition? position = null;
                if (!n.TryGetProperty("position", out var pos) || pos.ValueKind != JsonValueKind.Object
                    || !pos.TryGetProperty("x", out var x) || x.ValueKind != JsonValueKind.Number
                    || !pos.TryGetProperty("y", out var y) || y.ValueKind != JsonValueKind.Number)
                {
                    Error($"{path}.position", "Must be { \"x\": number, \"y\": number }.");
                }
                else
                {
                    AllowOnly(pos, $"{path}.position", Error, "x", "y");
                    position = new NodePosition(x.GetDouble(), y.GetDouble());
                }

                if (!n.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object)
                {
                    Error($"{path}.config", "Must be an object.");
                }

                if (id is not null && !nodeIds.Add(id))
                {
                    Error($"{path}.id", $"Duplicate node id '{id}'.");
                }

                if (id is not null && type is not null && title is not null && position is not null && config.ValueKind == JsonValueKind.Object)
                {
                    nodes.Add(new WorkflowNode(id, type, title, position, config.Clone()));
                }
            }
        }

        if (!root.TryGetProperty("edges", out var edgesElement) || edgesElement.ValueKind != JsonValueKind.Array)
        {
            Error("definition.edges", "Must be an array.");
        }
        else if (edgesElement.GetArrayLength() > MaxEdges)
        {
            Error("definition.edges", $"At most {MaxEdges} edges.");
        }
        else
        {
            var i = 0;
            foreach (var e in edgesElement.EnumerateArray())
            {
                var path = $"definition.edges[{i++}]";
                if (e.ValueKind != JsonValueKind.Object)
                {
                    Error(path, "Must be an object.");
                    continue;
                }

                AllowOnly(e, path, Error, "id", "source", "sourcePort", "target", "targetPort");
                var id = Id(e, "id", path, Error);
                var source = Id(e, "source", path, Error);
                var sourcePort = Str(e, "sourcePort", path, Error, 1, 32);
                var target = Id(e, "target", path, Error);
                var targetPort = Str(e, "targetPort", path, Error, 1, 32);
                if (id is not null && !edgeIds.Add(id))
                {
                    Error($"{path}.id", $"Duplicate edge id '{id}'.");
                }

                if (id is not null && source is not null && sourcePort is not null && target is not null && targetPort is not null)
                {
                    edges.Add(new WorkflowEdge(id, source, sourcePort, target, targetPort));
                }
            }
        }

        return errors.Count > 0 ? (null, Freeze(errors)) : (new ParsedDefinition(nodes, edges), Freeze(errors));
    }

    public static (ParsedDefinition? Definition, IReadOnlyDictionary<string, string[]> Errors) Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Parse(document.RootElement);
    }

    private static string? Id(JsonElement obj, string name, string path, Action<string, string> error)
    {
        var value = Str(obj, name, path, error, 1, 64);
        if (value is not null && !IdPattern().IsMatch(value))
        {
            error($"{path}.{name}", "Use 1–64 letters, digits, '-' or '_'.");
            return null;
        }

        return value;
    }

    private static string? Str(JsonElement obj, string name, string path, Action<string, string> error, int min, int max)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
        {
            error($"{path}.{name}", "Required (text).");
            return null;
        }

        var s = v.GetString()!;
        if (s.Length < min || s.Length > max)
        {
            error($"{path}.{name}", min > 0 ? $"{min}–{max} characters." : $"At most {max} characters.");
            return null;
        }

        return s;
    }

    private static void AllowOnly(JsonElement obj, string path, Action<string, string> error, params string[] allowed)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (!allowed.Contains(p.Name, StringComparer.Ordinal))
            {
                error($"{path}.{p.Name}", $"Unknown property. Allowed: {string.Join(", ", allowed)}.");
            }
        }
    }

    private static Dictionary<string, string[]> Freeze(Dictionary<string, List<string>> errors) =>
        errors.ToDictionary(e => e.Key, e => e.Value.ToArray());
}
