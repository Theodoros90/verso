namespace Verso.Domain.Executions;

/// <summary>Plan §6.1. Rejected = the data failed validation (not retryable); Failed = a technical error (retryable).</summary>
public enum ExecutionStatus
{
    Queued,
    Running,
    Succeeded,
    Rejected,
    Failed,
}

public enum StepStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped,
}

/// <summary>A step to create when a run is submitted: one per workflow node, in run order.</summary>
public sealed record PlannedStep(string NodeId, string NodeType, string Title);

/// <summary>
/// One run of a published workflow version with one submitted payload. Records the exact workflow and template
/// versions it uses. The Worker that runs it holds a short lock (<see cref="LockedUntil"/>), so two deliveries of
/// the same job never run it at the same time; <see cref="DispatchedAt"/> lets the dispatcher find runs whose job
/// went missing.
/// </summary>
public sealed class Execution
{
    public const int MaxFailureMessageLength = 1000;

    private readonly List<ExecutionStep> _steps = [];

    private Execution() { } // EF Core

    public Guid Id { get; private set; }
    public Guid WorkflowId { get; private set; }
    public string WorkflowKey { get; private set; } = "";
    public Guid WorkflowVersionId { get; private set; }
    public int WorkflowVersionNumber { get; private set; }

    /// <summary>JSON object: generate-pdf node id → template version id, resolved at submission.</summary>
    public string ResolvedTemplateVersions { get; private set; } = "{}";

    public ExecutionStatus Status { get; private set; }
    public string IdempotencyKey { get; private set; } = "";
    public string CorrelationId { get; private set; } = "";

    /// <summary>The submitted data (synthetic only). Never logged, never copied into results or messages.</summary>
    public string InputPayload { get; private set; } = "";

    /// <summary>SHA-256 (hex) of the canonical input payload; decides idempotent replays.</summary>
    public string InputHash { get; private set; } = "";

    public DateTimeOffset SubmittedAt { get; private set; }
    public string SubmittedBy { get; private set; } = "";
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>1 for the first run; increased by each operator retry.</summary>
    public int Attempt { get; private set; }

    public string? FailureCode { get; private set; }

    /// <summary>Sanitized: never contains payload values.</summary>
    public string? FailureMessage { get; private set; }

    /// <summary>A Worker is running this execution until then (renewed as it progresses). Null: nobody is.</summary>
    public DateTimeOffset? LockedUntil { get; private set; }

