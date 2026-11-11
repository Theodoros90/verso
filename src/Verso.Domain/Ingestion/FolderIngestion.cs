namespace Verso.Domain.Ingestion;

/// <summary>
/// An approved folder, as the Worker reports it from its own configuration (<c>FolderInput:Locations</c>). Only the
/// key, display name and purpose are stored: the physical path stays in the Worker's configuration, so neither the
/// API nor a browser ever sees or chooses a server path.
/// </summary>
public sealed class FolderLocation
{
    public const int MaxProblemLength = 500;

    private FolderLocation() { } // EF Core

    public string Key { get; private set; } = "";
    public string Name { get; private set; } = "";

    /// <summary>Files may be picked up from here.</summary>
    public bool AllowInput { get; private set; }

    /// <summary>Processed or failed files may be moved here.</summary>
    public bool AllowOutput { get; private set; }

    /// <summary>False when the Worker no longer has it configured, or its configuration is invalid (see <see cref="Problem"/>).</summary>
    public bool Enabled { get; private set; }

    /// <summary>Why it is disabled (sanitized: never a path).</summary>
    public string? Problem { get; private set; }

    public string ReportedBy { get; private set; } = "";
    public DateTimeOffset UpdatedAt { get; private set; }

    public static FolderLocation Report(string key, string name, bool allowInput, bool allowOutput, string? problem, string reportedBy, DateTimeOffset now)
    {
        var location = new FolderLocation { Key = key };
        location.Update(name, allowInput, allowOutput, problem, reportedBy, now);
        return location;
    }

    public void Update(string name, bool allowInput, bool allowOutput, string? problem, string reportedBy, DateTimeOffset now)
    {
        Name = name;
        AllowInput = allowInput;
        AllowOutput = allowOutput;
        Problem = Truncate(problem, MaxProblemLength);
        Enabled = problem is null;
        ReportedBy = reportedBy;
        UpdatedAt = now;
    }

    /// <summary>No longer in the Worker's configuration. Kept (not deleted) so workflows that name it show why it stopped.</summary>
    public void Withdraw(string reportedBy, DateTimeOffset now) =>
        Update(Name, AllowInput, AllowOutput, "Not configured on the Worker any more.", reportedBy, now);

    internal static string? Truncate(string? text, int max) => text is { } t && t.Length > max ? t[..max] : text;
}

public enum FolderScanStatus
{
    Ok,
    Unavailable,
    AccessDenied,
    Misconfigured,
    Error,
}

/// <summary>
/// Scan state of one workflow's folder-input trigger. A Worker takes a scan by moving <see cref="LastScanStartedAt"/>
/// forward under optimistic concurrency, so with several Workers each folder is scanned once per interval.
/// </summary>
public sealed class FolderWatch
{
    public const int MaxErrorLength = 500;

    private FolderWatch() { } // EF Core

    public Guid WorkflowId { get; private set; }
    public string WorkflowKey { get; private set; } = "";
    public string NodeId { get; private set; } = "";
    public DateTimeOffset? LastScanStartedAt { get; private set; }
    public DateTimeOffset? LastScanAt { get; private set; }
    public FolderScanStatus? LastStatus { get; private set; }

    /// <summary>Sanitized message of the last failed scan (never a path).</summary>
    public string? LastError { get; private set; }

    public int LastFilesFound { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static FolderWatch Start(Guid workflowId, string workflowKey, string nodeId) =>
        new() { WorkflowId = workflowId, WorkflowKey = workflowKey, NodeId = nodeId };

    public bool IsDue(DateTimeOffset now, TimeSpan interval) => LastScanStartedAt is not { } started || started + interval <= now;

    /// <summary>Takes this interval's scan. The caller saves; a concurrency failure means another Worker took it.</summary>
    public bool TryStartScan(string nodeId, DateTimeOffset now, TimeSpan interval)
    {
        if (!IsDue(now, interval))
        {
            return false;
        }

        NodeId = nodeId;
        LastScanStartedAt = now;
        return true;
    }

    public void FinishScan(FolderScanStatus status, string? error, int filesFound, DateTimeOffset now)
    {
        LastScanAt = now;
        LastStatus = status;
        LastError = FolderLocation.Truncate(error, MaxErrorLength);
        LastFilesFound = filesFound;
    }
}

/// <summary>Claimed → Submitted (a run exists) → Delivering (outcome decided, file being moved) → Completed.</summary>
public enum FolderFileStatus
{
    Claimed,
    Submitted,
    Delivering,
    Completed,
}

public enum FolderFileOutcome
{
    /// <summary>The run succeeded; the file goes to the processed location.</summary>
    Processed,

