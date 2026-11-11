using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Verso.Domain.Audit;
using Verso.Domain.Ingestion;

namespace Verso.Application.Ingestion;

public sealed record SaveFolderLocationRequest(string? Name, string? Path, bool? Input, bool? Output);

/// <summary>
/// A location as administrators see it. <see cref="Path"/> is set for locations managed in Verso and null for the
/// ones set in the Worker's configuration (their paths aren't known to the API). <see cref="Problem"/> is what the
/// Worker reported; a new or changed location reads "Waiting for the Worker" until it has picked it up (about 15 seconds).
/// </summary>
public sealed record AdminFolderLocationDto(
    string Key, string Name, string? Path, bool Input, bool Output, bool Managed, bool Enabled, string? Problem, DateTimeOffset UpdatedAt, string? UpdatedBy);

/// <summary>
/// Administration of approved folder locations (Admin role): add, change and remove locations without editing the
/// Worker's configuration. Whoever can do this chooses what the Worker's service account reads and writes, so it is
/// Admin only and audited. The Worker validates the path as its own identity (and against its optional
/// <c>FolderInput:AllowedRoots</c>) and reports the outcome; designers still only ever pick keys.
/// </summary>
public sealed class FolderLocationAdmin(IVersoDbContext db, ICurrentUser user, TimeProvider clock)
{
    public const string EntityType = "FolderLocation";
    public const string SavedAction = "FolderLocationSaved";
    public const string RemovedAction = "FolderLocationRemoved";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<AdminFolderLocationDto>> ListAsync(CancellationToken ct)
    {
        var catalog = await db.FolderLocations.AsNoTracking().ToDictionaryAsync(l => l.Key, StringComparer.Ordinal, ct);
        var managed = await db.ManagedFolderLocations.AsNoTracking().ToDictionaryAsync(l => l.Key, StringComparer.Ordinal, ct);
        var result = new List<AdminFolderLocationDto>();

        foreach (var m in managed.Values.Where(m => !m.Removed))
        {
            catalog.TryGetValue(m.Key, out var reported);
            var applied = reported is { Managed: true };
            result.Add(new AdminFolderLocationDto(
                m.Key, m.Name, m.Path, m.AllowInput, m.AllowOutput, Managed: true,
                Enabled: applied && reported!.Enabled,
                Problem: applied
                    ? reported!.Problem
                    : reported is null
                        ? "Waiting for the Worker to pick it up (about 15 seconds)."
                        : "This key is also set in the Worker's configuration, which takes precedence.",
                m.UpdatedAt, m.UpdatedBy));
        }

        // Set in the Worker's configuration: listed for completeness, read-only, and without a path.
        foreach (var l in catalog.Values.Where(l => !l.Managed && !managed.ContainsKey(l.Key)))
        {
            result.Add(new AdminFolderLocationDto(l.Key, l.Name, null, l.AllowInput, l.AllowOutput, Managed: false, l.Enabled, l.Problem, l.UpdatedAt, l.ReportedBy));
        }

        return [.. result.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<Result<AdminFolderLocationDto>> SaveAsync(string key, SaveFolderLocationRequest? request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (!FolderPaths.IsValidLocationKey(key))
        {
            errors["key"] = ["2–64 characters: lower-case letters, digits and '-', starting with a letter (e.g. ap-inbound)."];
        }

        var name = request?.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 200)
        {
            errors["name"] = ["1–200 characters."];
        }

        var path = request?.Path?.Trim() ?? "";
        if (PathProblem(path) is { } pathProblem)
        {
            errors["path"] = [pathProblem];
        }

        var input = request?.Input ?? false;
        var output = request?.Output ?? false;
        if (!input && !output)
        {
            errors["input"] = ["Allow files to be picked up from it (input), moved into it (output), or both."];
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var existing = await db.ManagedFolderLocations.SingleOrDefaultAsync(l => l.Key == key, ct);
        if (existing is null && await db.FolderLocations.AnyAsync(l => l.Key == key && !l.Managed, ct))
        {
            return AppError.Conflict("folder_location.key_taken", $"'{key}' is set in the Worker's configuration. Choose another key, or change it there.");
        }

        var now = clock.GetUtcNow();
        if (existing is null)
        {
            db.ManagedFolderLocations.Add(ManagedFolderLocation.Create(key, name, path, input, output, user.Id, now));
        }
        else
        {
            existing.Save(name, path, input, output, user.Id, now);
        }

        Audit(SavedAction, key, now, new { name, path, input, output, created = existing is null });
        await db.SaveChangesAsync(ct);
        return (await ListAsync(ct)).Single(l => l.Key == key && l.Managed);
    }

    /// <summary>Removes a managed location. Refused while files of unfinished ingestions still refer to it.</summary>
    public async Task<Result<bool>> RemoveAsync(string key, CancellationToken ct)
    {
        var existing = await db.ManagedFolderLocations.SingleOrDefaultAsync(l => l.Key == key && !l.Removed, ct);
        if (existing is null)
        {
            return AppError.NotFound("folder_location.not_found", $"No location '{key}' is managed here. Locations set in the Worker's configuration are removed there.");
        }

        if (await db.FolderFiles.AnyAsync(f => f.Status != FolderFileStatus.Completed
                                               && (f.SourceLocation == key || f.ProcessedLocation == key || f.FailedLocation == key), ct))
        {
            return AppError.Conflict("folder_location.in_use", $"Files from or to '{key}' are still being processed. Remove it once they are finished.");
        }

        var now = clock.GetUtcNow();
        existing.Remove(user.Id, now);
        Audit(RemovedAction, key, now, new { name = existing.Name, path = existing.Path });
        await db.SaveChangesAsync(ct);
        return Result.Success(true);
    }

    /// <summary>
    /// Checks only what is true on every operating system: absolute (drive letter, UNC or leading '/'), no '.' or '..'
    /// parts, no control or reserved characters. The Worker checks the rest where the path lives.
    /// </summary>
    public static string? PathProblem(string path)
    {
        if (path.Length == 0)
        {
            return "Required: the absolute path as the Worker's server sees it, such as D:\\Verso\\inbound, \\\\fileserver\\share\\inbound or /mnt/verso/inbound.";
        }

        if (path.Length > ManagedFolderLocation.MaxPathLength)
        {
            return $"At most {ManagedFolderLocation.MaxPathLength} characters.";
        }

        if (path.Any(char.IsControl) || path.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)
        {
            return "Contains characters a folder name can't have.";
        }

        var drive = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
        if (!drive && !path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith('/'))
        {
            return "Must be absolute: a drive path (D:\\…), a network share (\\\\server\\share\\…) or a path starting with '/'.";
        }

        if (path.Split('\\', '/').Any(s => s is ".." or "."))
        {
            return "No '.' or '..' parts.";
        }

        return null;
    }

    private void Audit(string action, string key, DateTimeOffset now, object details) =>
        db.AuditEntries.Add(AuditEntry.Record(
            action, EntityType, key, user.Id, user.Name, now, JsonSerializer.Serialize(details, Json), Activity.Current?.TraceId.ToHexString()));
}
