using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Verso.Rendering.Binding;

namespace Verso.Application.Executions;

/// <summary>A mapping row that couldn't be applied. Messages name the row and the transform; they never echo values.</summary>
public sealed record MapIssue(string Code, string Message, string Field);

/// <summary>The data after mapping, or the rows that failed.</summary>
public sealed record MapResult(JsonElement? Data, IReadOnlyList<MapIssue> Issues);

/// <summary>
/// The Map fields step (prototype: "Map fields"). Each row copies the value at a source path to a destination path, optionally
/// transformed. Sources are read from the data the step received, so row order doesn't matter. The mapped values are then merged
/// over that data: destination objects merge, anything else is replaced, and fields no row mentions stay available to later steps.
/// A source that isn't in the data is skipped (a Validate step downstream can demand it). Transforms are a fixed list, never expressions.
/// </summary>
public static partial class FieldMapper
{
    public const string AsIs = "as-is";
    private const int MaxSchemaDepth = 8;
    private const int MaxSchemaFields = 200;

    public static readonly IReadOnlyDictionary<string, string> Transforms = new Dictionary<string, string>
    {
        [AsIs] = "As is",
        ["trim"] = "Trim",
        ["uppercase"] = "Uppercase",
        ["number"] = "Number",
        ["round2"] = "Round to 2 decimals",
        ["date"] = "Format date (YYYY-MM-DD)",
    };

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd.MM.yyyy", "d.M.yyyy", "dd-MM-yyyy", "yyyy/MM/dd", "yyyyMMdd"];

    public static MapResult Apply(JsonElement config, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return new(null, [new("map.not_record", "The data isn't a JSON object, so there is nothing to map.", "")]);
        }

        var root = (JsonObject)JsonNode.Parse(data.GetRawText())!;
        var issues = new List<MapIssue>();
        var index = -1;
        foreach (var row in Rows(config))
        {
            index++;
            var field = $"rows[{index}]";
            if (!DataPath.TryResolve(data, row.From, out var value))
            {
                continue;
            }

            var mapped = Transform(row.Transform, value);
            if (mapped.Failure is { } failure)
            {
                issues.Add(new("map.transform", $"Row {index + 1}: {failure}", field));
                continue;
            }

            Set(root, row.To, mapped.Value);
        }

        return issues.Count > 0
            ? new(null, issues)
            : new(JsonSerializer.Deserialize<JsonElement>(root.ToJsonString()), []);
    }

    /// <summary>
    /// The fields of a template's data model: every scalar reachable through objects, and each list as one field (its columns belong to the rows).
    /// This is what the Required template fields list in the designer shows. Null or unreadable schemas give no fields.
    /// </summary>
    public static IReadOnlyList<string> TemplateFields(string? dataSchema)
    {
        var fields = new List<string>();
        if (string.IsNullOrWhiteSpace(dataSchema))
        {
            return fields;
        }

        try
        {
            using var doc = JsonDocument.Parse(dataSchema);
            Walk(doc.RootElement, "", 0);
        }
        catch (JsonException)
        {
            return [];
        }

        return fields;

        void Walk(JsonElement schema, string prefix, int depth)
        {
            if (depth > MaxSchemaDepth || schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var p in props.EnumerateObject())
            {
                var path = prefix.Length == 0 ? p.Name : prefix + "." + p.Name;
                if (!DataPath.IsValid(path))
                {
                    continue;
                }

                if (IsObject(p.Value))
                {
                    Walk(p.Value, path, depth + 1);
                }
                else if (fields.Count < MaxSchemaFields)
                {
                    fields.Add(path);
                }
            }
        }

        static bool IsObject(JsonElement s) =>
            s.ValueKind == JsonValueKind.Object && s.TryGetProperty("properties", out _) && !(s.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "array");
    }

    /// <summary>The template fields no row maps yet (a row counts when its source and destination are filled in).</summary>
    public static IReadOnlyList<string> Unmapped(JsonElement config, IReadOnlyList<string> templateFields)
    {
        var mapped = Rows(config).Select(r => r.To).ToHashSet(StringComparer.Ordinal);
        return [.. templateFields.Where(d => !mapped.Contains(d))];
    }

    private readonly record struct Row(string From, string To, string Transform);

    private static IEnumerable<Row> Rows(JsonElement config)
    {
        if (!config.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var r in rows.EnumerateArray())
        {
            if (r.ValueKind == JsonValueKind.Object && Text(r, "from") is { Length: > 0 } from && Text(r, "to") is { Length: > 0 } to)
            {
                yield return new(from, to, Text(r, "transform") ?? AsIs); // config is schema-validated on publish
            }
        }
    }

    private static string? Text(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

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

    private readonly record struct Transformed(JsonNode? Value, string? Failure);

    private static Transformed Transform(string transform, JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : null;
        switch (transform)
        {
            case AsIs:
                return new(JsonNode.Parse(value.GetRawText()), null);
            case "trim":
                return new(text is null ? JsonNode.Parse(value.GetRawText()) : JsonValue.Create(text.Trim()), null);
            case "uppercase":
                return new(text is null ? JsonNode.Parse(value.GetRawText()) : JsonValue.Create(text.ToUpperInvariant()), null);
            case "number":
                return AsNumber(value, text) is { } n ? new(JsonValue.Create(n), null) : new(null, "the value can't be read as a number (Number).");
            case "round2":
                return AsNumber(value, text) is { } r
                    ? new(JsonValue.Create(Math.Round(r, 2, MidpointRounding.AwayFromZero)), null)
                    : new(null, "the value can't be read as a number (Round to 2 decimals).");
            case "date":
                return text is not null && TryDate(text.Trim(), out var d)
                    ? new(JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), null)
                    : new(null, "the value isn't a date that can be read (Format date).");
            default:
                return new(null, $"unknown transform '{transform}'.");
        }
    }

    /// <summary>A JSON number, or text holding a plain decimal ("4797.28", or "12,5" when there is no dot). Thousands separators aren't guessed.</summary>
    private static decimal? AsNumber(JsonElement value, string? text)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetDecimal(out var n) ? n : null;
        }

        if (text is null || !PlainDecimal().IsMatch(text.Trim()))
        {
            return null;
        }

        var normalised = text.Trim();
        if (!normalised.Contains('.', StringComparison.Ordinal))
        {
            normalised = normalised.Replace(',', '.');
        }

        return decimal.TryParse(normalised, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    internal static decimal? AsNumber(JsonElement value) =>
        AsNumber(value, value.ValueKind == JsonValueKind.String ? value.GetString() : null);

    internal static bool TryDate(string text, out DateOnly date)
    {
        if (DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }

        // ISO date-time: keep the date part as written, ignoring the zone.
        if (text.Length > 10 && text[10] is 'T' or ' ' && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"^[+-]?[0-9]+([.,][0-9]+)?$")]
    private static partial Regex PlainDecimal();
}
