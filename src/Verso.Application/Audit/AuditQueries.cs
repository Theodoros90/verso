using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Verso.Application.Templates;

namespace Verso.Application.Audit;

public sealed record AuditEntryDto(
    Guid Id, long Sequence, DateTimeOffset OccurredAt, string ActorId, string ActorName,
    string Action, string EntityType, string EntityId, JsonElement? Details, string? TraceId);

/// <summary>Filters for the audit trail; all optional. <see cref="From"/> is inclusive, <see cref="To"/> exclusive.</summary>
public sealed record AuditFilter(
    string? EntityType = null, string? EntityId = null, string? Action = null, string? ActorId = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, bool NewestFirst = false);

/// <summary>Read side of the audit log (append-only). Oldest first by default; the audit screen asks for newest first.</summary>
public sealed class AuditQueries(IVersoDbContext db)
{
    public async Task<Result<PageResult<AuditEntryDto>>> ListAsync(AuditFilter filter, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        if (filter.From is { } f && filter.To is { } t && t <= f)
        {
            return AppError.Validation(new Dictionary<string, string[]> { ["to"] = ["Must be after 'from'."] });
        }

        var query = db.AuditEntries.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filter.EntityType))
        {
            query = query.Where(e => e.EntityType == filter.EntityType);
        }

        if (!string.IsNullOrWhiteSpace(filter.EntityId))
        {
            query = query.Where(e => e.EntityId == filter.EntityId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Action))
        {
            query = query.Where(e => e.Action == filter.Action);
        }

        if (!string.IsNullOrWhiteSpace(filter.ActorId))
        {
            query = query.Where(e => e.ActorId == filter.ActorId);
        }

        if (filter.From is { } from)
        {
            query = query.Where(e => e.OccurredAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(e => e.OccurredAt < to);
        }

        var total = await query.CountAsync(ct);
        var ordered = filter.NewestFirst ? query.OrderByDescending(e => e.Sequence) : query.OrderBy(e => e.Sequence);
        var rows = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var items = rows.Select(e => new AuditEntryDto(
            e.Id, e.Sequence, e.OccurredAt, e.ActorId, e.ActorName, e.Action, e.EntityType, e.EntityId,
            e.Details is null ? null : ParseDetails(e.Details), e.TraceId)).ToList();
        return new PageResult<AuditEntryDto>(items, page, pageSize, total);
    }

    /// <summary>The action names that occur, for the audit screen's filter.</summary>
    public async Task<IReadOnlyList<string>> ActionsAsync(CancellationToken ct) =>
        await db.AuditEntries.AsNoTracking().Select(e => e.Action).Distinct().OrderBy(a => a).ToListAsync(ct);

    private static JsonElement ParseDetails(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
