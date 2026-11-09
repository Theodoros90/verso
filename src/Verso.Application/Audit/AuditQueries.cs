using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Verso.Application.Templates;

namespace Verso.Application.Audit;

public sealed record AuditEntryDto(
    Guid Id, long Sequence, DateTimeOffset OccurredAt, string ActorId, string ActorName,
    string Action, string EntityType, string EntityId, JsonElement? Details, string? TraceId);

/// <summary>Read side of the audit log, oldest entry first.</summary>
public sealed class AuditQueries(IVersoDbContext db)
{
    public async Task<PageResult<AuditEntryDto>> ListAsync(string? entityType, string? entityId, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.AuditEntries.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(entityType))
        {
            query = query.Where(e => e.EntityType == entityType);
        }

        if (!string.IsNullOrWhiteSpace(entityId))
        {
            query = query.Where(e => e.EntityId == entityId);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(e => e.Sequence).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var items = rows.Select(e => new AuditEntryDto(
            e.Id, e.Sequence, e.OccurredAt, e.ActorId, e.ActorName, e.Action, e.EntityType, e.EntityId,
            e.Details is null ? null : ParseDetails(e.Details), e.TraceId)).ToList();
        return new PageResult<AuditEntryDto>(items, page, pageSize, total);
    }

    private static JsonElement ParseDetails(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
