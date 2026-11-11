using Verso.Api.Auth;
using Verso.Api.Common;
using Verso.Application.Ingestion;

namespace Verso.Api.Ingestion;

/// <summary>
/// Administration of approved folder locations (Admin only, audited). The paths are visible here and nowhere else.
/// Contract documented in docs/api.md.
/// </summary>
public static class FolderLocationAdminEndpoints
{
    public static IEndpointRouteBuilder MapFolderLocationAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/folder-locations").WithTags("Administration").RequireAuthorization(Policies.Administration);

        group.MapGet("/", async (FolderLocationAdmin admin, CancellationToken ct) => Results.Ok(await admin.ListAsync(ct)))
            .WithName("ListAdminFolderLocations")
            .Produces<IReadOnlyList<AdminFolderLocationDto>>();

        group.MapPut("/{key}", async (string key, SaveFolderLocationRequest? request, FolderLocationAdmin admin, CancellationToken ct) =>
                (await admin.SaveAsync(key, request, ct)).ToResult())
            .WithName("SaveAdminFolderLocation")
            .Produces<AdminFolderLocationDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{key}", async (string key, FolderLocationAdmin admin, CancellationToken ct) =>
            {
                var result = await admin.RemoveAsync(key, ct);
                return result.IsSuccess ? Results.NoContent() : result.Error!.ToProblem();
            })
            .WithName("RemoveAdminFolderLocation")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }
}
