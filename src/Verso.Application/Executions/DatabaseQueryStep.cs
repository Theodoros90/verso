using System.Text.RegularExpressions;

namespace Verso.Application.Executions;

/// <summary>The Query database step's settings that need more than a schema: the parameter lines (<c>name = data.path</c>).</summary>
public static partial class DatabaseQueryStep
{
    public const int MaxParameters = 20;
    public const string ResultNamePattern = "^[A-Za-z_][A-Za-z0-9_]{0,29}$";

    /// <summary>
    /// Reads the parameters setting: one <c>name = data.path</c> per line, blank lines ignored. <c>name</c> is the placeholder in the SQL
    /// without its <c>@</c>. Returns the first problem found, in words for the person writing the step.
    /// </summary>
    public static (IReadOnlyList<(string Name, string Path)> Items, string? Problem) ParseParameters(string? text)
    {
        var items = new List<(string Name, string Path)>();
        // Names differing only in case are refused as duplicates: SQL Server (and Oracle) would see the same bind variable twice. Placeholder matching in
        // SqlGuard stays case-sensitive, so @Id in the SQL still needs a line called Id.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                return (items, $"'{Short(name)}' isn't a valid parameter name. Use letters, digits and underscores, without the @.");
            }

            if (path.Length == 0)
            {
                return (items, $"Parameter '{name}' needs a data path after the = sign.");
            }

            if (!PathRegex().IsMatch(path))
            {
                return (items, $"Parameter '{name}': '{Short(path)}' isn't a valid data path (such as customer.id).");
            }

            if (!seen.Add(name))
            {
                return (items, $"Parameter '{name}' is listed more than once.");
            }

            if (items.Count == MaxParameters)
            {
                return (items, $"At most {MaxParameters} parameters.");
            }

            items.Add((name, path));
        }

        return (items, null);
    }

    private static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex PathRegex();
}
