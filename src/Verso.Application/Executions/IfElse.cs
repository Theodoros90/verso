using System.Globalization;
using System.Text.Json;
using Verso.Rendering.Binding;

namespace Verso.Application.Executions;

/// <summary>
/// The If / else step (prototype: "If / else": "Sends records down one of two paths"). It checks conditions on the run's data: each one
/// reads a field and compares it, with a fixed operator (no expressions, no scripts), with a value typed into the step or with another
/// field of the data (<c>compare</c>: <c>value</c> or <c>field</c>; for <c>field</c> the setting <c>value</c> is a data path). Conditions
/// are joined by the <c>join</c> of each row after the first, <c>and</c> (default) or <c>or</c>; AND binds tighter than OR, as usual
/// (<c>A or B and C</c> is <c>A or (B and C)</c>); there are no groups. The run continues by the <c>yes</c> output when the whole
/// expression holds and by <c>no</c> otherwise. The data is not changed. Comparison (as in the prototype): text is trimmed and compared
/// without regard to case; the number operators compare numbers (a JSON number, or text holding a plain decimal such as <c>4797.28</c>)
/// and are false when either side isn't one; a missing, null or blank field is "empty", and so is an empty list or object. When the
/// field to compare with is missing, every comparison is false except <c>not-equals</c> and <c>not-contains</c>.
/// </summary>
public static class IfElse
{
    public const string Yes = "yes";
    public const string No = "no";

    public const int MaxConditions = 10;

    public const string IsEqual = "equals";
    public const string NotEquals = "not-equals";
    public const string Contains = "contains";
    public const string NotContains = "not-contains";
    public const string StartsWith = "starts-with";
    public const string EndsWith = "ends-with";
    public const string GreaterThan = "greater-than";
    public const string GreaterOrEqual = "greater-or-equal";
    public const string LessThan = "less-than";
    public const string LessOrEqual = "less-or-equal";
    public const string IsEmpty = "is-empty";
    public const string IsNotEmpty = "is-not-empty";

    public const string FromValue = "value";
    public const string FromField = "field";
    public const string And = "and";
    public const string Or = "or";

    public static readonly IReadOnlyDictionary<string, string> Operators = new Dictionary<string, string>
    {
        [IsEqual] = "is",
        [NotEquals] = "is not",
        [Contains] = "contains",
        [NotContains] = "does not contain",
        [StartsWith] = "starts with",
        [EndsWith] = "ends with",
        [GreaterThan] = "is greater than",
        [GreaterOrEqual] = "is at least",
        [LessThan] = "is less than",
        [LessOrEqual] = "is at most",
        [IsEmpty] = "is empty",
        [IsNotEmpty] = "is not empty",
    };

    public static readonly IReadOnlyDictionary<string, string> Compares = new Dictionary<string, string>
    {
        [FromValue] = "A value",
        [FromField] = "A field of the data",
    };

    public static readonly IReadOnlyDictionary<string, string> Joins = new Dictionary<string, string>
    {
        [And] = "AND",
        [Or] = "OR",
    };

    /// <summary>Operators that don't compare with anything.</summary>
    public static bool NeedsNoValue(string op) => op is IsEmpty or IsNotEmpty;

    private static bool IsNumeric(string op) => op is GreaterThan or GreaterOrEqual or LessThan or LessOrEqual;

    /// <summary>True when the conditions hold (the <c>yes</c> path). Never fails: a field that isn't there is just empty.</summary>
    public static bool Evaluate(JsonElement config, JsonElement data)
    {
        // OR separates groups of ANDed conditions: the expression holds when any group does.
        var any = false;
        var group = (bool?)null;
        var first = true;
        foreach (var condition in Conditions(config))
        {
            var met = Met(condition, data);
            if (first || Setting(condition, "join") != Or)
            {
                group = (group ?? true) && met;
            }
            else
            {
                any |= group == true;
                group = met;
            }

            first = false;
        }

        return any || group == true;
    }