    /// <summary>When a job was last queued for this execution, or when a scheduled retry is due.</summary>
    public DateTimeOffset DispatchedAt { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyList<ExecutionStep> Steps => _steps;

    public bool IsFinished => Status is ExecutionStatus.Succeeded or ExecutionStatus.Rejected or ExecutionStatus.Failed;

    public static Execution Submit(
        Guid workflowId, string workflowKey, Guid workflowVersionId, int workflowVersionNumber, string resolvedTemplateVersions,
        string idempotencyKey, string correlationId, string inputPayload, string inputHash,
        IReadOnlyList<PlannedStep> steps, string actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var execution = new Execution
        {
            Id = Guid.CreateVersion7(now),
            WorkflowId = workflowId,
            WorkflowKey = workflowKey,
            WorkflowVersionId = workflowVersionId,
            WorkflowVersionNumber = workflowVersionNumber,
            ResolvedTemplateVersions = resolvedTemplateVersions,
            Status = ExecutionStatus.Queued,
            IdempotencyKey = idempotencyKey,
            CorrelationId = correlationId,
            InputPayload = inputPayload,
            InputHash = inputHash,
            SubmittedAt = now,
            SubmittedBy = actor,
            Attempt = 1,
            DispatchedAt = now,
        };
        var sequence = 1;
        foreach (var step in steps)
        {
            execution._steps.Add(new ExecutionStep(execution.Id, step, sequence++, now));
        }

        return execution;
    }

    public ExecutionStep Step(string nodeId) =>
        _steps.SingleOrDefault(s => s.NodeId == nodeId)
        ?? throw new DomainRuleException("execution.unknown_step", $"Execution {Id} has no step for node '{nodeId}'.");

    /// <summary>
    /// Takes the lock for a Worker run. False when the execution is finished or another run holds a live lock.
    /// The first claim moves it from Queued to Running.
    /// </summary>
    public bool TryClaim(DateTimeOffset now, TimeSpan lease)
    {
        if (IsFinished || LockedUntil > now)
        {
            return false;
        }

        Status = ExecutionStatus.Running;
        StartedAt ??= now;
        LockedUntil = now + lease;
        return true;
    }

    /// <summary>Extends the lock while the run makes progress.</summary>
    public void RenewLock(DateTimeOffset now, TimeSpan lease)
    {
        EnsureNotFinished();
        LockedUntil = now + lease;
    }

    /// <summary>The Worker is stopping: release the lock so the redelivered job can continue at once.</summary>
    public void ReleaseLock() => LockedUntil = null;

    /// <summary>A step failed technically and will be retried at <paramref name="due"/> (by a scheduled job).</summary>
    public void ScheduleRetry(DateTimeOffset due)
    {
        EnsureNotFinished();
        LockedUntil = null;
        DispatchedAt = due;
    }

    public void MarkDispatched(DateTimeOffset now)
    {
        EnsureNotFinished();
        DispatchedAt = now;
    }

    public void Succeed(DateTimeOffset now) => Finish(ExecutionStatus.Succeeded, null, null, now, skipPending: true);

    public void Reject(string code, string message, DateTimeOffset now) => Finish(ExecutionStatus.Rejected, code, message, now, skipPending: true);

    /// <summary>Technical failure. Steps that didn't run stay Pending: an operator retry can still run them.</summary>
    public void Fail(string code, string message, DateTimeOffset now) => Finish(ExecutionStatus.Failed, code, message, now, skipPending: false);

    /// <summary>Operator retry (plan §6.1: Failed → Queued). Resumes at the failed step with a fresh attempt budget.</summary>
    public void Retry(DateTimeOffset now)
    {
        if (Status != ExecutionStatus.Failed)
        {
            throw new DomainRuleException(
                "execution.not_retryable",
                Status == ExecutionStatus.Rejected
                    ? "Rejected executions failed data validation; retrying can't change that. Submit corrected data instead."
                    : $"Only failed executions can be retried; this one is {Status}.");
        }

        Status = ExecutionStatus.Queued;
        Attempt++;
        FailureCode = null;
        FailureMessage = null;
        CompletedAt = null;
        LockedUntil = null;
        DispatchedAt = now;
        foreach (var step in _steps.Where(s => s.Status is StepStatus.Failed or StepStatus.Running))
        {
            step.ResetForRetry();
        }
    }

    private void Finish(ExecutionStatus status, string? code, string? message, DateTimeOffset now, bool skipPending)
    {
        EnsureNotFinished();
        Status = status;
        FailureCode = code;
        FailureMessage = message is { Length: > MaxFailureMessageLength } ? message[..MaxFailureMessageLength] : message;
        CompletedAt = now;
        LockedUntil = null;
        if (skipPending)
        {
            foreach (var step in _steps.Where(s => s.Status == StepStatus.Pending))
            {
                step.Skip();
            }
        }
    }

    private void EnsureNotFinished()
    {
        if (IsFinished)
        {
            throw new DomainRuleException("execution.finished", $"Execution {Id} is already {Status}.");
        }
    }
}

/// <summary>One node of the workflow within an execution.</summary>
public sealed class ExecutionStep
{
    public const int MaxErrorLength = 1000;

    private ExecutionStep() { } // EF Core

    internal ExecutionStep(Guid executionId, PlannedStep planned, int sequence, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7(now);
        ExecutionId = executionId;
        NodeId = planned.NodeId;
        NodeType = planned.NodeType;
        Title = planned.Title;
        Sequence = sequence;
        Status = StepStatus.Pending;
    }

