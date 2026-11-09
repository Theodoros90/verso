using Verso.Application.Audit;
using Verso.Application.Templates;

namespace Verso.Api.Audit;

public static class AuditEndpoints
{
    /// <summary><c>GET /api/v1/audit?entityType=&amp;entityId=&amp;page=&amp;pageSize=</c>, oldest entry first.</summary>
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/audit", async (
                AuditQueries queries, CancellationToken ct,
                string? entityType = null, string? entityId = null, int page = 1, int pageSize = 50) =>
                Results.Ok(await queries.ListAsync(entityType, entityId, page, pageSize, ct)))
            .WithTags("Audit")
            .WithName("ListAuditEntries")
            .Produces<PageResult<AuditEntryDto>>();

        return app;
    }
}