    private static IEnumerable<JsonElement> Conditions(JsonElement config) =>
        config.TryGetProperty("conditions", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object)
            : [];

    private static bool Met(JsonElement condition, JsonElement data)
    {
        var op = Setting(condition, "op");
        var found = DataPath.TryResolve(data, Setting(condition, "field"), out var value);

        var empty = !found || value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!.Trim().Length == 0,
            JsonValueKind.Array => value.GetArrayLength() == 0,
            JsonValueKind.Object => !value.EnumerateObject().Any(),
            _ => false,
        };
        if (op == IsEmpty)
        {
            return empty;
        }

        if (op == IsNotEmpty)
        {
            return !empty;
        }

        // What to compare with: the typed value, or the other field's value.
        string expected;
        decimal? expectedNumber;
        if (Setting(condition, "compare") == FromField)
        {
            if (!DataPath.TryResolve(data, Setting(condition, "value"), out var other))
            {
                return op is NotEquals or NotContains;
            }

            expected = Text(other);
            expectedNumber = Number(other);
        }
        else
        {
            expected = Setting(condition, "value");
            expectedNumber = Number(expected);
        }

        var text = found ? Text(value) : "";
        var actual = found ? Number(value) : null;
        return op switch
        {
            IsEqual => string.Equals(text, expected, StringComparison.OrdinalIgnoreCase),
            NotEquals => !string.Equals(text, expected, StringComparison.OrdinalIgnoreCase),
            Contains => text.Contains(expected, StringComparison.OrdinalIgnoreCase),
            NotContains => !text.Contains(expected, StringComparison.OrdinalIgnoreCase),
            StartsWith => text.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
            EndsWith => text.EndsWith(expected, StringComparison.OrdinalIgnoreCase),
            GreaterThan => actual is { } a && expectedNumber is { } b && a > b,
            GreaterOrEqual => actual is { } a2 && expectedNumber is { } b2 && a2 >= b2,
            LessThan => actual is { } a3 && expectedNumber is { } b3 && a3 < b3,
            LessOrEqual => actual is { } a4 && expectedNumber is { } b4 && a4 <= b4,
            _ => false,
        };
    }

    /// <summary>
    /// Problems the settings schema can't express, each with the setting it belongs to (<c>conditions[0].value</c>).
    /// Empty when complete. A missing field or operator is reported by the schema check.
    /// </summary>
    public static IReadOnlyList<(string Field, string Message)> Incomplete(JsonElement config)
    {
        var problems = new List<(string, string)>();
        var conditions = Conditions(config).ToList();
        if (conditions.Count == 0)
        {
            problems.Add(("conditions", "Add at least one condition."));
        }

        for (var i = 0; i < conditions.Count; i++)
        {
            var op = Setting(conditions[i], "op");
            if (!Operators.ContainsKey(op) || NeedsNoValue(op))
            {
                continue;
            }

            var value = Setting(conditions[i], "value");
            if (Setting(conditions[i], "compare") == FromField)
            {
                if (!DataPath.IsValid(value))
                {
                    problems.Add(($"conditions[{i}].value", $"Condition {i + 1}: choose the field to compare with, such as totals.net."));
                }
            }
            else if (value.Length == 0)
            {
                problems.Add(($"conditions[{i}].value", $"Condition {i + 1}: enter the value to compare with."));
            }
            else if (IsNumeric(op) && Number(value) is null)
            {
                problems.Add(($"conditions[{i}].value", $"Condition {i + 1}: comparing sizes needs a number such as 10000 as the value."));
            }
        }

        return problems;
    }

    private static string Setting(JsonElement config, string name) =>
        config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

    /// <summary>Scalars as text (numbers and booleans as written); objects and lists have none.</summary>
    private static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!.Trim(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        _ => "",
    };

    private static decimal? Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.TryGetDecimal(out var d) ? d : null,
        JsonValueKind.String => Number(value.GetString()!),
        _ => null,
    };

    private static decimal? Number(string text) =>
        decimal.TryParse(text.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) ? d : null;
}
