using Microsoft.EntityFrameworkCore;

namespace Verso.Application.Ingestion;

/// <summary>An approved location as designers see it: key, name and purpose. Never its path.</summary>
public sealed record FolderLocationDto(string Key, string Name, bool Input, bool Output, bool Enabled, string? Problem, DateTimeOffset UpdatedAt);

/// <param name="LastStatus"><c>Ok</c>, <c>Unavailable</c>, <c>AccessDenied</c>, <c>Misconfigured</c> or <c>Error</c>; null before the first scan.</param>
public sealed record FolderWatchDto(string NodeId, DateTimeOffset? LastScanAt, string? LastStatus, string? LastError, int LastFilesFound);

/// <param name="Status"><c>Claimed</c>, <c>Submitted</c> (waiting for its run), <c>Delivering</c> or <c>Completed</c>.</param>
/// <param name="Outcome"><c>Processed</c> or <c>Failed</c> once decided.</param>
public sealed record FolderFileDto(
    Guid Id, string FileName, string RelativePath, long SizeBytes, string Status, string? Outcome, string? ErrorCode, string? ErrorMessage,
    Guid? ExecutionId, string? DeliveredName, string? DeliveryError, DateTimeOffset ClaimedAt, DateTimeOffset? CompletedAt);

/// <summary>Folder-input state of a workflow: the last scan (errors such as an unreachable folder show here) and recent files.</summary>
public sealed record FolderInputStatusDto(FolderWatchDto? Watch, IReadOnlyList<FolderFileDto> Files);

public sealed class FolderInputQueries(IVersoDbContext db)
{
    public const int RecentFiles = 25;

    public async Task<IReadOnlyList<FolderLocationDto>> LocationsAsync(CancellationToken ct) =>
        await db.FolderLocations.AsNoTracking()
            .OrderBy(l => l.Name)
            .Select(l => new FolderLocationDto(l.Key, l.Name, l.AllowInput, l.AllowOutput, l.Enabled, l.Problem, l.UpdatedAt))
            .ToListAsync(ct);

    public async Task<Result<FolderInputStatusDto>> StatusAsync(Guid workflowId, CancellationToken ct)
    {
        if (!await db.Workflows.AnyAsync(w => w.Id == workflowId, ct))
        {
            return AppError.NotFound("workflow.not_found", $"Workflow {workflowId} was not found.");
        }

        var watch = await db.FolderWatches.AsNoTracking().SingleOrDefaultAsync(w => w.WorkflowId == workflowId, ct);
        var files = await db.FolderFiles.AsNoTracking()
            .Where(f => f.WorkflowId == workflowId)
            .OrderByDescending(f => f.ClaimedAt)
            .Take(RecentFiles)
            .ToListAsync(ct);
        return new FolderInputStatusDto(
            watch is null ? null : new FolderWatchDto(watch.NodeId, watch.LastScanAt, watch.LastStatus?.ToString(), watch.LastError, watch.LastFilesFound),
            [.. files.Select(f => new FolderFileDto(
                f.Id, f.FileName, f.RelativePath, f.SizeBytes, f.Status.ToString(), f.Outcome?.ToString(), f.ErrorCode, f.ErrorMessage,
                f.ExecutionId, f.DeliveredName, f.DeliveryError, f.ClaimedAt, f.CompletedAt))]);
    }
}
