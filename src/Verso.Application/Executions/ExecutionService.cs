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
        public const string InputViewed = "ExecutionInputViewed";
        public const string ApprovalRequested = "ExecutionApprovalRequested";
        public const string ApprovalDecided = "ExecutionApprovalDecided";
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

    /// <summary>
    /// The submitted payload. It may hold personal data, so only Auditor/Admin read it (plan §7); each read is audited.
    /// The role is checked before anything else, so the response doesn't reveal whether the execution exists.
    /// </summary>
    public async Task<Result<ExecutionInputDto>> InputAsync(Guid id, CancellationToken ct)
    {
        if (!user.CanReadPayloads())
        {
            return new AppError(ErrorKind.Forbidden, "forbidden", "Reading submitted data requires the Auditor or Admin role.");
        }

        var row = await db.Executions.AsNoTracking().Where(e => e.Id == id)
            .Select(e => new { e.InputPayload, e.InputHash, e.WorkflowKey }).SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return NotFound(id);
        }

        var now = clock.GetUtcNow();
        db.AuditEntries.Add(AuditEntry.Record(
            Actions.InputViewed, EntityType, id.ToString(), user.Id, user.Name, now,
            JsonSerializer.Serialize(new { inputHash = row.InputHash }), Activity.Current?.TraceId.ToHexString()));
        await db.SaveChangesAsync(ct);

        using var document = JsonDocument.Parse(row.InputPayload);
        return new ExecutionInputDto(id, row.InputHash, document.RootElement.Clone());
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

    /// <summary>
    /// Approves or declines the Approval step that is waiting in an execution, then queues the run to continue. Only a person holding
    /// the step's approver role (or an administrator) may decide. The decision, who made it and the comment go on the step and the audit log.
    /// </summary>
    public async Task<Result<ExecutionDto>> DecideAsync(Guid id, string nodeId, DecideApprovalRequest? request, CancellationToken ct)
    {
        var decision = request?.Decision?.Trim().ToLowerInvariant();
        if (decision is not ("approved" or "declined"))
        {
            return Invalid("validation", "decision", "One of: approved, declined.");
        }

        var comment = request?.Comment?.Trim();
        if (comment is { Length: > Approval.MaxCommentLength })
        {
            return Invalid("validation", "comment", $"At most {Approval.MaxCommentLength} characters.");
        }

        var execution = await db.Executions.Include(e => e.Steps).SingleOrDefaultAsync(e => e.Id == id, ct);
        if (execution is null)
        {
            return NotFound(id);
        }

        var iteration = request?.Iteration ?? 0;
        var step = execution.Steps.SingleOrDefault(s => s.NodeId == nodeId && s.Iteration == iteration && s.NodeType == NodeCatalog.ApprovalStep);
        if (step is null)
        {
            return AppError.NotFound("approval.not_found", $"Execution {id} has no approval step '{nodeId}'.");
        }

        if (execution.Status != ExecutionStatus.AwaitingApproval || step.Status != StepStatus.Waiting)
        {
            return AppError.Conflict("approval.not_waiting", "This approval isn't waiting for a decision (it may already have been decided).");
        }

        var waiting = System.Text.Json.Nodes.JsonNode.Parse(step.Result ?? "{}")!.AsObject();
        var approver = waiting["approver"]?.GetValue<string>() ?? Approval.DefaultApprover;
        if (!Approval.CanDecide(user, approver))
        {
            var label = Approval.Approvers.TryGetValue(approver, out var entry) ? entry.Label.ToLowerInvariant() : approver;
            return new AppError(ErrorKind.Forbidden, "forbidden", $"This approval is for {label} (or an administrator).");
        }

        var now = clock.GetUtcNow();
        waiting["decision"] = decision;
        waiting["decidedBy"] = user.Id;
        waiting["decidedByName"] = user.Name;
        waiting["decidedAt"] = now;
        if (!string.IsNullOrEmpty(comment))
        {
            waiting["comment"] = comment;
        }

        var port = decision == "approved" ? Approval.Approved : Approval.Rejected;
        try
        {
            execution.Decide(nodeId, iteration, port, waiting.ToJsonString(), now);
        }
        catch (DomainRuleException ex)
        {
            return AppError.Conflict(ex.Code, ex.Message);
        }

        db.ExecutionEvents.Add(ExecutionEvent.Record(execution.Id, ExecutionEventTypes.ApprovalDecided, now, nodeId,
            JsonSerializer.Serialize(new { decision, decidedBy = user.Name, iteration = iteration > 0 ? iteration : (int?)null })));
        Audit(Actions.ApprovalDecided, execution, now, new { nodeId, decision, approver, iteration, hasComment = !string.IsNullOrEmpty(comment) });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return AppError.Conflict("execution.changed", "The execution changed while deciding; read it again.");
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
        foreach (var node in plan.Ordered.Where(n => NodeCatalog.IsDocumentStep(n.Type)))
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
            s.StartedAt, s.CompletedAt, s.DurationMs, s.Error, ParseResult(s.Result), s.Iteration))],
        [.. documents.Select(d => new ExecutionDocumentDto(
            d.Id, d.NodeId, d.TemplateVersionId, d.FileName, d.ContentType, d.SizeBytes, d.Sha256, d.Status.ToString(), d.CreatedAt,
            archives.TryGetValue(d.Id, out var a)
                ? new ArchiveDto(a.ArchivedAt, a.FolderPath, a.Retention, a.RetentionUntil, JsonSerializer.Deserialize<Dictionary<string, string>>(a.Metadata) ?? [])
                : null,
            d.Iteration))]);

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
