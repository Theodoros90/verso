using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Verso.Application.Workflows;
using Verso.Domain;
using Verso.Domain.Audit;
using Verso.Domain.Documents;
using Verso.Domain.Executions;
using Verso.Domain.Templates;
using Verso.Domain.Workflows;

namespace Verso.Application.Executions;

/// <summary>
/// Executions as the API sees them: submit (idempotent), read, retry. Submitting commits the execution row, its
/// steps, the Submitted event and the audit entry in one transaction, then queues the job. If queueing fails the
/// row stays Queued and <see cref="ExecutionDispatcher"/> queues it later (transactional outbox; plan §6.2 as
/// revised in M7), so there is never a row without a job, and a job without a row does nothing.
/// </summary>
public sealed partial class ExecutionService(IVersoDbContext db, IExecutionJobs jobs, ICurrentUser user, TimeProvider clock, ILogger<ExecutionService> logger)
{
    public const string EntityType = "Execution";

    /// <summary>1,000,000 characters of JSON (plan §7: about 1 MB).</summary>
    public const int MaxDataChars = 1_000_000;

    public const int MaxDataDepth = 32;

    public static class Actions
    {
        public const string Submitted = "ExecutionSubmitted";
        public const string RetryRequested = "ExecutionRetryRequested";
        public const string Completed = "ExecutionCompleted";
    }

