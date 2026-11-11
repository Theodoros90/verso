using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using System.IO.Enumeration;
using Verso.Application.Ingestion;

namespace Verso.Infrastructure.Ingestion;

/// <summary>One approved location in the Worker's configuration (<c>FolderInput:Locations:&lt;key&gt;</c>).</summary>
public sealed class FolderLocationOptions
{
    public string Name { get; set; } = "";

    /// <summary>
    /// Absolute path as the Worker's OS sees it: <c>D:\Verso\inbound</c> or <c>\\fileserver\share\inbound</c> on
    /// Windows, <c>/mnt/verso/inbound</c> on Linux. Reached as the Worker's service identity; never with stored passwords.
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>Files may be picked up here.</summary>
    public bool Input { get; set; }

    /// <summary>Processed or failed files may be moved here.</summary>
    public bool Output { get; set; }
}

public sealed class FolderAccessOptions
{
    public Dictionary<string, FolderLocationOptions> Locations { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// <see cref="IFolderAccess"/> on the local file system or SMB/NFS shares mounted for the Worker. Every path is the
/// configured root of an approved location plus a validated relative subfolder; the result must stay inside the
/// root, and linked folders (symbolic links, junctions) below the root are refused, so nothing reaches outside the
/// approved folder. Error messages name the location key, never a path.
/// </summary>
public sealed class LocalFolderAccess : IFolderAccess
{
    private const string ProbePrefix = ".verso-access-test-";

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly Dictionary<string, (FolderLocationOptions Options, string? Root, string? Problem)> _locations = new(StringComparer.Ordinal);

    public LocalFolderAccess(IOptions<FolderAccessOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var (key, location) in options.Value.Locations)
        {
            var (root, problem) = Validate(key, location);
            _locations[key] = (location, root, problem);
        }

        Locations = [.. _locations.Select(l => new ConfiguredFolderLocation(
            l.Key, string.IsNullOrWhiteSpace(l.Value.Options.Name) ? l.Key : l.Value.Options.Name.Trim(), l.Value.Options.Input, l.Value.Options.Output, l.Value.Problem))
            .Where(l => FolderPaths.IsValidLocationKey(l.Key))];
    }

    public IReadOnlyList<ConfiguredFolderLocation> Locations { get; }

    public string Identity { get; } = $"{Environment.UserDomainName}\\{Environment.UserName}";

    /// <summary>Checks a configured root: absolute, normalized, not a file-system root, and for a valid key and purpose.</summary>
    internal static (string? Root, string? Problem) Validate(string key, FolderLocationOptions location)
    {
        if (!FolderPaths.IsValidLocationKey(key))
        {
            return (null, "Invalid key: 2–64 lower-case letters, digits and '-', starting with a letter.");
        }

        if (!location.Input && !location.Output)
        {
            return (null, "Neither Input nor Output is allowed.");
        }

        var path = location.Path?.Trim() ?? "";
        if (path.Length == 0 || !Path.IsPathFullyQualified(path))
        {
            return (null, "The path must be absolute (a local folder or a network share as the Worker's OS sees it).");
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, "The path isn't valid on this server.");
        }

        if (!string.Equals(full, Path.TrimEndingDirectorySeparator(path), PathComparison))
        {
            return (null, "The path must be normalized (no '.' or '..' parts).");
        }

        if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? ""), full, PathComparison))
        {
            return (null, "A drive, share or file-system root can't be approved; use a dedicated folder.");
        }

        return (full, null);
    }

    public IReadOnlyList<FoundFile> List(FolderRef source, string pattern, bool includeSubfolders, TimeSpan settle, DateTimeOffset now, int max)
    {
        ArgumentNullException.ThrowIfNull(source);
        var folder = Resolve(source, FolderAccessMode.Input);
        if (!FolderPaths.IsValidFilePattern(pattern))
        {
            throw new FolderException(FolderErrors.Misconfigured, "The file pattern isn't valid.");
        }

        EnsureReadable(source, folder);
        var cutoff = now - settle;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubfolders,
            IgnoreInaccessible = true, // the folder itself was checked above; an unreadable subfolder is skipped
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            ReturnSpecialDirectories = false,
        };
        try
        {
            var files = new FileSystemEnumerable<FoundFile>(folder, (ref FileSystemEntry e) =>
                    new FoundFile(
                        Path.GetRelativePath(folder, e.ToFullPath()).Replace(Path.DirectorySeparatorChar, '/'),
                        e.FileName.ToString(), e.Length, e.LastWriteTimeUtc), options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) =>
                    !e.IsDirectory && !e.IsHidden
                    && !FolderPaths.IsTemporaryName(e.FileName.ToString())
                    && FileSystemName.MatchesSimpleExpression(pattern, e.FileName, ignoreCase: true)
                    && e.LastWriteTimeUtc <= cutoff,
                ShouldRecursePredicate = (ref FileSystemEntry e) => !e.IsHidden && !e.FileName.StartsWith(".") && (e.Attributes & FileAttributes.ReparsePoint) == 0,
            };
            return [.. files.OrderBy(f => f.LastWriteAt).ThenBy(f => f.RelativePath, StringComparer.Ordinal).Take(max)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Translate(ex, source, "listing");
        }
    }

    public bool TryMoveToInFlight(FolderRef source, string relativePath, Guid id)
    {
        ArgumentNullException.ThrowIfNull(source);
        var folder = Resolve(source, FolderAccessMode.Input);
        var file = Inside(folder, relativePath);
        try
        {
            if (!File.Exists(file))
            {
                return false;
            }

            Directory.CreateDirectory(InFlightFolder(folder));

            // Still open for writing? (Windows: a writer's handle blocks this; elsewhere the rename convention covers it.)
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            {
            }

            File.Move(file, InFlightPath(folder, id), overwrite: false); // atomic rename within the folder's volume
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false; // taken or removed meanwhile
        }
        catch (UnauthorizedAccessException ex)
        {
            throw Translate(ex, source, "moving a file into Verso's working folder");
        }
        catch (IOException)
        {
            return false; // in use (still being written), or already moved by someone else
        }
    }

    public bool InFlightExists(FolderRef source, Guid id) => File.Exists(InFlightPath(Resolve(source, FolderAccessMode.Input), id));

    public bool SourceExists(FolderRef source, string relativePath) => File.Exists(Inside(Resolve(source, FolderAccessMode.Input), relativePath));

    public async Task<InFlightContent> ReadInFlightAsync(FolderRef source, Guid id, long maxBytes, CancellationToken ct)
    {
        var path = InFlightPath(Resolve(source, FolderAccessMode.Input), id);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                hash.AppendData(chunk, 0, read);
                total += read;
                if (total <= maxBytes)
                {
                    buffer.Write(chunk, 0, read);
                }
            }

            return new InFlightContent(Convert.ToHexStringLower(hash.GetHashAndReset()), total, total <= maxBytes ? buffer.ToArray() : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Translate(ex, source, "reading a file");
        }
    }

    public string Deliver(FolderRef source, Guid id, FolderRef destination, string fileName, string? sha256)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var inFlight = InFlightPath(Resolve(source, FolderAccessMode.Input), id);
        var folder = Resolve(destination, FolderAccessMode.Output);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        string[] candidates = [fileName, $"{stem}.{id.ToString("N")[..8]}{ext}", $"{stem}.{id:N}{ext}"];
        try
        {
            Directory.CreateDirectory(folder);
            EnsureNoLinks(destination, folder);

            // Already delivered (a crash after the move, before it was recorded)?
            foreach (var name in candidates)
            {
                var target = Path.Combine(folder, name);
                if (File.Exists(target) && (sha256 is null ? name == candidates[2] : Sha256Of(target) == sha256))
                {
                    if (File.Exists(inFlight))
                    {
                        File.Delete(inFlight);
                    }

                    return name;
                }
            }

            if (!File.Exists(inFlight))
            {
                throw new FolderException(FolderErrors.FileMissing, "The file is no longer in Verso's working folder.");
            }

            // The first free name; the full-id name is ours alone, so it may be replaced.
            var chosen = candidates.Take(2).FirstOrDefault(n => !File.Exists(Path.Combine(folder, n))) ?? candidates[2];

            // Copy under a hidden temporary name, then rename: the destination never holds a partial file under its
            // real name (it may be another system's input folder), even when source and destination are on different volumes.
            var temp = Path.Combine(folder, $".{id:N}.verso-partial");
            File.Copy(inFlight, temp, overwrite: true);
            File.Move(temp, Path.Combine(folder, chosen), overwrite: chosen == candidates[2]);
            File.Delete(inFlight);
            return chosen;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Translate(ex, destination, "moving the file to its destination");
        }
    }

    public void WriteSidecar(FolderRef destination, string fileName, string content)
    {
        var folder = Resolve(destination, FolderAccessMode.Output);
        var target = Inside(folder, fileName);
        var temp = Path.Combine(folder, $".{Guid.NewGuid():N}.verso-partial");
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Translate(ex, destination, "writing the error file");
        }
    }

    public IReadOnlyList<FolderCheck> Test(FolderRef target, FolderAccessMode mode)
    {
        ArgumentNullException.ThrowIfNull(target);
        var checks = new List<FolderCheck>();
        string folder;
        try
        {
            folder = Resolve(target, mode);
            checks.Add(new("approved", true, $"'{target.Location}' is configured on this Worker for {(mode == FolderAccessMode.Input ? "input" : "output")}."));
        }
        catch (FolderException ex)
        {
            checks.Add(new("approved", false, ex.Message));
            return checks;
        }

        try
        {
            if (mode == FolderAccessMode.Output && !Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                checks.Add(new("folder", true, "The folder didn't exist and was created."));
            }
            else
            {
                EnsureReadable(target, folder);
                checks.Add(new("folder", true, "The folder exists and can be listed."));
            }

            EnsureNoLinks(target, folder);

            var probe = Path.Combine(folder, $"{ProbePrefix}{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "Verso access test; safe to delete.");
            try
            {
                if (mode == FolderAccessMode.Input)
                {
                    // What ingestion does: move the file into Verso's working folder (needs modify rights), then remove it.
                    var working = InFlightFolder(folder);
                    Directory.CreateDirectory(working);
                    var moved = Path.Combine(working, Path.GetFileName(probe));
                    File.Move(probe, moved);
                    probe = moved;
                    checks.Add(new("modify", true, "Files can be created, moved into Verso's working folder and deleted."));
                }
                else
                {
                    checks.Add(new("write", true, "Files can be created and deleted."));
                }
            }
            finally
            {
                File.Delete(probe);
            }
        }
        catch (FolderException ex)
        {
            checks.Add(new("folder", false, ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var problem = Translate(ex, target, mode == FolderAccessMode.Input ? "reading and moving files" : "creating files");
            checks.Add(new(checks.Count < 2 ? "folder" : mode == FolderAccessMode.Input ? "modify" : "write", false, problem.Message));
        }

        return checks;
    }

    public bool IsSameOrInside(FolderRef inner, FolderRef outer)
    {
        try
        {
            var a = Path.TrimEndingDirectorySeparator(ResolvePath(inner));
            var b = Path.TrimEndingDirectorySeparator(ResolvePath(outer));
            return string.Equals(a, b, PathComparison) || a.StartsWith(b + Path.DirectorySeparatorChar, PathComparison);
        }
        catch (FolderException)
        {
            return false;
        }
    }

    // ----- resolution -----

    private string Resolve(FolderRef target, FolderAccessMode mode)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!_locations.TryGetValue(target.Location, out var location))
        {
            throw new FolderException(FolderErrors.UnknownLocation, $"Location '{target.Location}' isn't configured on this Worker ({Environment.MachineName}).");
        }

        if (location.Problem is not null)
        {
            throw new FolderException(FolderErrors.Misconfigured, $"Location '{target.Location}' is misconfigured on this Worker: {location.Problem}");
        }

        if (mode == FolderAccessMode.Input ? !location.Options.Input : !location.Options.Output)
        {
            throw new FolderException(FolderErrors.Misconfigured, $"Location '{target.Location}' isn't approved for {(mode == FolderAccessMode.Input ? "input" : "output")}.");
        }

        return ResolvePath(target);
    }

    private string ResolvePath(FolderRef target)
    {
        if (!_locations.TryGetValue(target.Location, out var location) || location.Root is null)
        {
            throw new FolderException(FolderErrors.UnknownLocation, $"Location '{target.Location}' isn't configured on this Worker.");
        }

        var subfolder = FolderPaths.NormalizeSubfolder(target.Subfolder);
        if (FolderPaths.SubfolderProblem(subfolder) is { } problem)
        {
            throw new FolderException(FolderErrors.OutsideLocation, $"Subfolder of '{target.Location}' isn't valid: {problem}");
        }

        return subfolder.Length == 0 ? location.Root : Inside(location.Root, subfolder);
    }

    /// <summary>Joins a relative '/' path to a folder and refuses results outside it.</summary>
    private static string Inside(string folder, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new FolderException(FolderErrors.OutsideLocation, "The path leaves the approved folder.");
        }

        return full;
    }

    /// <summary>Refuses linked folders between the location's root and <paramref name="folder"/> (they could point anywhere).</summary>
    private void EnsureNoLinks(FolderRef target, string folder)
    {
        var root = _locations[target.Location].Root!;
        var current = folder;
        while (current.Length > root.Length && !string.Equals(Path.TrimEndingDirectorySeparator(current), root, PathComparison))
        {
            var info = new DirectoryInfo(current);
            if (info.Exists && (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            {
                throw new FolderException(FolderErrors.OutsideLocation, $"A subfolder of '{target.Location}' is a link (symbolic link or junction); links aren't followed.");
            }

            current = Path.GetDirectoryName(current) ?? root;
        }
    }

    /// <summary>Distinguishes "not found", "access denied" and "unavailable" (Directory.Exists can't).</summary>
    private void EnsureReadable(FolderRef target, string folder)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(folder).GetEnumerator();
            entries.MoveNext();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Translate(ex, target, "listing the folder");
        }

        EnsureNoLinks(target, folder);
    }

    private FolderException Translate(Exception ex, FolderRef target, string action) => ex switch
    {
        FolderException f => f,
        UnauthorizedAccessException => new FolderException(FolderErrors.AccessDenied,
            $"Access denied to '{target}' for {Identity} while {action}. Grant the Worker's service account access to the folder.", ex),
        DirectoryNotFoundException => new FolderException(FolderErrors.NotFound,
            $"Folder '{target}' doesn't exist or isn't reachable from {Environment.MachineName}.", ex),
        FileNotFoundException => new FolderException(FolderErrors.FileMissing, $"A file in '{target}' disappeared while {action}.", ex),
        _ => new FolderException(FolderErrors.Unavailable,
            $"Folder '{target}' is unavailable from {Environment.MachineName} while {action} ({ex.GetType().Name}, HResult 0x{ex.HResult:X8}).", ex),
    };

    private static string InFlightFolder(string folder) => Path.Combine(folder, FolderPaths.InFlightFolder);

    private static string InFlightPath(string folder, Guid id) => Path.Combine(InFlightFolder(folder), id.ToString("N"));

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
