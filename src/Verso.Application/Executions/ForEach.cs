using System.Text.Json;
using Verso.Application.Workflows;
using Verso.Rendering.Binding;

namespace Verso.Application.Executions;

/// <summary>
/// The For each step (prototype: "For each": "Repeats the next steps for every item in a list"). It reads a list from the run's data and
/// runs the steps after its <c>each</c> output once per item; an empty (or missing) list leaves by <c>empty</c> instead. The steps of the
/// loop are everything reachable from <c>each</c>; one step of them may connect back into the For each to end an item (a path that simply
/// ends does the same). There is no output for "all items done": when the last item is finished the run ends as Succeeded.
/// <para>
/// Mode <c>each</c> keeps the whole record and adds the current item to it under the item name (default <c>item</c>, so
/// <c>{{item.description}}</c> works in later steps). Mode <c>split</c> makes each item its own record for the rest of the workflow: an
/// object item is the record, anything else becomes <c>{ "&lt;item name&gt;": value }</c>. A list field holding a single value (not a list)
/// counts as a list of one; a missing, null or blank field is an empty list. The data after the loop is the data before it.
/// </para>
/// </summary>
public static class ForEach
{
    public const string Each = "each";
    public const string Empty = "empty";

    public const string ModeEach = "each";
    public const string ModeSplit = "split";

    public const string DefaultItemName = "item";
    public const string DefaultEachLabel = "For each item";
    public const string DefaultEmptyLabel = "No items";

    /// <summary>A run takes at most five minutes (plan §6.3); this keeps a loop of PDFs inside that, and the step history readable.</summary>
    public const int MaxItems = 200;

    public const string ItemNamePattern = "^[A-Za-z_][A-Za-z0-9_]{0,39}$";

    public static readonly IReadOnlyDictionary<string, string> Modes = new Dictionary<string, string>
    {
        [ModeEach] = "each: run the next steps once per item, keeping the whole record",
        [ModeSplit] = "split: turn each item into its own record for the rest of the workflow",
    };

    /// <summary>The step's outputs: <c>each</c> (required) and <c>empty</c> (optional), named by the settings.</summary>
    public static IReadOnlyList<PortDto> Ports(JsonElement config) =>
    [
        new PortDto(Each, IfElse.SettingText(config, "eachLabel") is { Length: > 0 } e ? e : DefaultEachLabel, true),
        new PortDto(Empty, IfElse.SettingText(config, "emptyLabel") is { Length: > 0 } m ? m : DefaultEmptyLabel, false),
    ];

    public static string ItemName(JsonElement config) =>
        IfElse.SettingText(config, "item") is { Length: > 0 } name ? name : DefaultItemName;

    public static bool IsSplit(JsonElement config) => IfElse.SettingText(config, "mode") == ModeSplit;

    /// <summary>The items of the list the step repeats over (see the class remarks for what counts as a list).</summary>
    public static IReadOnlyList<JsonElement> Items(JsonElement config, JsonElement data)
    {
        if (!DataPath.TryResolve(data, IfElse.SettingText(config, "field"), out var value))
        {
            return [];
        }

        return value.ValueKind switch
        {
            JsonValueKind.Array => [.. value.EnumerateArray()],
            JsonValueKind.String when string.IsNullOrWhiteSpace(value.GetString()) => [],
            _ => [value],
        };
    }

    /// <summary>The data one item runs with. The result owns its memory (it outlives the data it was built from).</summary>
    public static JsonElement IterationData(JsonElement config, JsonElement data, JsonElement item)
    {
        var name = ItemName(config);
        if (IsSplit(config) && item.ValueKind == JsonValueKind.Object)
        {
            return item.Clone();
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (!IsSplit(config) && data.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in data.EnumerateObject().Where(p => p.Name != name))
                {
                    property.WriteTo(writer);
                }
            }

            writer.WritePropertyName(name);
            item.WriteTo(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
}
