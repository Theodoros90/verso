using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Verso.Application.Connections;
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

    /// <summary>
    /// False when <paramref name="text"/> holds a lone surrogate: URL-encoding would silently turn it into U+FFFD and send a different value than the data holds.
    /// Used for path pieces and (by the engine) for query values.
    /// </summary>
    public static bool IsValidText(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The run-time values of the query lines, read from <paramref name="data"/> with the same dotted paths and scalar rules as the path pieces: text, numbers
    /// (invariant culture, formatted as in the path) and true/false. The pairs are raw, in the order of the lines (the sender encodes them); an empty text,
    /// "." or ".." is an ordinary value here. Issues (code, message, field), never a value: <c>fetch.value_missing</c> for a missing or null value,
    /// <c>fetch.value_invalid</c> for a list, a group of fields, or text that can't be sent (a lone surrogate). Pairs hold only the lines without an issue.
    /// </summary>
    public static (IReadOnlyList<KeyValuePair<string, string>> Pairs, IReadOnlyList<object> Issues) ResolveQuery(IReadOnlyList<(string Name, string Path)> items, JsonElement data)
    {
        ArgumentNullException.ThrowIfNull(items);
        var pairs = new List<KeyValuePair<string, string>>(items.Count);
        var issues = new List<object>();
        foreach (var (name, path) in items)
        {
            if (!DataPath.TryResolve(data, path, out var value))
            {
                issues.Add(new { code = "fetch.value_missing", message = $"Query value '{name}' has no value: '{Short(path)}' is missing or null in the data (an empty text is a value).", field = path });
                continue;
            }

            var text = ScalarText(value);
            if (text is null || !IsValidText(text))
            {
                var kind = value.ValueKind switch { JsonValueKind.Array => "a list", JsonValueKind.Object => "a group of fields", _ => "text that can't be sent" };
                issues.Add(new { code = "fetch.value_invalid", message = $"Query value '{name}': '{Short(path)}' holds {kind}; only text, a number or true/false can be sent.", field = path });
                continue;
            }

            pairs.Add(new KeyValuePair<string, string>(name, text));
        }

        return (pairs, issues);
    }

    /// <summary>
    /// The text of a scalar data value as it is sent (path piece or query value): text as is, true/false, numbers in the invariant culture (whole numbers,
    /// then exact decimals, then the round-trip form of a double). Null for a list, a group of fields, or text System.Text.Json can't read (a lone surrogate escape).
    /// </summary>
    internal static string? ScalarText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => ReadString(value),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number when value.TryGetInt64(out var whole) => whole.ToString(CultureInfo.InvariantCulture),
        JsonValueKind.Number when value.TryGetDecimal(out var exact) => exact.ToString(CultureInfo.InvariantCulture),
        JsonValueKind.Number => value.GetDouble().ToString("R", CultureInfo.InvariantCulture),
        _ => null,
    };

    private static string? ReadString(JsonElement value)
    {
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
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

            // A static % stays allowed (percent-encoded text is legal in a path, and a piece may complete it); Resolve re-checks the finished segments for . and ..
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

            var text = ApiFetchStep.ScalarText(value); // null (invalid) for a list, a group, or text System.Text.Json can't read, such as a lone surrogate escape
            if (text is null)
            {
                issues.Add(new { code = "fetch.value_invalid", message = $"'{Short(dataPath)}' holds {value.ValueKind switch { JsonValueKind.Array => "a list", JsonValueKind.String => "text that can't be sent", _ => "a group of fields" }}; only text, a number or true/false can go in the path.", field = dataPath });
                continue;
            }

            if (text.Length == 0 || text is "." or ".." || !ApiFetchStep.IsValidText(text))
            {
                issues.Add(new { code = "fetch.value_invalid", message = $"'{Short(dataPath)}' holds a value that can't be used in a path (empty text, . or .., or characters that can't be sent).", field = dataPath });
                continue;
            }

            path.Append(Uri.EscapeDataString(text));
        }

        if (issues.Count > 0)
        {
            return (null, issues);
        }

        // Same per-segment check as the service (one unescape): a static "%2" followed by a value "e" must not become a ".." segment. Pieces are encoded as one
        // piece, so the resolved path has exactly as many segments as the template and they line up.
        var resolved = path.ToString();
        var templateSegments = template.Split('/');
        var resolvedSegments = resolved.Split('/');
        for (var s = 0; s < resolvedSegments.Length && s < templateSegments.Length; s++)
        {
            if (Uri.UnescapeDataString(resolvedSegments[s]) is "." or "..")
            {
                var names = PieceRegex().Matches(templateSegments[s]).Select(m => m.Groups[1].Value.Trim()).ToList();
                issues.Add(new
                {
                    code = "fetch.value_invalid",
                    message = $"The path would contain a . or .. segment once {(names.Count == 0 ? "the values are" : $"'{Short(names[0])}' is")} filled in.",
                    field = names.Count == 0 ? "path" : names[0],
                });
            }
        }

        // The service refuses a resolved path longer than ApiUrls.MaxUrlLength (api.bad_request, a technical failure). Percent-encoding makes values grow, so a long
        // (especially non-ASCII) value is the data's fault: say so here, naming the data paths of the pieces, never a value, and send nothing.
        if (issues.Count == 0 && resolved.Length > ApiUrls.MaxUrlLength)
        {
            var names = PieceRegex().Matches(template).Select(m => $"'{Short(m.Groups[1].Value.Trim())}'").ToList();
            issues.Add(new
            {
                code = "fetch.value_invalid",
                message = $"The path would be longer than {ApiUrls.MaxUrlLength} characters once {(names.Count == 0 ? "the values are" : string.Join(", ", names) + (names.Count == 1 ? " is" : " are"))} filled in (each value is percent-encoded, so non-ASCII characters take 6 to 12 characters each).",
                field = names.Count == 0 ? "path" : PieceRegex().Match(template).Groups[1].Value.Trim(),
            });
        }

        return (issues.Count == 0 ? resolved : null, issues);
    }

    [GeneratedRegex(@"\{\{(.*?)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PieceRegex();

    private static bool IsStaticChar(char c) => StaticChars().IsMatch(c.ToString());

    private static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;

    [GeneratedRegex(@"^[A-Za-z0-9._~!$&'()*+,;=:@%/\-]$", RegexOptions.CultureInvariant)]
    private static partial Regex StaticChars();
}
