using Verso.Api.Auth;
using Verso.Api.Common;
using Verso.Application.Audit;
using Verso.Application.Templates;

namespace Verso.Api.Audit;

/// <summary>The audit trail (read-only; Auditor and Admin). Contract documented in docs/api.md.</summary>
public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/audit").WithTags("Audit").RequireAuthorization(Policies.ReadAudit);

        // GET /api/v1/audit?entityType=&entityId=&action=&actorId=&from=&to=&newestFirst=&page=&pageSize=
        group.MapGet("", async (
                AuditQueries queries, CancellationToken ct,
                string? entityType = null, string? entityId = null, string? action = null, string? actorId = null,
                DateTimeOffset? from = null, DateTimeOffset? to = null, bool newestFirst = false, int page = 1, int pageSize = 50) =>
                (await queries.ListAsync(new AuditFilter(entityType, entityId, action, actorId, from, to, newestFirst), page, pageSize, ct)).ToResult())
            .WithName("ListAuditEntries")
            .Produces<PageResult<AuditEntryDto>>()
            .ProducesValidationProblem();

        group.MapGet("/actions", async (AuditQueries queries, CancellationToken ct) => Results.Ok(await queries.ActionsAsync(ct)))
            .WithName("ListAuditActions")
            .Produces<IReadOnlyList<string>>();

        return app;
    }
}
