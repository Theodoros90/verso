using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Verso.Rendering.Binding;

namespace Verso.Application.Executions;

/// <summary>A rule that couldn't be applied. Messages name the rule and the field path; they never echo values.</summary>
public sealed record TransformIssue(string Code, string Message, string Field);

/// <summary>The data after transforming, or the rules that failed.</summary>
public sealed record TransformResult(JsonElement? Data, IReadOnlyList<TransformIssue> Issues);

/// <summary>
/// The Transform data step (prototype: "Transform data": "Calculates, reformats or reshapes values"). The prototype takes a JSONata or
/// JavaScript expression; Verso never runs author-written code, so the step is an ordered list of rules from a fixed set of operations
/// instead. Each rule writes its result to a target field; rules run top to bottom and a rule sees what the rules above it wrote, so a
/// total can be calculated and then rounded. An operand is a data path or a plain number. A rule whose operand is missing or of the
/// wrong kind fails the step (the run ends Rejected); only <c>default</c> treats a missing field as normal.
/// </summary>
public static partial class DataTransformer
{
    public const int MaxRules = 100;
    private const int MaxText = 2000;

    /// <summary>Operation keys and their labels. Which of a/b/text each one reads is in <see cref="Uses"/>.</summary>
    public static readonly IReadOnlyDictionary<string, string> Operations = new Dictionary<string, string>
    {
        ["copy"] = "Copy a field",
        ["text"] = "Build text from fields",
        ["add"] = "Add (A + B)",
        ["subtract"] = "Subtract (A − B)",
        ["multiply"] = "Multiply (A × B)",
        ["divide"] = "Divide (A ÷ B)",
        ["round"] = "Round A to B decimals",
        ["trim"] = "Trim spaces",
        ["uppercase"] = "Uppercase",
        ["lowercase"] = "Lowercase",
        ["date"] = "Format date (YYYY-MM-DD)",
        ["default"] = "Use a fallback when empty",
        ["remove"] = "Remove the field",
    };

    /// <summary>Which settings of a rule an operation reads: A (first operand), B (second operand), T (the text).</summary>
    public static (bool A, bool B, bool Text, bool BOptional) Uses(string operation) => operation switch
    {
        "copy" or "trim" or "uppercase" or "lowercase" or "date" => (true, false, false, false),
        "add" or "subtract" or "multiply" or "divide" => (true, true, false, false),
        "round" => (true, true, false, true),
        "default" => (true, false, true, false),
        "text" => (false, false, true, false),
        _ => (false, false, false, false), // remove
    };

    public static TransformResult Apply(JsonElement config, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return new(null, [new("transform.not_record", "The data isn't a JSON object, so there is nothing to transform.", "")]);
        }

        var root = (JsonObject)JsonNode.Parse(data.GetRawText())!;
        var issues = new List<TransformIssue>();
        var index = -1;
        foreach (var rule in Rules(config))
        {
            index++;
            var current = JsonSerializer.Deserialize<JsonElement>(root.ToJsonString());
            var outcome = Run(rule, current);
            if (outcome.Failure is { } failure)
            {
                issues.Add(new(failure.Code, $"Rule {index + 1}: {failure.Message}", $"rules[{index}]"));
                continue;
            }

            if (rule.Operation == "remove")
            {
                Remove(root, rule.Target);
            }
            else
            {
                Set(root, rule.Target, outcome.Value);
            }
        }

