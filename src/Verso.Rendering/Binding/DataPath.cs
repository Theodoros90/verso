using System.Text.Json;
using System.Text.RegularExpressions;

namespace Verso.Rendering.Binding;

/// <summary>Dotted paths into the data object, e.g. <c>invoice.number</c>. No indexing, no expressions.</summary>
public static partial class DataPath
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$")]
    private static partial Regex Pattern();

    public static bool IsValid(string path) => path.Length <= 200 && Pattern().IsMatch(path);

    /// <summary>Finds the value at <paramref name="path"/>. JSON <c>null</c> counts as not found.</summary>
    public static bool TryResolve(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        foreach (var segment in path.Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            {
                value = default;
                return false;
            }
        }

        return value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }
}
