using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Verso.Application.Templates;
using Verso.Domain.Executions;

namespace Verso.Application.Executions;

public sealed record ExecutionSummaryDto(
    Guid Id, string Status, string WorkflowKey, int WorkflowVersion, string CorrelationId,
    DateTimeOffset SubmittedAt, DateTimeOffset? CompletedAt, int Attempt, string? FailureCode, string? FailureMessage);

public sealed record ExecutionEventDto(long Id, Guid ExecutionId, string? NodeId, string Type, DateTimeOffset OccurredAt, JsonElement? Data);

/// <summary>An event as the API's relay needs it: with the execution's workflow and current status.</summary>
public sealed record RelayEvent(
    long Id, Guid ExecutionId, Guid WorkflowId, string Type, string? NodeId, string? Data, string ExecutionStatus);

public sealed record ExecutionFilter(string? Workflow, string? Status, DateTimeOffset? From, DateTimeOffset? To, string? CorrelationId);

/// <summary>Read side of executions: history list, event timeline, and the feed the live-progress relay polls.</summary>
public sealed class ExecutionQueries(IVersoDbContext db)
{
    public const int MaxEventsPerRead = 500;

    public async Task<Result<PageResult<ExecutionSummaryDto>>> ListAsync(ExecutionFilter filter, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Executions.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            if (!Enum.TryParse<ExecutionStatus>(filter.Status, ignoreCase: true, out var status) || !Enum.IsDefined(status))
            {
                return AppError.Validation(new Dictionary<string, string[]>
                {
                    ["status"] = [$"One of: {string.Join(", ", Enum.GetNames<ExecutionStatus>())}."],
                });
            }

            query = query.Where(e => e.Status == status);
        }

        if (filter.From is { } from && filter.To is { } to && to <= from)
        {
            return AppError.Validation(new Dictionary<string, string[]> { ["to"] = ["Must be after 'from'."] });
        }

        if (!string.IsNullOrWhiteSpace(filter.Workflow))
        {
            query = query.Where(e => e.WorkflowKey == filter.Workflow);
        }

        if (!string.IsNullOrWhiteSpace(filter.CorrelationId))
        {
            query = query.Where(e => e.CorrelationId == filter.CorrelationId);
        }

        if (filter.From is { } submittedFrom)
        {
            query = query.Where(e => e.SubmittedAt >= submittedFrom);
        }

        if (filter.To is { } submittedBefore)
        {
            query = query.Where(e => e.SubmittedAt < submittedBefore);
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(e => e.SubmittedAt).ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(e => new ExecutionSummaryDto(
                e.Id, e.Status.ToString(), e.WorkflowKey, e.WorkflowVersionNumber, e.CorrelationId,
                e.SubmittedAt, e.CompletedAt, e.Attempt, e.FailureCode, e.FailureMessage))
            .ToListAsync(ct);
        return new PageResult<ExecutionSummaryDto>(rows, page, pageSize, total);
    }

    /// <summary>Events of one execution after the given id (0 = from the start), oldest first.</summary>
    public async Task<Result<IReadOnlyList<ExecutionEventDto>>> EventsAsync(Guid executionId, long after, int limit, CancellationToken ct)
    {
        if (!await db.Executions.AsNoTracking().AnyAsync(e => e.Id == executionId, ct))
        {
            return AppError.NotFound("execution.not_found", $"Execution {executionId} was not found.");
        }

        var rows = await db.ExecutionEvents.AsNoTracking()
            .Where(e => e.ExecutionId == executionId && e.Id > Math.Max(0, after))
            .OrderBy(e => e.Id)
            .Take(Math.Clamp(limit, 1, MaxEventsPerRead))
            .ToListAsync(ct);
        IReadOnlyList<ExecutionEventDto> events = [.. rows.Select(e => new ExecutionEventDto(e.Id, e.ExecutionId, e.NodeId, e.Type, e.OccurredAt, ParseData(e.Data)))];
        return Result.Success(events);
    }

    /// <summary>The highest event id so far (0 when there are none): where a new relay starts.</summary>
    public async Task<long> LatestEventIdAsync(CancellationToken ct) =>
        await db.ExecutionEvents.AsNoTracking().MaxAsync(e => (long?)e.Id, ct) ?? 0;

    /// <summary>Events with an id above <paramref name="afterId"/>, oldest first, each with its execution's workflow and status.</summary>
    public async Task<IReadOnlyList<RelayEvent>> RelayWindowAsync(long afterId, int max, CancellationToken ct) =>
        await (from ev in db.ExecutionEvents.AsNoTracking()
               where ev.Id > afterId
               join ex in db.Executions.AsNoTracking() on ev.ExecutionId equals ex.Id
               orderby ev.Id
               select new RelayEvent(ev.Id, ev.ExecutionId, ex.WorkflowId, ev.Type, ev.NodeId, ev.Data, ex.Status.ToString()))
            .Take(max)
            .ToListAsync(ct);

    private static JsonElement? ParseData(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