        return issues.Count > 0
            ? new(null, issues)
            : new(JsonSerializer.Deserialize<JsonElement>(root.ToJsonString()), []);
    }

    private readonly record struct Rule(string Target, string Operation, string A, string B, string Text);

    private static IEnumerable<Rule> Rules(JsonElement config)
    {
        if (!config.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var r in rules.EnumerateArray())
        {
            if (r.ValueKind == JsonValueKind.Object && Text(r, "target") is { Length: > 0 } target && Text(r, "operation") is { Length: > 0 } operation)
            {
                yield return new(target, operation, (Text(r, "a") ?? "").Trim(), (Text(r, "b") ?? "").Trim(), Text(r, "text") ?? ""); // config is schema-validated on publish
            }
        }
    }

    private static string? Text(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private readonly record struct Failure(string Code, string Message);

    private readonly record struct Outcome(JsonNode? Value, Failure? Failure);

    private static Outcome Fail(string code, string message) => new(null, new Failure(code, message));

    private static Outcome Ok(JsonNode? value) => new(value, null);

    private static Outcome Run(Rule rule, JsonElement data)
    {
        switch (rule.Operation)
        {
            case "remove":
                return Ok(null);

            case "text":
                {
                    var resolved = PatternText.Resolve(rule.Text, data);
                    if (resolved.Missing.Count > 0)
                    {
                        return Fail("transform.missing", $"no value for '{resolved.Missing[0]}' (Build text from fields).");
                    }

                    return resolved.Text.Length > MaxText ? Fail("transform.too_long", "the text is longer than 2000 characters.") : Ok(JsonValue.Create(resolved.Text));
                }

            case "default":
                {
                    if (TryOperand(rule.A, data, out var value) && !IsEmpty(value))
                    {
                        return Ok(JsonNode.Parse(value.GetRawText()));
                    }

                    return Ok(decimal.TryParse(rule.Text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) && PlainNumber().IsMatch(rule.Text)
                        ? JsonValue.Create(n)
                        : JsonValue.Create(rule.Text));
                }
        }

        if (!TryOperand(rule.A, data, out var a))
        {
            return Fail("transform.missing", $"no value for '{rule.A}' ({Label(rule.Operation)}).");
        }

        switch (rule.Operation)
        {
            case "copy":
                return Ok(JsonNode.Parse(a.GetRawText()));
            case "trim" or "uppercase" or "lowercase":
                if (a.ValueKind != JsonValueKind.String)
                {
                    return Ok(JsonNode.Parse(a.GetRawText())); // like Map fields: only text changes
                }

                var s = a.GetString()!;
                return Ok(JsonValue.Create(rule.Operation switch { "trim" => s.Trim(), "uppercase" => s.ToUpperInvariant(), _ => s.ToLowerInvariant() }));
            case "date":
                return a.ValueKind == JsonValueKind.String && FieldMapper.TryDate(a.GetString()!.Trim(), out var date)
                    ? Ok(JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
                    : Fail("transform.not_date", $"'{rule.A}' isn't a date that can be read (Format date).");
        }

        // Calculations.
        if (FieldMapper.AsNumber(a) is not { } x)
        {
            return Fail("transform.not_number", $"'{rule.A}' can't be read as a number ({Label(rule.Operation)}).");
        }

        decimal? y = null;
        if (rule.B.Length > 0)
        {
            if (!TryOperand(rule.B, data, out var b))
            {
                return Fail("transform.missing", $"no value for '{rule.B}' ({Label(rule.Operation)}).");
            }

            y = FieldMapper.AsNumber(b);
            if (y is null)
            {
                return Fail("transform.not_number", $"'{rule.B}' can't be read as a number ({Label(rule.Operation)}).");
            }
        }

        if (y is null && rule.Operation != "round")
        {
            return Fail("transform.missing", $"the second value is empty ({Label(rule.Operation)}).");
        }

        try
        {
            switch (rule.Operation)
            {
                case "add": return Ok(Number(x + y!.Value));
                case "subtract": return Ok(Number(x - y!.Value));
                case "multiply": return Ok(Number(x * y!.Value));
                case "divide":
                    return y == 0 ? Fail("transform.divide_by_zero", $"'{rule.B}' is zero (Divide).") : Ok(Number(x / y!.Value));
                case "round":
                    var places = y ?? 2;
                    return places is < 0 or > 8 || places != decimal.Truncate(places)
                        ? Fail("transform.decimals", "the number of decimals must be a whole number from 0 to 8 (Round).")
                        : Ok(Number(Math.Round(x, (int)places, MidpointRounding.AwayFromZero)));
                default:
                    return Fail("transform.unknown", $"unknown operation '{rule.Operation}'.");
            }
        }
        catch (OverflowException)
        {
            return Fail("transform.overflow", $"the result is too large ({Label(rule.Operation)}).");
        }
    }

    /// <summary>A decimal without trailing zeros (12.0 is written as 12).</summary>
    private static JsonValue Number(decimal value) => JsonValue.Create(value / 1.000000000000000000000000000m);

    private static string Label(string operation) => Operations.TryGetValue(operation, out var l) ? l : operation;

    private static bool IsEmpty(JsonElement v) =>
        v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString());

    /// <summary>A plain number such as <c>19</c> or <c>-0.5</c> is itself; anything else is a data path, which must be present (and not null).</summary>
    private static bool TryOperand(string operand, JsonElement data, out JsonElement value)
    {
        if (operand.Length > 0 && PlainNumber().IsMatch(operand))
        {
            value = JsonSerializer.Deserialize<JsonElement>(operand);
            return true;
        }

        value = default;
        return operand.Length > 0 && DataPath.IsValid(operand) && DataPath.TryResolve(data, operand, out value);
    }

    private static void Set(JsonObject root, string path, JsonNode? value)
    {
        var segments = path.Split('.');
        var node = root;
        foreach (var segment in segments[..^1])
        {
            if (node[segment] is not JsonObject child)
            {
                child = [];
                node[segment] = child; // a scalar or list in the way is replaced
            }

            node = child;
        }

        node[segments[^1]] = value;
    }

    private static void Remove(JsonObject root, string path)
    {
        var segments = path.Split('.');
        var node = root;
        foreach (var segment in segments[..^1])
        {
            if (node[segment] is not JsonObject child)
            {
                return;
            }

            node = child;
        }

        node.Remove(segments[^1]);
    }

    /// <summary>What a rule needs that its settings don't give (used when a workflow is validated). Null when complete.</summary>
    public static string? Incomplete(string operation, string target, string a, string b, string text)
    {
        if (!Operations.ContainsKey(operation))
        {
            return $"Unknown operation '{operation}'.";
        }

        if (!DataPath.IsValid(target))
        {
            return "The target must be a field path such as totals.gross.";
        }

        var uses = Uses(operation);
        if (uses.A && !IsOperand(a))
        {
            return "The first value must be a field path or a number.";
        }

        if (uses.B && !uses.BOptional && !IsOperand(b))
        {
            return "The second value must be a field path or a number.";
        }

        if (uses.B && uses.BOptional && b.Length > 0 && !IsOperand(b))
        {
            return "The second value must be a field path or a number.";
        }

        if (uses.Text && operation == "text" && text.Trim().Length == 0)
        {
            return "Enter the text to build, such as {{customer.name}} ({{invoice.number}}).";
        }

        return null;
    }

    private static bool IsOperand(string s) => s.Length > 0 && (PlainNumber().IsMatch(s) || DataPath.IsValid(s));

    [GeneratedRegex(@"^-?[0-9]+(\.[0-9]+)?$")]
    private static partial Regex PlainNumber();
}
