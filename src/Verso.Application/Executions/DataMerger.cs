using System.Text.Json;
using System.Text.Json.Nodes;
using Verso.Rendering.Binding;

namespace Verso.Application.Executions;

/// <summary>A record that couldn't be merged. Messages name the record's position; they never echo values.</summary>
public sealed record MergeIssue(string Code, string Message, string Field);

/// <summary>The data after merging (or the issues), with how many records matched, found no match, and were dropped.</summary>
public sealed record MergeResult(JsonElement? Data, IReadOnlyList<MergeIssue> Issues, int Matched, int Unmatched, int Dropped);

/// <summary>
/// The Merge data step (prototype: "Merge data": "Joins two data sources on a key"). The prototype has a second input port for the
/// lookup source. Verso's engine runs one path with one record, so the lookup source is a table of records typed into the step
/// (reference data such as customers or products, synthetic in tests). The primary source is the run's data: one record, or the
/// list at <c>source</c> (each entry is enriched). A record matches the lookup row whose <c>lookupKey</c> equals the record's
/// <c>key</c> (compared as text: trimmed, case-sensitive; numbers by their written form). The matched row's fields are written
/// into the record (under <c>into</c>, or at its top level); objects merge, and values the record already has are kept unless
/// <c>overwrite</c> is on. Without a match the strategy decides: keep the record unchanged, drop it (lists only; for a single
/// record that leaves nothing to continue with, so it fails) or stop the run (the step fails and the run ends Rejected).
/// </summary>
public static class DataMerger
{
    public const int MaxLookupRows = 2000;
    public const int MaxLookupText = 200_000;

    public const string Keep = "keep";
    public const string Drop = "drop";
    public const string Stop = "stop";

    public static readonly IReadOnlyDictionary<string, string> Strategies = new Dictionary<string, string>
    {
        [Keep] = "Keep all primary records",
        [Drop] = "Drop unmatched records",
        [Stop] = "Stop the run",
    };

    public static MergeResult Apply(JsonElement config, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return Failed("merge.not_record", "The data isn't a JSON object, so there is nothing to merge into.", "");
        }

        var table = Table(config, out var tableProblem);
        if (table is null)
        {
            return Failed("merge.lookup_invalid", tableProblem!, "lookup");
        }

