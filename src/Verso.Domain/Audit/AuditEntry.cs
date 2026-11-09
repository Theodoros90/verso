namespace Verso.Domain.Audit;

/// <summary>Append-only record of who did what to which entity. Never updated or deleted.</summary>
public sealed class AuditEntry
{
    private AuditEntry() { } // EF Core

    public Guid Id { get; private set; }

    /// <summary>Database-assigned, strictly increasing: the authoritative order of entries.</summary>
    public long Sequence { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }
    public string ActorId { get; private set; } = "";
    public string ActorName { get; private set; } = "";
    public string Action { get; private set; } = "";
    public string EntityType { get; private set; } = "";
    public string EntityId { get; private set; } = "";

    /// <summary>Small JSON object with action-specific facts (e.g. version number). Never payload data.</summary>
    public string? Details { get; private set; }

    /// <summary>W3C trace id of the request that caused the entry, for correlating with logs.</summary>
    public string? TraceId { get; private set; }

    public static AuditEntry Record(
        string action, string entityType, string entityId, string actorId, string actorName,
        DateTimeOffset now, string? details = null, string? traceId = null) => new()
    {
        Id = Guid.CreateVersion7(now),
        OccurredAt = now,
        Action = action,
        EntityType = entityType,
        EntityId = entityId,
        ActorId = actorId,
        ActorName = actorName,
        Details = details,
        TraceId = traceId,
    };
}
