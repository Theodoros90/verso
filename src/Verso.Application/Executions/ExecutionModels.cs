using System.Text.Json;

namespace Verso.Application.Executions;

// Request and response shapes of the executions API (docs/api.md#executions).

/// <summary>Body of a submission: <c>{ "data": { … } }</c>, synthetic data only.</summary>
public sealed record SubmitExecutionRequest(JsonElement? Data);

/// <summary>Response of a submission (202 for a new run, 200 for an idempotent replay).</summary>
public sealed record SubmissionDto(Guid ExecutionId, string Status, string WorkflowKey, int WorkflowVersion, string CorrelationId);

/// <summary>A submission result: the run, and whether this request created it.</summary>
public sealed record Submission(SubmissionDto Execution, bool Created);

public sealed record ExecutionWorkflowDto(Guid Id, string Key, int Version, Guid VersionId);

public sealed record ExecutionStepDto(
    string NodeId, string Type, string Title, int Sequence, string Status, string? OutputPort, int Attempt,
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, long? DurationMs, string? Error, JsonElement? Result);

public sealed record ArchiveDto(
    DateTimeOffset ArchivedAt, string FolderPath, string Retention, DateTimeOffset? RetentionUntil, IReadOnlyDictionary<string, string> Metadata);

public sealed record ExecutionDocumentDto(
    Guid Id, string NodeId, Guid TemplateVersionId, string FileName, string ContentType, long SizeBytes, string Sha256,
    string Status, DateTimeOffset CreatedAt, ArchiveDto? Archive);

public sealed record ExecutionDto(
    Guid Id,
    string Status,
    ExecutionWorkflowDto Workflow,
    IReadOnlyDictionary<string, Guid> TemplateVersions,
    string IdempotencyKey,
    string CorrelationId,
    string InputHash,
    DateTimeOffset SubmittedAt,
    string SubmittedBy,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int Attempt,
    string? FailureCode,
    string? FailureMessage,
    IReadOnlyList<ExecutionStepDto> Steps,
    IReadOnlyList<ExecutionDocumentDto> Documents);

/// <summary>Queues execution jobs. Implemented with Hangfire in Infrastructure; the Worker runs them.</summary>
public interface IExecutionJobs
{
    void Enqueue(Guid executionId);

    void Schedule(Guid executionId, TimeSpan delay);
}
