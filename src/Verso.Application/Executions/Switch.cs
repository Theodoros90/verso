using System.Text.Json;
using System.Text.RegularExpressions;
using Verso.Application.Workflows;
using Verso.Rendering.Binding;

namespace Verso.Application.Executions;

/// <summary>
/// The Switch step (prototype: "Switch": "Routes records by a field's value"). It reads one field of the run's data and compares it,
/// with one operator, against each branch's value (a typed value, or another field of the data) in order; the run continues by the
/// first branch that matches. With <c>other</c> on, a record that matches no branch leaves by the <c>other</c> output; with it off the
/// run ends there as Succeeded (like any output with nothing connected). The step's outputs are its branches, so unlike other steps
/// they come from the settings (<see cref="Ports"/>), not from the catalog. The data is not changed.
/// </summary>
public static partial class Switch
{
    public const string Other = "other";
    public const int MaxBranches = 10;

    /// <summary>Branch ids become output port ids: lower case, short, and never the reserved <c>other</c>.</summary>
    public const string IdPattern = "^[a-z][a-z0-9-]{0,29}$";

    [GeneratedRegex(IdPattern)]
    private static partial Regex IdRegex();

    /// <summary>Operators a Switch can use: those that compare with a value.</summary>
    public static readonly IReadOnlyDictionary<string, string> Operators =
        IfElse.Operators.Where(o => !IfElse.NeedsNoValue(o.Key)).ToDictionary(o => o.Key, o => o.Value);

    private static IEnumerable<JsonElement> Branches(JsonElement config) =>
        config.TryGetProperty("branches", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(b => b.ValueKind == JsonValueKind.Object)
            : [];

    private static bool OtherOn(JsonElement config) => config.TryGetProperty("other", out var o) && o.ValueKind == JsonValueKind.True;

    /// <summary>The step's outputs: one per branch (named by its label), then "Other" when that is on. All optional, like If / else.</summary>
    public static IReadOnlyList<PortDto> Ports(JsonElement config)
    {
        var ports = new List<PortDto>();
        foreach (var branch in Branches(config))
        {
            if (IfElse.SettingText(branch, "id") is { Length: > 0 } id && id != Other && ports.All(p => p.Id != id))
            {
                ports.Add(new PortDto(id, IfElse.SettingText(branch, "label") is { Length: > 0 } label ? label : "Untitled", false));
            }
        }

        if (OtherOn(config))
        {
            ports.Add(new PortDto(Other, "Other", false));
        }

        return ports;
    }

    /// <summary>The output the run leaves by: the first matching branch's id, else <c>other</c> (when on), else null (the run ends).</summary>
    public static string? Pick(JsonElement config, JsonElement data)
    {
        var field = IfElse.SettingText(config, "field");
        var op = IfElse.SettingText(config, "op");
        foreach (var branch in Branches(config))
        {
            if (IfElse.Compare(data, field, op, IfElse.SettingText(branch, "compare"), IfElse.SettingText(branch, "value")))
            {
                return IfElse.SettingText(branch, "id");
            }
        }

        return OtherOn(config) ? Other : null;
    }

    /// <summary>Problems the settings schema can't express, each with the setting it belongs to (<c>branches[1].value</c>).</summary>
    public static IReadOnlyList<(string Field, string Message)> Incomplete(JsonElement config)
    {
        var problems = new List<(string, string)>();
        var branches = Branches(config).ToList();
        if (branches.Count == 0 && !OtherOn(config))
        {
            problems.Add(("branches", "Add at least one branch."));
        }

        var op = IfElse.SettingText(config, "op");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < branches.Count; i++)
        {
            var id = IfElse.SettingText(branches[i], "id");
            if (!IdRegex().IsMatch(id) || id == Other)
            {
                problems.Add(($"branches[{i}].id", $"Branch {i + 1} has an invalid id."));
            }
            else if (!seen.Add(id))
            {
                problems.Add(($"branches[{i}].id", $"Branch {i + 1} repeats the id of an earlier branch."));
            }

            if (IfElse.SettingText(branches[i], "label").Length == 0)
            {
                problems.Add(($"branches[{i}].label", $"Branch {i + 1}: give the branch a name."));
            }

            if (IfElse.ValueProblem(op, IfElse.SettingText(branches[i], "compare"), IfElse.SettingText(branches[i], "value")) is { } problem)
            {
                problems.Add(($"branches[{i}].value", $"Branch {i + 1}: {problem}"));
            }
        }

        return problems;
    }
}
