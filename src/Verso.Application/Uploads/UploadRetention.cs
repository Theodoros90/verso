using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Verso.Domain.Ingestion;

namespace Verso.Application.Uploads;

/// <summary>
/// The Worker's retention pass over staged uploads (ADR 0023). A <see cref="UploadStatus.Staged"/> row older than
/// <see cref="UploadOptions.Retention"/> is either expired (its run exists: bytes cleared, metadata kept, so the run fails with
/// <c>upload.expired</c> and the duplicate rule still holds) or deleted (no run exists, nothing refers to it). Whether a run exists is
/// decided by the run key <c>upload:&lt;id:N&gt;</c>, never by <c>ExecutionId</c>, which is saved afterwards and can be lost.
/// Every change is a single conditional statement that re-checks <c>Status = 'Staged'</c>: the Worker step may be marking the same row
/// Taken at that moment, so the row is never loaded and saved. Taken and Expired rows are never touched.
/// </summary>
public sealed class UploadRetention(IVersoDbContext db, TimeProvider clock, IOptions<UploadOptions> options)
{
    /// <summary>Returns the number of rows expired or deleted.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - options.Value.Retention;
        var old = await db.Uploads.AsNoTracking()
            .Where(u => u.Status == UploadStatus.Staged && u.CreatedAt < cutoff)
            .Select(u => u.Id)
            .ToListAsync(ct);

        var changed = 0;
        foreach (var id in old)
        {
            ct.ThrowIfCancellationRequested();
            var key = UploadService.KeyPrefix + id.ToString("N");
            if (await db.Executions.AnyAsync(e => e.IdempotencyKey == key, ct))
            {
                changed += await db.Uploads.Where(u => u.Id == id && u.Status == UploadStatus.Staged)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UploadStatus.Expired).SetProperty(u => u.Content, (byte[]?)null), ct);
            }
            else
            {
                changed += await db.Uploads
                    .Where(u => u.Id == id && u.Status == UploadStatus.Staged && !db.Executions.Any(e => e.IdempotencyKey == key))
                    .ExecuteDeleteAsync(ct);
            }
        }

        return changed;
    }
}
