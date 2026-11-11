using System.Text.Json;
using System.Text.RegularExpressions;

namespace Verso.Application.Ingestion;

/// <summary>An approved location plus an optional subfolder below it (with '/' separators; empty for the location itself).</summary>
public sealed record FolderRef(string Location, string Subfolder)
{
    public override string ToString() => Subfolder.Length == 0 ? Location : $"{Location}/{Subfolder}";
}

/// <summary>The settings of a <c>folder-input</c> node, read from its config.</summary>
public sealed record FolderInputSettings(
    FolderRef Source, string Pattern, bool IncludeSubfolders, string Interval, FolderRef Processed, FolderRef Failed)
{
    public TimeSpan IntervalTime => FolderPaths.Intervals.TryGetValue(Interval, out var t) ? t : TimeSpan.FromMinutes(1);

    /// <summary>Null when a required setting is missing (the graph validator reports it).</summary>
    public static FolderInputSettings? Parse(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string Text(string name) => config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
        var include = config.TryGetProperty("includeSubfolders", out var b) && b.ValueKind == JsonValueKind.True;
        var settings = new FolderInputSettings(
            new FolderRef(Text("sourceLocation"), FolderPaths.NormalizeSubfolder(Text("sourceSubfolder"))),
            Text("pattern"),
            include,
            Text("interval"),
            new FolderRef(Text("processedLocation"), FolderPaths.NormalizeSubfolder(Text("processedSubfolder"))),
            new FolderRef(Text("failedLocation"), FolderPaths.NormalizeSubfolder(Text("failedSubfolder"))));
        return settings.Source.Location.Length == 0 || settings.Pattern.Length == 0
               || settings.Processed.Location.Length == 0 || settings.Failed.Location.Length == 0
            ? null
            : settings;
    }
}

/// <summary>
/// Rules for location keys, subfolders and file patterns. Pure, so the API (design time) and the Worker (run time)
/// apply exactly the same checks. The Worker additionally resolves the result on disk and refuses anything that
/// leaves the approved folder (links, junctions).
/// </summary>
public static partial class FolderPaths
{
    public const int MaxSubfolderLength = 200;
    public const int MaxSubfolderDepth = 8;

    /// <summary>Verso's working folder inside a source folder (files being processed). Never scanned.</summary>
    public const string InFlightFolder = ".verso-inflight";

    public const string LocationKeyPattern = "^[a-z][a-z0-9-]{1,63}$";

    /// <summary>
    /// Segments of letters, digits, space, '.', '_' and '-', not starting or ending with '.' or space (so no '.', '..',
    /// hidden folders, or names Windows would silently trim), separated by '/'.
    /// </summary>
    public const string SubfolderPattern = @"^([A-Za-z0-9_-]([A-Za-z0-9 ._-]*[A-Za-z0-9_-])?)(/[A-Za-z0-9_-]([A-Za-z0-9 ._-]*[A-Za-z0-9_-])?)*$";

    /// <summary>A file name pattern with '*' and '?' wildcards, no path parts, not starting with '.'.</summary>
    public const string FilePatternPattern = @"^[A-Za-z0-9_*?-][A-Za-z0-9._*?-]*$";

    public static readonly IReadOnlyDictionary<string, TimeSpan> Intervals = new Dictionary<string, TimeSpan>
    {
        ["every-minute"] = TimeSpan.FromMinutes(1),
        ["every-5-minutes"] = TimeSpan.FromMinutes(5),
        ["every-15-minutes"] = TimeSpan.FromMinutes(15),
        ["hourly"] = TimeSpan.FromHours(1),
    };

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Suffixes writers use while a file is incomplete; such files are never picked up.</summary>
    private static readonly string[] TemporarySuffixes = [".tmp", ".temp", ".part", ".partial", ".filepart", ".crdownload", ".lock", ".download"];

    [GeneratedRegex(LocationKeyPattern)]
    private static partial Regex LocationKeyRegex();

    [GeneratedRegex(SubfolderPattern)]
    private static partial Regex SubfolderRegex();

    [GeneratedRegex(FilePatternPattern)]
    private static partial Regex FilePatternRegex();

    public static bool IsValidLocationKey(string key) => LocationKeyRegex().IsMatch(key);

    /// <summary>Trims and strips leading/trailing '/'. Backslashes are kept, so they fail validation (use '/').</summary>
    public static string NormalizeSubfolder(string? subfolder) => (subfolder ?? "").Trim().Trim('/');

    /// <summary>Null when valid, else a message. Empty means the location itself.</summary>
    public static string? SubfolderProblem(string subfolder)
    {
        if (subfolder.Length == 0)
        {
            return null;
        }

        if (subfolder.Length > MaxSubfolderLength)
        {
            return $"At most {MaxSubfolderLength} characters.";
        }

        if (!SubfolderRegex().IsMatch(subfolder))
        {
            return "A relative path such as invoices/incoming: letters, digits, spaces, '.', '_' and '-', separated by '/'. No '..', no leading '.', no drive or server names.";
        }

        var segments = subfolder.Split('/');
        if (segments.Length > MaxSubfolderDepth)
        {
            return $"At most {MaxSubfolderDepth} levels.";
        }

        if (segments.FirstOrDefault(s => ReservedNames.Contains(s.Split('.')[0])) is { } reserved)
        {
            return $"'{reserved}' is a reserved device name.";
        }

        return null;
    }

    public static bool IsValidFilePattern(string pattern) => pattern.Length is > 0 and <= 100 && FilePatternRegex().IsMatch(pattern);

    /// <summary>
    /// Handoff convention: files still being written are skipped. Writers must write under a temporary name (one
    /// starting with '.' or '~', or ending with .tmp, .part, …) and rename to the final name when complete.
    /// </summary>
    public static bool IsTemporaryName(string fileName) =>
        fileName.StartsWith('.') || fileName.StartsWith('~')
        || TemporarySuffixes.Any(s => fileName.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether files moved into <paramref name="destination"/> would be found again by a scan of <paramref name="source"/>
    /// (same location, and the destination is the scanned folder or, with subfolders included, below it).
    /// </summary>
    public static bool WouldRescan(FolderRef source, FolderRef destination, bool includeSubfolders)
    {
        if (!string.Equals(source.Location, destination.Location, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(source.Subfolder, destination.Subfolder, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return includeSubfolders
               && (source.Subfolder.Length == 0 || destination.Subfolder.StartsWith(source.Subfolder + "/", StringComparison.OrdinalIgnoreCase));
    }
}
