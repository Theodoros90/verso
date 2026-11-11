using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Verso.Application.Executions;
using Verso.Application.Workflows;
using Verso.Domain.Executions;
using Verso.Domain.Ingestion;
using Verso.Domain.Workflows;

namespace Verso.Application.Ingestion;

/// <summary>Worker settings for folder input (section <c>FolderInput</c>; the locations themselves are read by Infrastructure).</summary>
public sealed class FolderIngestionOptions
{
    public const string Section = "FolderInput";

    /// <summary>How often the Worker looks for due scans, finished runs and unfinished files.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>A file must be unchanged for this long before it is picked up (handoff convention, second line of defence).</summary>
    public TimeSpan SettleTime { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Largest file read (bytes). Larger files go to the failed location unread.</summary>
    public long MaxFileBytes { get; set; } = 1024 * 1024;

    /// <summary>Files claimed per folder per scan; the rest wait for the next scan.</summary>
    public int MaxFilesPerScan { get; set; } = 25;

    /// <summary>How long a Worker holds a file it is working on; after that another Worker may take it over.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>What one pass did (for logs and tests).</summary>
public sealed record FolderPassResult(int Scanned, int Claimed, int Submitted, int Delivered, int Errors);

/// <summary>
/// Folder input, run by the Worker (never the API or a browser). Each pass:
/// <list type="number">
/// <item>finishes files left unfinished (Worker restart, crash, unreachable destination), and moves the files of
/// finished runs to the processed or failed location;</item>
/// <item>scans the source folder of every workflow whose latest published version starts with a <c>folder-input</c>
/// trigger, when its interval is due, and ingests new files.</item>
/// </list>
/// Ingesting a file: a <see cref="FolderFile"/> row is inserted first (a filtered unique index on the source file
/// means only one Worker can claim it), then the file is moved into the folder's <c>.verso-inflight</c> folder, read,
/// and submitted as a run. The run's idempotency key is derived from the content, so identical data never starts a
/// second run (a duplicate goes to the failed location), and resubmitting after a crash finds the same run.
/// </summary>
public sealed partial class FolderIngestion(
    IVersoDbContext db, IFolderAccess folders, ExecutionService executions, FolderIngestionOptions options, TimeProvider clock, ILogger<FolderIngestion> logger)
{
    /// <summary>The actor recorded on runs started from a folder.</summary>
    public const string ActorId = "verso.folder-input";

    public const string ActorName = "Folder input";

    private const int BatchSize = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static readonly string Worker = $"{Environment.MachineName}:{Environment.ProcessId}";

    public async Task<FolderPassResult> RunOnceAsync(CancellationToken ct)
    {
        var counters = new Counters();
        await ResumeClaimedAsync(counters, ct);
        await RouteFinishedRunsAsync(ct);
        await DeliverPendingAsync(counters, ct);
        await ScanDueFoldersAsync(counters, ct);
        return new FolderPassResult(counters.Scanned, counters.Claimed, counters.Submitted, counters.Delivered, counters.Errors);
    }

    /// <summary>Replaces the catalog of approved locations with this Worker's configuration (keys, names, purposes only).</summary>
    public async Task SyncLocationsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var configured = folders.Locations.ToDictionary(l => l.Key, StringComparer.Ordinal);
        var stored = await db.FolderLocations.ToListAsync(ct);
        foreach (var location in stored)
        {
            if (configured.Remove(location.Key, out var c))
            {
                if (location.Name != c.Name || location.AllowInput != c.Input || location.AllowOutput != c.Output || location.Problem != c.Problem || !location.Enabled && c.Problem is null)
                {
                    location.Update(c.Name, c.Input, c.Output, c.Problem, Worker, now);
                }
            }
            else if (location.Enabled)
            {
                location.Withdraw(Worker, now);
            }
        }

        foreach (var c in configured.Values)
        {
            db.FolderLocations.Add(FolderLocation.Report(c.Key, c.Name, c.Input, c.Output, c.Problem, Worker, now));
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            LogSyncConflict(logger, ex); // another Worker reported at the same time; the next sync converges
        }
    }

    private sealed class Counters
    {
        public int Scanned;
        public int Claimed;
        public int Submitted;
        public int Delivered;
        public int Errors;
    }

    // ----- 1. Recovery: files claimed by a Worker that stopped before submitting them -----

    private async Task ResumeClaimedAsync(Counters counters, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var stale = await db.FolderFiles
            .Where(f => f.Status == FolderFileStatus.Claimed && (f.LockedUntil == null || f.LockedUntil < now))
            .OrderBy(f => f.ClaimedAt)
            .Take(BatchSize)
            .ToListAsync(ct);
        foreach (var file in stale)
        {
            if (!await TryLockAsync(file, ct))
            {
                continue;
            }

            LogResuming(logger, file.Id, file.Status);
            await ProcessClaimedAsync(file, counters, ct);
        }
    }

    // ----- 2. Runs that finished: decide where their file goes -----

    private async Task RouteFinishedRunsAsync(CancellationToken ct)
    {
        var waiting = await db.FolderFiles
            .Where(f => f.Status == FolderFileStatus.Submitted)
            .OrderBy(f => f.ClaimedAt)
            .Take(BatchSize * 5)
            .ToListAsync(ct);
        if (waiting.Count == 0)
        {
            return;
        }

        var ids = waiting.Select(f => f.ExecutionId!.Value).ToList();
        var runs = await db.Executions.AsNoTracking()
            .Where(e => ids.Contains(e.Id) && (e.Status == ExecutionStatus.Succeeded || e.Status == ExecutionStatus.Rejected || e.Status == ExecutionStatus.Failed))
            .Select(e => new { e.Id, e.Status, e.FailureCode, e.FailureMessage })
            .ToDictionaryAsync(e => e.Id, ct);
        foreach (var file in waiting)
        {
            if (!runs.TryGetValue(file.ExecutionId!.Value, out var run))
            {
                continue;
            }

            if (run.Status == ExecutionStatus.Succeeded)
            {
                file.Route(FolderFileOutcome.Processed, null, null);
            }
            else
            {
                file.Route(FolderFileOutcome.Failed, run.FailureCode ?? "execution." + run.Status.ToString().ToLowerInvariant(),
                    $"The run {(run.Status == ExecutionStatus.Rejected ? "was rejected" : "failed")}: {run.FailureMessage}");
            }
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear(); // another Worker routed them; it delivers them too
        }
    }

    // ----- 3. Move routed files to their destination -----

    private async Task DeliverPendingAsync(Counters counters, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var pending = await db.FolderFiles
            .Where(f => f.Status == FolderFileStatus.Delivering && (f.LockedUntil == null || f.LockedUntil < now))
            .OrderBy(f => f.ClaimedAt)
            .Take(BatchSize)
            .ToListAsync(ct);
        foreach (var file in pending)
        {
            if (await TryLockAsync(file, ct))
            {
                await DeliverAsync(file, counters, ct);
            }
        }
    }

    // ----- 4. Scan due folders -----

    private async Task ScanDueFoldersAsync(Counters counters, CancellationToken ct)
    {
        foreach (var (workflowId, workflowKey, node, settings) in await FolderWorkflowsAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            var now = clock.GetUtcNow();
            var watch = await db.FolderWatches.SingleOrDefaultAsync(w => w.WorkflowId == workflowId, ct);
            if (watch is null)
            {
                watch = FolderWatch.Start(workflowId, workflowKey, node.Id);
                db.FolderWatches.Add(watch);
            }

            if (!watch.TryStartScan(node.Id, now, settings.IntervalTime) || !await SaveOrSkipAsync(ct))
            {
                continue; // not due, or another Worker took this scan
            }

            counters.Scanned++;
            await ScanAsync(watch, workflowId, workflowKey, node.Id, settings, counters, ct);
        }
    }

    private async Task ScanAsync(FolderWatch watch, Guid workflowId, string workflowKey, string nodeId, FolderInputSettings settings, Counters counters, CancellationToken ct)
    {
        IReadOnlyList<FoundFile> found;
        try
        {
            if (DestinationLoop(settings) is { } loop)
            {
                throw new FolderException(FolderErrors.Misconfigured, loop);
            }

            found = folders.List(settings.Source, settings.Pattern, settings.IncludeSubfolders, options.SettleTime, clock.GetUtcNow(), options.MaxFilesPerScan);
        }
        catch (FolderException ex)
        {
            counters.Errors++;
            LogScanFailed(logger, workflowKey, ex.Code);
            watch.FinishScan(ScanStatus(ex.Code), ex.Message, 0, clock.GetUtcNow());
            await SaveOrSkipAsync(ct);
            return;
        }

        watch.FinishScan(FolderScanStatus.Ok, null, found.Count, clock.GetUtcNow());
        await SaveOrSkipAsync(ct);

        foreach (var candidate in found)
        {
            ct.ThrowIfCancellationRequested();
            var file = FolderFile.Claim(
                workflowId, workflowKey, nodeId, settings.Source.Location, settings.Source.Subfolder,
                candidate.RelativePath, candidate.FileName, candidate.SizeBytes, candidate.LastWriteAt,
                settings.Processed.Location, settings.Processed.Subfolder, settings.Failed.Location, settings.Failed.Subfolder,
                Worker, clock.GetUtcNow(), options.Lease);
            db.FolderFiles.Add(file);
            if (!await SaveOrSkipAsync(ct))
            {
                continue; // another Worker claimed this file (unique index on the source file while claimed)
            }

            bool moved;
            try
            {
                moved = folders.TryMoveToInFlight(Source(file), file.RelativePath, file.Id);
            }
            catch (FolderException ex)
            {
                // E.g. the files can be read but not moved (no modify right): record it on the scan and stop here.
                counters.Errors++;
                db.FolderFiles.Remove(file);
                var current = await db.FolderWatches.SingleAsync(w => w.WorkflowId == workflowId, ct); // tracked even after an earlier lost race
                current.FinishScan(ScanStatus(ex.Code), ex.Message, found.Count, clock.GetUtcNow());
                await SaveOrSkipAsync(ct);
                LogScanFailed(logger, workflowKey, ex.Code);
                return;
            }

            if (!moved)
            {
                // Gone, or still open for writing: release the claim; a later scan sees it again if it is still there.
                db.FolderFiles.Remove(file);
                await SaveOrSkipAsync(ct);
                continue;
            }

            counters.Claimed++;
            LogClaimed(logger, file.Id, workflowKey);
            await ProcessClaimedAsync(file, counters, ct);
        }
    }

    /// <summary>Read the in-flight file, then submit it as a run or route it to the failed location.</summary>
    private async Task ProcessClaimedAsync(FolderFile file, Counters counters, CancellationToken ct)
    {
        var source = Source(file);
        try
        {
            if (!folders.InFlightExists(source, file.Id)
                && !(folders.SourceExists(source, file.RelativePath) && folders.TryMoveToInFlight(source, file.RelativePath, file.Id)))
            {
                file.Lost("The file was removed from the folder before Verso read it.", clock.GetUtcNow());
                await SaveOrSkipAsync(ct);
                return;
            }
        }
        catch (FolderException ex)
        {
            // The folder is unreachable or no longer configured on this Worker: leave the file; the lease expires and
            // a later pass (or a Worker that has the location) tries again.
            counters.Errors++;
            LogReadFailed(logger, file.Id, ex.Code);
            return;
        }

        InFlightContent content;
        try
        {
            content = await folders.ReadInFlightAsync(source, file.Id, options.MaxFileBytes, ct);
        }
        catch (FolderException ex)
        {
            counters.Errors++;
            LogReadFailed(logger, file.Id, ex.Code); // the lease expires and recovery tries again
            return;
        }

        file.Read(content.Sha256, content.SizeBytes);
        if (content.Content is null)
        {
            file.Route(FolderFileOutcome.Failed, "file.too_large", $"The file is larger than {options.MaxFileBytes:N0} bytes.");
        }
        else if (ParseObject(content.Content) is not { } data)
        {
            file.Route(FolderFileOutcome.Failed, "file.invalid_json", IsJsonName(file.FileName)
                ? "The file isn't a JSON object (synthetic invoice data is expected)."
                : "Only JSON files can be read in this version; XML and CSV need a field mapping, which isn't built yet.");
        }
        else
        {
            using (data)
            {
                await SubmitAsync(file, data.RootElement, counters, ct);
            }
        }

        if (file.Status == FolderFileStatus.Delivering && await SaveOrSkipAsync(ct))
        {
            await DeliverAsync(file, counters, ct);
        }
    }

    private async Task SubmitAsync(FolderFile file, JsonElement data, Counters counters, CancellationToken ct)
    {
        // Content-derived key: identical data for this workflow always maps to the same run.
        var key = "folder:" + CanonicalJson.Sha256Hex(data);
        var correlationId = file.Id.ToString("N"); // the ingestion id, so a run and its file are easy to link
        var result = await executions.SubmitAsync(file.WorkflowKey, key, correlationId, new SubmitExecutionRequest(data), ct);
        if (!result.IsSuccess)
        {
            var error = result.Error!;
            file.Route(FolderFileOutcome.Failed, error.Code, error.Message); // e.g. the workflow's template isn't published any more
            return;
        }

        var executionId = result.Value!.Execution.ExecutionId;
        var owner = await db.FolderFiles.AsNoTracking()
            .Where(f => f.ExecutionId == executionId && f.Id != file.Id)
            .Select(f => new { f.FileName, f.ClaimedAt })
            .FirstOrDefaultAsync(ct);
        if (!result.Value.Created && owner is not null)
        {
            file.Route(FolderFileOutcome.Failed, "file.duplicate",
                $"Same data as '{owner.FileName}' (picked up {owner.ClaimedAt:yyyy-MM-dd HH:mm} UTC, run {executionId}); not run again.");
            return;
        }

        // A replay without another owner is this file's own run, found again after a restart.
        file.Submitted(executionId);
        try
        {
            await db.SaveChangesAsync(ct);
            counters.Submitted++;
            LogSubmitted(logger, file.Id, executionId);
        }
        catch (DbUpdateException)
        {
            // Another file with the same data took this run at the same moment (unique index on ExecutionId).
            db.ChangeTracker.Clear();
            var reloaded = await db.FolderFiles.SingleAsync(f => f.Id == file.Id, ct);
            reloaded.Route(FolderFileOutcome.Failed, "file.duplicate", $"Same data as another file picked up at the same time (run {executionId}); not run again.");
            if (await SaveOrSkipAsync(ct))
            {
                await DeliverAsync(reloaded, counters, ct);
            }
        }
    }

    private async Task DeliverAsync(FolderFile file, Counters counters, CancellationToken ct)
    {
        var (location, subfolder) = file.Destination;
        var destination = new FolderRef(location, subfolder);
        try
        {
            var name = folders.Deliver(Source(file), file.Id, destination, file.FileName, file.Sha256);
            file.Delivered(name, clock.GetUtcNow());
            if (file.Outcome == FolderFileOutcome.Failed)
            {
                WriteErrorFile(file, destination, name);
            }

            counters.Delivered++;
            LogDelivered(logger, file.Id, file.Outcome!.Value, file.ErrorCode);
        }
        catch (FolderException ex) when (ex.Code == FolderErrors.FileMissing)
        {
            counters.Errors++;
            file.Lost("The file was removed from Verso's working folder before it could be moved.", clock.GetUtcNow());
            LogFileLost(logger, file.Id);
        }
        catch (FolderException ex)
        {
            counters.Errors++;
            file.DeliveryFailed($"{ex.Code}: {ex.Message}");
            LogDeliveryFailed(logger, file.Id, ex.Code);
        }

        await SaveOrSkipAsync(ct);
    }

    private void WriteErrorFile(FolderFile file, FolderRef destination, string name)
    {
        var details = JsonSerializer.Serialize(new
        {
            file = file.FileName,
            originalPath = file.RelativePath,
            workflow = file.WorkflowKey,
            pickedUpAt = file.ClaimedAt,
            code = file.ErrorCode,
            message = file.ErrorMessage,
            executionId = file.ExecutionId,
            ingestionId = file.Id,
        }, Json);
        try
        {
            folders.WriteSidecar(destination, name + ".error.json", details);
        }
        catch (FolderException ex)
        {
            LogSidecarFailed(logger, file.Id, ex.Code); // the record in the database is the reference
        }
    }

    /// <summary>Workflows whose latest published version starts with a folder-input trigger, with that trigger's settings.</summary>
    private async Task<List<(Guid WorkflowId, string WorkflowKey, WorkflowNode Node, FolderInputSettings Settings)>> FolderWorkflowsAsync(CancellationToken ct)
    {
        var marker = $"\"{NodeCatalog.FolderInput}\"";
        var latest = await db.WorkflowVersions.AsNoTracking()
            .Where(v => v.Status == WorkflowVersionStatus.Published
                        && v.VersionNumber == db.WorkflowVersions.Where(o => o.WorkflowId == v.WorkflowId && o.Status == WorkflowVersionStatus.Published).Max(o => o.VersionNumber)
                        && v.Definition.Contains(marker))
            .Join(db.Workflows.AsNoTracking(), v => v.WorkflowId, w => w.Id, (v, w) => new { v.WorkflowId, w.Key, v.Definition })
            .ToListAsync(ct);

        var result = new List<(Guid, string, WorkflowNode, FolderInputSettings)>();
        foreach (var row in latest)
        {
            var definition = WorkflowDefinitionParser.Parse(row.Definition).Definition;
            var trigger = definition?.Nodes.FirstOrDefault(n => n.Type == NodeCatalog.FolderInput);
            if (trigger is not null && FolderInputSettings.Parse(trigger.Config) is { } settings)
            {
                result.Add((row.WorkflowId, row.Key, trigger, settings));
            }
        }

        return result;
    }

    /// <summary>
    /// Destinations whose files would be scanned again: by configuration (same key) or physically (two keys for the
    /// same share): the source folder itself, or a folder below it when subfolders are included.
    /// </summary>
    private string? DestinationLoop(FolderInputSettings s)
    {
        foreach (var (label, destination) in new[] { ("processed", s.Processed), ("failed", s.Failed) })
        {
            var below = folders.IsSameOrInside(destination, s.Source);
            var same = below && folders.IsSameOrInside(s.Source, destination);
            if (FolderPaths.WouldRescan(s.Source, destination, s.IncludeSubfolders) || same || (below && s.IncludeSubfolders))
            {
                return $"The {label} location would be scanned again. Choose a folder outside the source folder.";
            }
        }

        return null;
    }

    private static FolderScanStatus ScanStatus(string code) => code switch
    {
        FolderErrors.AccessDenied => FolderScanStatus.AccessDenied,
        FolderErrors.NotFound or FolderErrors.Unavailable => FolderScanStatus.Unavailable,
        FolderErrors.UnknownLocation or FolderErrors.Misconfigured or FolderErrors.OutsideLocation => FolderScanStatus.Misconfigured,
        _ => FolderScanStatus.Error,
    };

    private static FolderRef Source(FolderFile f) => new(f.SourceLocation, f.SourceSubfolder);

    private static bool IsJsonName(string fileName) => fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private static JsonDocument? ParseObject(byte[] content)
    {
        try
        {
            var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = ExecutionService.MaxDataDepth + 1 });
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<bool> TryLockAsync(FolderFile file, CancellationToken ct) =>
        file.TryLock(clock.GetUtcNow(), options.Lease) && await SaveOrSkipAsync(ct);

    /// <summary>False (and the pending changes discarded) when another Worker changed or claimed the same rows.</summary>
    private async Task<bool> SaveOrSkipAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Folder file {IngestionId} claimed for workflow {WorkflowKey}")]
    private static partial void LogClaimed(ILogger logger, Guid ingestionId, string workflowKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "Folder file {IngestionId} submitted as execution {ExecutionId}")]
    private static partial void LogSubmitted(ILogger logger, Guid ingestionId, Guid executionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Folder file {IngestionId} moved: {Outcome} {Code}")]
    private static partial void LogDelivered(ILogger logger, Guid ingestionId, FolderFileOutcome outcome, string? code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Resuming folder file {IngestionId} ({Status}) left by a stopped Worker")]
    private static partial void LogResuming(ILogger logger, Guid ingestionId, FolderFileStatus status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scanning the folder of workflow {WorkflowKey} failed: {Code}")]
    private static partial void LogScanFailed(ILogger logger, string workflowKey, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading folder file {IngestionId} failed: {Code}; will retry")]
    private static partial void LogReadFailed(ILogger logger, Guid ingestionId, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Moving folder file {IngestionId} failed: {Code}; will retry")]
    private static partial void LogDeliveryFailed(ILogger logger, Guid ingestionId, string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "Folder file {IngestionId} disappeared from Verso's working folder")]
    private static partial void LogFileLost(ILogger logger, Guid ingestionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Writing the error file of folder file {IngestionId} failed: {Code}")]
    private static partial void LogSidecarFailed(ILogger logger, Guid ingestionId, string code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Folder locations were reported by another Worker at the same time")]
    private static partial void LogSyncConflict(ILogger logger, Exception exception);
}