    public Guid Id { get; private set; }
    public Guid ExecutionId { get; private set; }
    public string NodeId { get; private set; } = "";
    public string NodeType { get; private set; } = "";
    public string Title { get; private set; } = "";
    public int Sequence { get; private set; }
    public StepStatus Status { get; private set; }

    /// <summary>The output it left by (<c>valid</c>, <c>invalid</c>, <c>success</c>, <c>failed</c>, <c>out</c>).</summary>
    public string? OutputPort { get; private set; }

    /// <summary>Attempts within the current execution attempt (reset by an operator retry).</summary>
    public int Attempt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public long? DurationMs { get; private set; }

    /// <summary>Small JSON summary (validation issues, document id…). Never the payload.</summary>
    public string? Result { get; private set; }

    /// <summary>Last technical error, sanitized.</summary>
    public string? Error { get; private set; }

    public void Start(DateTimeOffset now)
    {
        if (Status is StepStatus.Succeeded or StepStatus.Skipped)
        {
            throw new DomainRuleException("step.finished", $"Step '{NodeId}' is already {Status}.");
        }

        Status = StepStatus.Running;
        Attempt++;
        StartedAt = now;
        CompletedAt = null;
        DurationMs = null;
        Error = null;
    }

    /// <param name="outputPort">Null for steps without outputs (Archive): the path ends there.</param>
    public void Succeed(string? outputPort, string? resultJson, DateTimeOffset now)
    {
        EnsureRunning();
        Status = StepStatus.Succeeded;
        OutputPort = outputPort;
        Result = resultJson;
        Complete(now);
    }

    public void Fail(string error, DateTimeOffset now)
    {
        EnsureRunning();
        Status = StepStatus.Failed;
        Error = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        Complete(now);
    }

    /// <summary>The Worker stopped mid-step: not the step's fault, so the attempt isn't counted.</summary>
    public void Interrupt()
    {
        EnsureRunning();
        Status = StepStatus.Pending;
        Attempt--;
        StartedAt = null;
    }

    internal void ResetForRetry()
    {
        Status = StepStatus.Pending;
        Attempt = 0;
    }

    internal void Skip() => Status = StepStatus.Skipped;

    private void Complete(DateTimeOffset now)
    {
        CompletedAt = now;
        DurationMs = StartedAt is { } started ? (long)Math.Max(0, (now - started).TotalMilliseconds) : null;
    }

    private void EnsureRunning()
    {
        if (Status != StepStatus.Running)
        {
            throw new DomainRuleException("step.not_running", $"Step '{NodeId}' is {Status}, not running.");
        }
    }
}

/// <summary>
/// Append-only progress record. <see cref="Id"/> is a database identity: strictly increasing, so clients and the
/// relay (M8) can catch up with "events after id N".
/// </summary>
public sealed class ExecutionEvent
{
    private ExecutionEvent() { } // EF Core

    public long Id { get; private set; }
    public Guid ExecutionId { get; private set; }
    public string? NodeId { get; private set; }
    public string Type { get; private set; } = "";
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>Small JSON object (status, port, attempt, code). Never payload values.</summary>
    public string? Data { get; private set; }

    public static ExecutionEvent Record(Guid executionId, string type, DateTimeOffset now, string? nodeId = null, string? data = null) => new()
    {
        ExecutionId = executionId,
        Type = type,
        OccurredAt = now,
        NodeId = nodeId,
        Data = data,
    };
}

/// <summary>Event types written to <see cref="ExecutionEvent.Type"/>.</summary>
public static class ExecutionEventTypes
{
    public const string Submitted = "Submitted";
    public const string Started = "Started";
    public const string StepStarted = "StepStarted";
    public const string StepSucceeded = "StepSucceeded";
    public const string StepFailed = "StepFailed";
    public const string RetryScheduled = "RetryScheduled";
    public const string Succeeded = "Succeeded";
    public const string Rejected = "Rejected";
    public const string Failed = "Failed";
    public const string RetryRequested = "RetryRequested";
    public const string Redispatched = "Redispatched";
}
