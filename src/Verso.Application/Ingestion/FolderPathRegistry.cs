using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Verso.Domain.Audit;
using Verso.Domain.Ingestion;

namespace Verso.Application.Ingestion;

/// <summary>
/// Records the folder paths workflow authors type into Folder input steps, so the Worker (which only knows keys from
/// the workflow definition and the ingestion records) can resolve them, also after a restart or a newer workflow
/// version. A new path, and a new purpose for a known path, is audited with who typed it.
/// </summary>
public sealed class FolderPathRegistry(IVersoDbContext db, ICurrentUser user, TimeProvider clock)
{
    public const string EntityType = "FolderPath";
    public const string RegisteredAction = "FolderPathRegistered";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Adds the paths to the context (the caller saves, so registering is part of the same change as publishing).</summary>
    public async Task RegisterAsync(IEnumerable<TypedPathUse> uses, CancellationToken ct)
    {
        var wanted = uses
            .GroupBy(u => FolderPaths.KeyForPath(u.Path))
            .Select(g => (Key: g.Key, Path: FolderPaths.NormalizePath(g.First().Path), Input: g.Any(u => u.Input), Output: g.Any(u => u.Output)))
            .ToList();
        if (wanted.Count == 0)
        {
            return;
        }

        var keys = wanted.Select(w => w.Key).ToList();
        var existing = await db.TypedFolderPaths.Where(p => keys.Contains(p.Key)).ToDictionaryAsync(p => p.Key, StringComparer.Ordinal, ct);
        var now = clock.GetUtcNow();
        foreach (var (key, path, input, output) in wanted)
        {
            if (!existing.TryGetValue(key, out var known))
            {
                db.TypedFolderPaths.Add(TypedFolderPath.Register(key, path, input, output, user.Id, now));
                Audit(key, path, input, output, created: true, now);
            }
            else if (known.AddPurpose(input, output))
            {
                Audit(key, known.Path, known.AllowInput, known.AllowOutput, created: false, now);
            }
        }
    }

    /// <summary>Hands every registered path to the file-system adapter (they are validated there, like any other location).</summary>
    public static async Task LoadIntoAsync(IVersoDbContext db, IFolderAccess folders, CancellationToken ct)
    {
        var rows = await db.TypedFolderPaths.AsNoTracking().ToListAsync(ct);
        folders.SetTypedPaths([.. rows.Select(r => new ManagedFolderPath(r.Key, r.Path, r.Path, r.AllowInput, r.AllowOutput))]);
    }

    /// <summary>Hands the administrator-managed locations and the typed paths to the adapter (used where the loop may not have run yet).</summary>
    public static async Task LoadAllAsync(IVersoDbContext db, IFolderAccess folders, CancellationToken ct)
    {
        var managed = await db.ManagedFolderLocations.AsNoTracking().Where(m => !m.Removed).ToListAsync(ct);
        folders.SetManagedLocations([.. managed.Select(m => new ManagedFolderPath(m.Key, m.Name, m.Path, m.AllowInput, m.AllowOutput))]);
        await LoadIntoAsync(db, folders, ct);
    }

    private void Audit(string key, string path, bool input, bool output, bool created, DateTimeOffset now) =>
        db.AuditEntries.Add(AuditEntry.Record(
            RegisteredAction, EntityType, key, user.Id, user.Name, now,
            JsonSerializer.Serialize(new { path, input, output, created }, Json), Activity.Current?.TraceId.ToHexString()));
}
