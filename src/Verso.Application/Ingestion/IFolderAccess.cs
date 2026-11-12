namespace Verso.Application.Ingestion;

/// <summary>An approved location as configured on this Worker (no path: that never leaves the file-system adapter).</summary>
public sealed record ConfiguredFolderLocation(string Key, string Name, bool Input, bool Output, string? Problem);

/// <summary>A location an administrator manages in Verso, with its path (read from the database by the Worker).</summary>
public sealed record FolderPathEntry(string Key, string Name, string Path, bool Input, bool Output);

/// <summary>A file found by a scan, ready to claim (complete by the handoff convention).</summary>
public sealed record FoundFile(string RelativePath, string FileName, long SizeBytes, DateTimeOffset LastWriteAt);

/// <summary>A file read from the in-flight folder. <see cref="Content"/> is null when it was larger than allowed.</summary>
public sealed record InFlightContent(string Sha256, long SizeBytes, byte[]? Content);

public enum FolderAccessMode
{
    /// <summary>A source folder: list, read, and move files out (into Verso's in-flight folder).</summary>
    Input,

    /// <summary>A destination: create files (and the subfolder, if missing).</summary>
    Output,
}

public sealed record FolderCheck(string Name, bool Ok, string Message);

/// <summary>Stable codes of folder problems. Messages that go with them never contain server paths.</summary>
public static class FolderErrors
{
    public const string UnknownLocation = "folder.unknown_location";
    public const string Misconfigured = "folder.misconfigured";
    public const string NotFound = "folder.not_found";
    public const string AccessDenied = "folder.access_denied";
    public const string Unavailable = "folder.unavailable";
    public const string OutsideLocation = "folder.outside_location";
    public const string FileMissing = "file.missing";
}

/// <summary>A folder operation failed. <see cref="Exception.Message"/> is sanitized (no paths).</summary>
public sealed class FolderException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>
/// The Worker's access to approved folders (implemented on the local file system / network shares in
/// Infrastructure). Every method resolves the location key against this Worker's configuration and refuses paths
/// that leave the approved folder. It runs as the Worker's service identity; there are no stored credentials.
/// </summary>
public interface IFolderAccess
{
    /// <summary>Locations from the Worker's configuration.</summary>
    IReadOnlyList<ConfiguredFolderLocation> Locations { get; }

    /// <summary>
    /// Replaces the paths workflow authors typed into steps (see FolderPathRegistry). They are resolved like locations
    /// but are not part of <see cref="Locations"/>, which only lists named locations.
    /// </summary>
    void SetTypedPaths(IReadOnlyCollection<FolderPathEntry> paths);

    /// <summary>The account this process accesses folders as (e.g. <c>CORP\svc-verso-worker$</c>).</summary>
    string Identity { get; }

    /// <summary>
    /// Files matching <paramref name="pattern"/> that follow the handoff convention: not a temporary name, unchanged
    /// for at least <paramref name="settle"/>, and not open for writing. Skips Verso's in-flight folder and hidden or
    /// linked folders. Oldest first, at most <paramref name="max"/>.
    /// </summary>
    /// <exception cref="FolderException">The folder is unknown, unavailable or access is denied.</exception>
    IReadOnlyList<FoundFile> List(FolderRef source, string pattern, bool includeSubfolders, TimeSpan settle, DateTimeOffset now, int max);

    /// <summary>Moves the file into the in-flight folder as <paramref name="id"/>. False if it vanished or is in use.</summary>
    bool TryMoveToInFlight(FolderRef source, string relativePath, Guid id);

    bool InFlightExists(FolderRef source, Guid id);

    bool SourceExists(FolderRef source, string relativePath);

    Task<InFlightContent> ReadInFlightAsync(FolderRef source, Guid id, long maxBytes, CancellationToken ct);

    /// <summary>
    /// Moves the in-flight file to <paramref name="destination"/> as <paramref name="fileName"/> (or, when that name
    /// is taken by another file, with the id as a suffix), creating the subfolder if needed, and returns the name
    /// used. Idempotent: if the move already happened (the in-flight file is gone and a destination file with
    /// <paramref name="sha256"/> exists) it returns that name.
    /// </summary>
    /// <exception cref="FolderException">Destination unknown/unavailable/denied, or the file is gone (<see cref="FolderErrors.FileMissing"/>).</exception>
    string Deliver(FolderRef source, Guid id, FolderRef destination, string fileName, string? sha256);

    /// <summary>Writes a small text file next to a delivered file (best effort; throws on failure).</summary>
    void WriteSidecar(FolderRef destination, string fileName, string content);

    /// <summary>Checks a folder as this process's identity. Stops at the first failed check.</summary>
    IReadOnlyList<FolderCheck> Test(FolderRef target, FolderAccessMode mode);

    /// <summary>
    /// True when <paramref name="inner"/> resolves to <paramref name="outer"/> or a folder below it (by real path,
    /// so two keys for the same share are caught). False when either can't be resolved.
    /// </summary>
    bool IsSameOrInside(FolderRef inner, FolderRef outer);
}