        var key = Setting(config, "key");
        var source = Setting(config, "source");
        var into = Setting(config, "into");
        var strategy = Setting(config, "strategy");
        var overwrite = config.TryGetProperty("overwrite", out var o) && o.ValueKind == JsonValueKind.True;
        var lookupKey = Setting(config, "lookupKey");
        var rows = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var row in table)
        {
            if (KeyText(Get(row, lookupKey)) is { } k)
            {
                rows.TryAdd(k, row); // duplicates are refused when the workflow is validated; the first row wins
            }
        }

        var root = (JsonObject)JsonNode.Parse(data.GetRawText())!;
        var issues = new List<MergeIssue>();
        int matched = 0, unmatched = 0, dropped = 0;

        if (source.Length == 0)
        {
            if (Enrich(root, key, into, overwrite, rows))
            {
                matched++;
            }
            else
            {
                unmatched++;
                if (strategy != Keep)
                {
                    issues.Add(new("merge.unmatched", strategy == Drop
                        ? "The record has no match in the lookup table and would be dropped, leaving nothing to continue with."
                        : "The record has no match in the lookup table.", ""));
                }
            }
        }
        else if (Get(root, source) is not JsonArray list)
        {
            issues.Add(new("merge.no_list", $"'{source}' isn't a list in the data.", "source"));
        }
        else
        {
            var drop = new List<int>();
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] is not JsonObject record)
                {
                    issues.Add(new("merge.not_record", $"Entry {i + 1} of '{source}' isn't a JSON object.", $"source[{i}]"));
                    continue;
                }

                if (Enrich(record, key, into, overwrite, rows))
                {
                    matched++;
                    continue;
                }

                unmatched++;
                if (strategy == Drop)
                {
                    drop.Add(i);
                }
                else if (strategy == Stop)
                {
                    issues.Add(new("merge.unmatched", $"Entry {i + 1} of '{source}' has no match in the lookup table.", $"source[{i}]"));
                }
            }

            dropped = drop.Count;
            for (var d = drop.Count - 1; d >= 0 && issues.Count == 0; d--)
            {
                list.RemoveAt(drop[d]);
            }
        }

        return issues.Count > 0
            ? new(null, issues, matched, unmatched, dropped)
            : new(JsonSerializer.Deserialize<JsonElement>(root.ToJsonString()), [], matched, unmatched, dropped);
    }

    /// <summary>Problems with the settings that a workflow can't be published with. Empty when complete.</summary>
    public static IReadOnlyList<string> Incomplete(JsonElement config)
    {
        var problems = new List<string>();
        if (!DataPath.IsValid(Setting(config, "key")))
        {
            problems.Add("Match on must be a field path such as customer.id.");
        }

        var lookupKey = Setting(config, "lookupKey");
        if (!DataPath.IsValid(lookupKey))
        {
            problems.Add("Lookup field must be a field path such as id.");
        }

        if (Setting(config, "source") is { Length: > 0 } s && !DataPath.IsValid(s))
        {
            problems.Add("The list to enrich must be a field path such as lines.");
        }

        if (Setting(config, "into") is { Length: > 0 } i && !DataPath.IsValid(i))
        {
            problems.Add("Write matches into must be a field path such as customer.");
        }

        if (!Strategies.ContainsKey(Setting(config, "strategy")))
        {
            problems.Add("Choose what happens when there's no match.");
        }

        var table = Table(config, out var tableProblem);
        if (table is null)
        {
            problems.Add(tableProblem!);
        }
        else if (DataPath.IsValid(lookupKey))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var number = 0;
            foreach (var row in table)
            {
                number++;
                if (KeyText(Get(row, lookupKey)) is not { } k)
                {
                    problems.Add($"Lookup row {number} has no '{lookupKey}' value.");
                }
                else if (!seen.Add(k))
                {
                    problems.Add($"Lookup row {number} repeats the key of an earlier row. Each key may appear once.");
                }
            }
        }

        return problems;
    }

    private static MergeResult Failed(string code, string message, string field) => new(null, [new(code, message, field)], 0, 0, 0);

    private static string Setting(JsonElement config, string name) =>
        config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

    /// <summary>The lookup table (a JSON array of objects), or null with the reason.</summary>
    private static List<JsonObject>? Table(JsonElement config, out string? problem)
    {
        problem = null;
        var text = config.TryGetProperty("lookup", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        if (text.Trim().Length == 0)
        {
            problem = "Add the lookup table: a JSON list of records.";
            return null;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            problem = "The lookup table isn't valid JSON.";
            return null;
        }

        if (node is not JsonArray array || array.Count == 0)
        {
            problem = "The lookup table must be a JSON list with at least one record.";
            return null;
        }

        if (array.Count > MaxLookupRows)
        {
            problem = $"The lookup table has more than {MaxLookupRows} records.";
            return null;
        }

        var rows = new List<JsonObject>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject row)
            {
                problem = "Every entry of the lookup table must be a JSON object.";
                return null;
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Writes the matching row into the record. False when the record has no key or no row matches.</summary>
    private static bool Enrich(JsonObject record, string key, string into, bool overwrite, Dictionary<string, JsonObject> rows)
    {
        if (KeyText(Get(record, key)) is not { } k || !rows.TryGetValue(k, out var row))
        {
            return false;
        }

        var target = record;
        if (into.Length > 0)
        {
            foreach (var segment in into.Split('.'))
            {
                if (target[segment] is not JsonObject child)
                {
                    child = [];
                    target[segment] = child; // a scalar or list in the way is replaced
                }

                target = child;
            }
        }

        MergeInto(target, row, overwrite);
        return true;
    }

    private static void MergeInto(JsonObject target, JsonObject row, bool overwrite)
    {
        foreach (var (name, value) in row)
        {
            if (value is JsonObject incoming && target[name] is JsonObject existing)
            {
                MergeInto(existing, incoming, overwrite);
            }
            else if (overwrite || target[name] is null)
            {
                target[name] = value?.DeepClone();
            }
        }
    }

    /// <summary>The text two values are compared as: strings trimmed (empty is no key), numbers and booleans as written. Objects, lists and null have none.</summary>
    private static string? KeyText(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<string>(out var s))
        {
            s = s.Trim();
            return s.Length == 0 ? null : s;
        }

        return value.ToJsonString();
    }

    private static JsonNode? Get(JsonNode node, string path)
    {
        var current = node;
        foreach (var segment in path.Split('.'))
        {
            if (current is not JsonObject o || o[segment] is not { } next)
            {
                return null;
            }

            current = next;
        }

        return current;
    }
}
