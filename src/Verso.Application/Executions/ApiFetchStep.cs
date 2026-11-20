using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Verso.Rendering.Binding;

namespace Verso.Application.Executions;

/// <summary>The Fetch from API step's query lines (<c>name = data.path</c>).</summary>
public static partial class ApiFetchStep
{
    public const int MaxQueryValues = 20;

    /// <summary>Reads the query setting: one <c>name = data.path</c> per line, blank lines ignored. Returns the first problem found, in words for the person writing the step.</summary>
    public static (IReadOnlyList<(string Name, string Path)> Items, string? Problem) ParseQuery(string? text)
    {
        var items = new List<(string Name, string Path)>();
        var seen = new HashSet<string>(StringComparer.Ordinal); // names are case-sensitive in a query string
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
            {
                return (items, $"'{Short(line)}' needs the form name = data.path.");
            }

            var name = line[..equals].Trim();
            var path = line[(equals + 1)..].Trim();
            if (!NameRegex().IsMatch(name))
            {
                return (items, $"'{Short(name)}' isn't a valid query parameter name. Use letters, digits and _ . - [ ].");
            }

            if (path.Length == 0)
            {
                return (items, $"Query parameter '{name}' needs a data path after the = sign.");
            }

            if (!DataPath.IsValid(path))
            {
                return (items, $"Query parameter '{name}': '{Short(path)}' isn't a valid data path (such as customer.id).");
            }

            if (!seen.Add(name))
            {
                return (items, $"Query parameter '{name}' is listed more than once.");
            }

            if (items.Count == MaxQueryValues)
            {
                return (items, $"At most {MaxQueryValues} query parameters.");
            }

            items.Add((name, path));
        }

        return (items, null);
    }

    private static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;

    [GeneratedRegex(@"^[A-Za-z0-9_.\-\[\]]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();
}

/// <summary>
/// The path of a Fetch from API step: a static path with <c>{{data.path}}</c> pieces. The static part is checked at design time; at run time each piece is
/// replaced by its value, URL-encoded as one piece, so a value can never add a segment, a query or change the host.
/// </summary>
public static partial class ApiPathTemplate
{
    public const int MaxLength = 500;

    /// <summary>Why <paramref name="template"/> can't be used, or null when it can. Mirrors the Worker's own re-check of the resolved path.</summary>
    public static string? Validate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return "The path is required.";
        }

        if (template.Length > MaxLength)
        {
            return $"The path is at most {MaxLength} characters.";
        }

        if (template[0] != '/' || template.StartsWith("//", StringComparison.Ordinal))
        {
            return "The path starts with a single / (such as /v1/latest).";
        }

        var stripped = new StringBuilder(); // the static text with every {{ }} piece replaced by "x", to check the segments
        var i = 0;
        while (i < template.Length)
        {
            if (template.AsSpan(i).StartsWith("{{", StringComparison.Ordinal))
            {
                var close = template.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    return "A {{ in the path has no closing }}.";
                }

                var inner = template[(i + 2)..close].Trim();
                if (!DataPath.IsValid(inner))
                {
                    return $"'{Short(inner)}' inside {{{{ }}}} isn't a valid data path (such as customer.number).";
                }

                stripped.Append('x');
                i = close + 2;
                continue;
            }

            var c = template[i];
            if (c == '}' && template.AsSpan(i).StartsWith("}}", StringComparison.Ordinal))
            {
                return "A }} in the path has no opening {{.";
            }

            if (c is '?' or '#')
            {
                return "The path can't contain ? or #; put query values in the Query setting.";
            }

            if (!IsStaticChar(c))
            {
                return $"The path can't contain '{(char.IsControl(c) ? "a control character" : c.ToString())}'. Use letters, digits and - . _ ~ / (spaces and other characters belong in a value, not in the path).";
            }

            stripped.Append(c);
            i++;
        }

        foreach (var segment in stripped.ToString().Split('/'))
        {
            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(segment);
            }
            catch (UriFormatException)
            {
                decoded = segment;
            }

            if (decoded is "." or "..")
            {
                return "The path can't contain a . or .. segment.";
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the request path for <paramref name="template"/> (already validated at publish) from <paramref name="data"/>. Issues (code, message, field), never
    /// a value: <c>fetch.value_missing</c> for a missing or null value, <c>fetch.value_invalid</c> for a list, a group of fields, an empty text or exactly . or ..
    /// </summary>
    public static (string? Path, IReadOnlyList<object> Issues) Resolve(string template, JsonElement data)
    {
        var issues = new List<object>();
        var path = new StringBuilder();
        var i = 0;
        while (i < template.Length)
        {
            if (!template.AsSpan(i).StartsWith("{{", StringComparison.Ordinal))
            {
                path.Append(template[i++]);
                continue;
            }

            var close = template.IndexOf("}}", i + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                issues.Add(new { code = "fetch.value_invalid", message = "The path has a {{ without a closing }}.", field = "path" });
                break;
            }

            var dataPath = template[(i + 2)..close].Trim();
            i = close + 2;
            if (!DataPath.IsValid(dataPath) || !DataPath.TryResolve(data, dataPath, out var value))
            {
                issues.Add(new { code = "fetch.value_missing", message = $"The path needs a value, but '{Short(dataPath)}' is missing or null in the data.", field = dataPath });
                continue;
            }

            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number when value.TryGetInt64(out var whole) => whole.ToString(CultureInfo.InvariantCulture),
                JsonValueKind.Number when value.TryGetDecimal(out var exact) => exact.ToString(CultureInfo.InvariantCulture),
                JsonValueKind.Number => value.GetDouble().ToString("R", CultureInfo.InvariantCulture),
                _ => null,
            };
            if (text is null)
            {
                issues.Add(new { code = "fetch.value_invalid", message = $"'{Short(dataPath)}' holds {(value.ValueKind == JsonValueKind.Array ? "a list" : "a group of fields")}; only text, a number or true/false can go in the path.", field = dataPath });
                continue;
            }

            if (text.Length == 0 || text is "." or "..")
            {
                issues.Add(new { code = "fetch.value_invalid", message = $"'{Short(dataPath)}' holds a value that can't be used in a path (empty text, . or ..).", field = dataPath });
                continue;
            }

            path.Append(Uri.EscapeDataString(text));
        }

        return (issues.Count == 0 ? path.ToString() : null, issues);
    }

    private static bool IsStaticChar(char c) => StaticChars().IsMatch(c.ToString());

    private static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;

    [GeneratedRegex(@"^[A-Za-z0-9._~!$&'()*+,;=:@%/\-]$", RegexOptions.CultureInvariant)]
    private static partial Regex StaticChars();
}