    /// <summary>Not run, or the run didn't succeed; the file goes to the failed location with an error file beside it.</summary>
    Failed,
}

/// <summary>
/// One file picked up by a folder-input trigger: the durable ingestion record. Holds everything needed to finish the
/// file after a Worker restart (source, both destinations, the run), so recovery doesn't depend on the workflow's
/// current version. While a Worker works on it, it holds <see cref="LockedUntil"/>.
/// </summary>
public sealed class FolderFile
{
    public const int MaxErrorLength = 1000;

    private FolderFile() { } // EF Core

    public Guid Id { get; private set; }
    public Guid WorkflowId { get; private set; }
    public string WorkflowKey { get; private set; } = "";
    public string NodeId { get; private set; } = "";

    public string SourceLocation { get; private set; } = "";
    public string SourceSubfolder { get; private set; } = "";

    /// <summary>Path of the file below the scanned folder, with '/' separators (e.g. <c>2026/inv-1.json</c>).</summary>
    public string RelativePath { get; private set; } = "";

    public string FileName { get; private set; } = "";
    public long SizeBytes { get; private set; }
    public DateTimeOffset LastWriteAt { get; private set; }

    public string ProcessedLocation { get; private set; } = "";
    public string ProcessedSubfolder { get; private set; } = "";
    public string FailedLocation { get; private set; } = "";
    public string FailedSubfolder { get; private set; } = "";

    /// <summary>SHA-256 (hex) of the file's bytes, once read.</summary>
    public string? Sha256 { get; private set; }

    public FolderFileStatus Status { get; private set; }
    public FolderFileOutcome? Outcome { get; private set; }

    /// <summary>Why the file failed (e.g. <c>file.invalid_json</c>, <c>file.duplicate</c>, the run's failure code).</summary>
    public string? ErrorCode { get; private set; }

    /// <summary>Sanitized: never file contents or paths.</summary>
    public string? ErrorMessage { get; private set; }

    public Guid? ExecutionId { get; private set; }

    /// <summary>Name the file got in its destination (may carry a suffix when the name was taken).</summary>
    public string? DeliveredName { get; private set; }

    /// <summary>Last problem moving the file (destination unavailable…); retried on the next pass.</summary>
    public string? DeliveryError { get; private set; }

