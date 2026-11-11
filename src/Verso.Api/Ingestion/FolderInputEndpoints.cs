using Verso.Api.Auth;
using Verso.Api.Common;
using Verso.Application.Ingestion;

namespace Verso.Api.Ingestion;

/// <summary>
/// Folder input as the API sees it: the approved locations the Worker reported, "Test access" (run by the Worker)
/// and a workflow's scan status and recent files. The API never touches folders. Contract documented in docs/api.md.
/// </summary>
public static class FolderInputEndpoints
{
    public static IEndpointRouteBuilder MapFolderInputEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/folder-locations", async (FolderInputQueries queries, CancellationToken ct) =>
                Results.Ok(await queries.LocationsAsync(ct)))
            .WithTags("Folder input")
            .WithName("ListFolderLocations")
            .RequireAuthorization(Policies.Read)
            .Produces<IReadOnlyList<FolderLocationDto>>();

        app.MapPost("/api/v1/folder-access-tests", async (FolderAccessTestRequest? request, FolderAccessTests tests, CancellationToken ct) =>
            {
                var result = await tests.RequestAsync(request, ct);
                return result.IsSuccess
                    ? Results.Accepted($"/api/v1/folder-access-tests/{result.Value!.Id}", result.Value)
                    : result.Error!.ToProblem();
            })
            .WithTags("Folder input")
            .WithName("RequestFolderAccessTest")
            .RequireAuthorization(Policies.WorkflowAuthoring)
            .Produces<FolderAccessTestDto>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem();

        app.MapGet("/api/v1/folder-access-tests/{id:guid}", async (Guid id, FolderAccessTests tests, CancellationToken ct) =>
                (await tests.GetAsync(id, ct)).ToResult())
            .WithTags("Folder input")
            .WithName("GetFolderAccessTest")
            .RequireAuthorization(Policies.WorkflowAuthoring)
            .Produces<FolderAccessTestDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapGet("/api/v1/workflows/{id:guid}/folder-input", async (Guid id, FolderInputQueries queries, CancellationToken ct) =>
                (await queries.StatusAsync(id, ct)).ToResult())
            .WithTags("Folder input")
            .WithName("GetWorkflowFolderInput")
            .RequireAuthorization(Policies.Read)
            .Produces<FolderInputStatusDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}