    [GeneratedRegex(@"^[\x21-\x7E]{1,100}$")]
    private static partial Regex IdempotencyKeyPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._:\-]{1,64}$")]
    private static partial Regex CorrelationIdPattern();

    public async Task<Result<Submission>> SubmitAsync(
        string workflowKey, string? idempotencyKey, string? correlationId, SubmitExecutionRequest? request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(idempotencyKey))
        {
            return Invalid("execution.idempotency_key_required", "Idempotency-Key", "Required: a unique value per submission (e.g. a UUID). Resending the same key returns the original execution.");
        }

        if (!IdempotencyKeyPattern().IsMatch(idempotencyKey))
        {
            return Invalid("validation", "Idempotency-Key", "1–100 visible ASCII characters, no spaces.");
        }

        if (correlationId is not null && !CorrelationIdPattern().IsMatch(correlationId))
        {
            return Invalid("validation", "X-Correlation-Id", "1–64 characters: letters, digits, '.', '_', ':' or '-'.");
        }

        if (request?.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            return Invalid("validation", "data", "Required: a JSON object (synthetic data only).");
        }

        var payload = data.GetRawText();
        if (payload.Length > MaxDataChars)
        {
            return Invalid("validation", "data", $"At most {MaxDataChars:N0} characters.");
        }

        if (Depth(data) > MaxDataDepth)
        {
            return Invalid("validation", "data", $"Nested at most {MaxDataDepth} levels deep.");
        }

        var workflow = await db.Workflows.AsNoTracking().Include(w => w.Versions).SingleOrDefaultAsync(w => w.Key == workflowKey, ct);
        if (workflow is null)
        {
            return AppError.NotFound("workflow.not_found", $"Workflow '{workflowKey}' was not found.");
        }

        var inputHash = CanonicalJson.Sha256Hex(data);
        if (await FindReplayAsync(workflow.Id, idempotencyKey, inputHash, ct) is { } replay)
        {
            return replay;
        }

        if (workflow.LatestPublished is not { } version)
        {
            return AppError.Conflict("workflow.not_published", $"Workflow '{workflowKey}' has no published version. Publish it before submitting data.");
        }

        var plan = ExecutionPlan.Create(WorkflowDefinitionParser.Parse(version.Definition).Definition
            ?? throw new InvalidOperationException($"Published workflow version {version.Id} has an invalid definition."));
        var templates = await ResolveTemplatesAsync(plan, ct);
        if (templates.Error is not null)
        {
            return templates.Error;
        }

        var now = clock.GetUtcNow();
        var execution = Execution.Submit(
            workflow.Id, workflow.Key, version.Id, version.VersionNumber!.Value, JsonSerializer.Serialize(templates.Value),
            idempotencyKey, correlationId ?? Guid.NewGuid().ToString("N"), payload, inputHash, plan.Steps, user.Id, now);
        db.Executions.Add(execution);
        db.ExecutionEvents.Add(ExecutionEvent.Record(execution.Id, ExecutionEventTypes.Submitted, now,
            data: JsonSerializer.Serialize(new { workflowVersion = execution.WorkflowVersionNumber })));
        Audit(Actions.Submitted, execution, now, new
        {
            workflowKey = execution.WorkflowKey,
            workflowVersion = execution.WorkflowVersionNumber,
            inputHash,
            correlationId = execution.CorrelationId,
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent request with the same key won the race (unique index): answer as a replay of it.
            if (await FindReplayAsync(workflow.Id, idempotencyKey, inputHash, CancellationToken.None) is { } concurrent)
            {
                return concurrent;
            }

            throw;
        }

        Dispatch(execution.Id);
        return new Submission(ToSubmission(execution), Created: true);
    }

    public async Task<Result<ExecutionDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var execution = await db.Executions.AsNoTracking().Include(e => e.Steps).SingleOrDefaultAsync(e => e.Id == id, ct);
        if (execution is null)
        {
            return NotFound(id);
        }

        var documents = await db.Documents.AsNoTracking().Where(d => d.ExecutionId == id).OrderBy(d => d.CreatedAt).ToListAsync(ct);
        var documentIds = documents.Select(d => d.Id).ToList();
        var archives = documentIds.Count == 0
            ? []
            : await db.ArchiveRecords.AsNoTracking().Where(a => documentIds.Contains(a.DocumentId)).ToDictionaryAsync(a => a.DocumentId, ct);
        return ToDto(execution, documents, archives);
    }

    /// <summary>Operator retry of a Failed execution: back to Queued, resuming at the failed step.</summary>
    public async Task<Result<ExecutionDto>> RetryAsync(Guid id, CancellationToken ct)
    {
        var execution = await db.Executions.Include(e => e.Steps).SingleOrDefaultAsync(e => e.Id == id, ct);
        if (execution is null)
        {
            return NotFound(id);
        }

        var now = clock.GetUtcNow();
        try
        {
            execution.Retry(now);
        }
        catch (DomainRuleException ex)
        {
            return AppError.Conflict(ex.Code, ex.Message);
        }

        db.ExecutionEvents.Add(ExecutionEvent.Record(execution.Id, ExecutionEventTypes.RetryRequested, now,
            data: JsonSerializer.Serialize(new { attempt = execution.Attempt })));
        Audit(Actions.RetryRequested, execution, now, new { attempt = execution.Attempt });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return AppError.Conflict("execution.changed", "The execution changed while retrying; read it again.");
        }

        Dispatch(execution.Id);
        return await GetAsync(id, ct);
    }

    /// <summary>Best effort: on failure the dispatcher queues the job later, because the row is already committed.</summary>
    private void Dispatch(Guid executionId)
    {
        try
        {
            jobs.Enqueue(executionId);
        }
#pragma warning disable CA1031 // any queueing failure is recovered by the dispatcher
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogEnqueueFailed(logger, executionId, ex);
        }
    }

    private async Task<Result<Submission>?> FindReplayAsync(Guid workflowId, string idempotencyKey, string inputHash, CancellationToken ct)
    {
        var existing = await db.Executions.AsNoTracking()
            .Where(e => e.WorkflowId == workflowId && e.IdempotencyKey == idempotencyKey)
            .Select(e => new { e.Id, e.Status, e.WorkflowKey, e.WorkflowVersionNumber, e.CorrelationId, e.InputHash })
            .SingleOrDefaultAsync(ct);
        if (existing is null)
        {
            return null;
        }

        if (existing.InputHash != inputHash)
        {
            return new AppError(ErrorKind.Conflict, "execution.idempotency_conflict",
                "This Idempotency-Key was already used with different data. Use a new key for a new submission.",
                Extensions: new Dictionary<string, object?> { ["executionId"] = existing.Id });
        }

        return new Submission(new SubmissionDto(existing.Id, existing.Status.ToString(), existing.WorkflowKey, existing.WorkflowVersionNumber, existing.CorrelationId), Created: false);
    }

    /// <summary>Pins every generate-pdf node to a template version id (latest-published is resolved now).</summary>
    private async Task<(Dictionary<string, Guid> Value, AppError? Error)> ResolveTemplatesAsync(ExecutionPlan plan, CancellationToken ct)
    {
        var resolved = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var node in plan.Ordered.Where(n => n.Type == NodeCatalog.GeneratePdf))
        {
            var templateId = Guid.Parse(node.Config.GetProperty("templateId").GetString()!);
            var requested = node.Config.GetProperty("templateVersion").GetString();
            var published = db.TemplateVersions.AsNoTracking()
                .Where(v => v.TemplateId == templateId && v.Status == TemplateVersionStatus.Published);
            var versionId = requested == NodeCatalog.LatestPublished
                ? await published.OrderByDescending(v => v.VersionNumber).Select(v => (Guid?)v.Id).FirstOrDefaultAsync(ct)
                : await published.Where(v => v.VersionNumber == int.Parse(requested!, System.Globalization.CultureInfo.InvariantCulture)).Select(v => (Guid?)v.Id).FirstOrDefaultAsync(ct);
            if (versionId is null)
            {
                return ([], AppError.Conflict("workflow.template_unavailable", $"Step '{node.Title}' refers to a template version that isn't published."));
            }

            resolved[node.Id] = versionId.Value;
        }

        return (resolved, null);
    }

    private void Audit(string action, Execution execution, DateTimeOffset now, object details) =>
        db.AuditEntries.Add(AuditEntry.Record(
            action, EntityType, execution.Id.ToString(), user.Id, user.Name, now,
            JsonSerializer.Serialize(details), Activity.Current?.TraceId.ToHexString()));

    private static int Depth(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => 1 + element.EnumerateObject().Select(p => Depth(p.Value)).DefaultIfEmpty(0).Max(),
        JsonValueKind.Array => 1 + element.EnumerateArray().Select(Depth).DefaultIfEmpty(0).Max(),
        _ => 0,
    };

    private static AppError Invalid(string code, string field, string message) =>
        new(ErrorKind.Validation, code, message, new Dictionary<string, string[]> { [field] = [message] });

    private static AppError NotFound(Guid id) => AppError.NotFound("execution.not_found", $"Execution {id} was not found.");

    internal static SubmissionDto ToSubmission(Execution e) =>
        new(e.Id, e.Status.ToString(), e.WorkflowKey, e.WorkflowVersionNumber, e.CorrelationId);

    internal static ExecutionDto ToDto(Execution e, IReadOnlyList<Document> documents, IReadOnlyDictionary<Guid, ArchiveRecord> archives) => new(
        e.Id,
        e.Status.ToString(),
        new ExecutionWorkflowDto(e.WorkflowId, e.WorkflowKey, e.WorkflowVersionNumber, e.WorkflowVersionId),
        JsonSerializer.Deserialize<Dictionary<string, Guid>>(e.ResolvedTemplateVersions) ?? [],
        e.IdempotencyKey,
        e.CorrelationId,
        e.InputHash,
        e.SubmittedAt,
        e.SubmittedBy,
        e.StartedAt,
        e.CompletedAt,
        e.Attempt,
        e.FailureCode,
        e.FailureMessage,
        [.. e.Steps.OrderBy(s => s.Sequence).Select(s => new ExecutionStepDto(
            s.NodeId, s.NodeType, s.Title, s.Sequence, s.Status.ToString(), s.OutputPort, s.Attempt,
            s.StartedAt, s.CompletedAt, s.DurationMs, s.Error, ParseResult(s.Result)))],
        [.. documents.Select(d => new ExecutionDocumentDto(
            d.Id, d.NodeId, d.TemplateVersionId, d.FileName, d.ContentType, d.SizeBytes, d.Sha256, d.Status.ToString(), d.CreatedAt,
            archives.TryGetValue(d.Id, out var a)
                ? new ArchiveDto(a.ArchivedAt, a.FolderPath, a.Retention, a.RetentionUntil, JsonSerializer.Deserialize<Dictionary<string, string>>(a.Metadata) ?? [])
                : null))]);

    private static JsonElement? ParseResult(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Queueing execution {ExecutionId} failed; the dispatcher will queue it")]
    private static partial void LogEnqueueFailed(ILogger logger, Guid executionId, Exception exception);
}