    public string ClaimedBy { get; private set; } = "";
    public DateTimeOffset ClaimedAt { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public bool IsLocked(DateTimeOffset now) => LockedUntil > now;

    public static FolderFile Claim(
        Guid workflowId, string workflowKey, string nodeId,
        string sourceLocation, string sourceSubfolder, string relativePath, string fileName, long sizeBytes, DateTimeOffset lastWriteAt,
        string processedLocation, string processedSubfolder, string failedLocation, string failedSubfolder,
        string worker, DateTimeOffset now, TimeSpan lease) => new()
        {
            Id = Guid.CreateVersion7(now),
            WorkflowId = workflowId,
            WorkflowKey = workflowKey,
            NodeId = nodeId,
            SourceLocation = sourceLocation,
            SourceSubfolder = sourceSubfolder,
            RelativePath = relativePath,
            FileName = fileName,
            SizeBytes = sizeBytes,
            LastWriteAt = lastWriteAt,
            ProcessedLocation = processedLocation,
            ProcessedSubfolder = processedSubfolder,
            FailedLocation = failedLocation,
            FailedSubfolder = failedSubfolder,
            Status = FolderFileStatus.Claimed,
            ClaimedBy = worker,
            ClaimedAt = now,
            LockedUntil = now + lease,
        };

    /// <summary>Takes the record for this Worker (recovery). The caller saves; a concurrency failure means someone else has it.</summary>
    public bool TryLock(DateTimeOffset now, TimeSpan lease)
    {
        if (Status == FolderFileStatus.Completed || IsLocked(now))
        {
            return false;
        }

        LockedUntil = now + lease;
        return true;
    }

    public void Read(string sha256, long sizeBytes)
    {
        EnsureStatus(FolderFileStatus.Claimed);
        Sha256 = sha256;
        SizeBytes = sizeBytes;
    }

    /// <summary>A run was created (or found again, after a restart). The file waits, unlocked, for the run to finish.</summary>
    public void Submitted(Guid executionId)
    {
        EnsureStatus(FolderFileStatus.Claimed);
        Status = FolderFileStatus.Submitted;
        ExecutionId = executionId;
        LockedUntil = null;
    }

    /// <summary>The outcome is decided: the file now goes to the processed or failed location.</summary>
    public void Route(FolderFileOutcome outcome, string? code, string? message)
    {
        if (Status is FolderFileStatus.Delivering or FolderFileStatus.Completed)
        {
            throw new DomainRuleException("folder_file.routed", $"File {Id} already has an outcome.");
        }

        Status = FolderFileStatus.Delivering;
        Outcome = outcome;
        ErrorCode = code;
        ErrorMessage = FolderLocation.Truncate(message, MaxErrorLength);
    }

    public (string Location, string Subfolder) Destination =>
        Outcome == FolderFileOutcome.Processed ? (ProcessedLocation, ProcessedSubfolder) : (FailedLocation, FailedSubfolder);

    public void Delivered(string name, DateTimeOffset now)
    {
        EnsureStatus(FolderFileStatus.Delivering);
        DeliveredName = name;
        DeliveryError = null;
        Complete(now);
    }

    /// <summary>Moving failed (e.g. the destination is unreachable); released so a later pass tries again.</summary>
    public void DeliveryFailed(string error)
    {
        EnsureStatus(FolderFileStatus.Delivering);
        DeliveryError = FolderLocation.Truncate(error, MaxErrorLength);
        LockedUntil = null;
    }

    /// <summary>The file disappeared before Verso could finish it (removed by someone else). Nothing left to move.</summary>
    public void Lost(string message, DateTimeOffset now)
    {
        if (Status == FolderFileStatus.Completed)
        {
            return;
        }

        Outcome ??= FolderFileOutcome.Failed;
        ErrorCode ??= "file.missing";
        ErrorMessage ??= FolderLocation.Truncate(message, MaxErrorLength);
        DeliveryError = FolderLocation.Truncate(message, MaxErrorLength);
        Complete(now);
    }

    private void Complete(DateTimeOffset now)
    {
        Status = FolderFileStatus.Completed;
        CompletedAt = now;
        LockedUntil = null;
    }

    private void EnsureStatus(FolderFileStatus expected)
    {
        if (Status != expected)
        {
            throw new DomainRuleException("folder_file.state", $"File {Id} is {Status}, not {expected}.");
        }
    }
}

public enum FolderAccessTestStatus
{
    Queued,
    Running,
    Completed,
    Failed,
}

/// <summary>
/// "Test access" for a folder-input step: requested through the API, run by the Worker under its own service
/// identity (the identity that will really read and move the files). Temporary: deleted after a day.
/// </summary>
public sealed class FolderAccessTest
{
    public const int MaxErrorLength = 500;

    private FolderAccessTest() { } // EF Core

    public FolderAccessTest(string targetsJson, string actor, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7(now);
        Targets = targetsJson;
        Status = FolderAccessTestStatus.Queued;
        RequestedBy = actor;
        RequestedAt = now;
    }

    public Guid Id { get; private set; }

    /// <summary>JSON array of <c>{ role, location, subfolder }</c>.</summary>
    public string Targets { get; private set; } = "[]";

    public FolderAccessTestStatus Status { get; private set; }

    /// <summary>JSON array of per-target results with their checks; set when completed.</summary>
    public string? Results { get; private set; }

    /// <summary>The account the Worker ran the checks as, and its machine.</summary>
    public string? Identity { get; private set; }

    public string? Machine { get; private set; }
    public string? Error { get; private set; }
    public string RequestedBy { get; private set; } = "";
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsFinished => Status is FolderAccessTestStatus.Completed or FolderAccessTestStatus.Failed;

    public void Start()
    {
        EnsureNotFinished();
        Status = FolderAccessTestStatus.Running;
    }

    public void Complete(string resultsJson, string identity, string machine, DateTimeOffset now)
    {
        EnsureNotFinished();
        Status = FolderAccessTestStatus.Completed;
        Results = resultsJson;
        Identity = identity;
        Machine = machine;
        CompletedAt = now;
    }

    public void Fail(string error, DateTimeOffset now)
    {
        EnsureNotFinished();
        Status = FolderAccessTestStatus.Failed;
        Error = FolderLocation.Truncate(error, MaxErrorLength);
        CompletedAt = now;
    }

    private void EnsureNotFinished()
    {
        if (IsFinished)
        {
            throw new DomainRuleException("folder_access_test.finished", $"Access test {Id} is already {Status}.");
        }
    }
}
