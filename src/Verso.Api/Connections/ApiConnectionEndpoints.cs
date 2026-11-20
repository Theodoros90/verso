using Verso.Api.Auth;
using Verso.Api.Common;
using Verso.Application.Connections;

namespace Verso.Api.Connections;

/// <summary>API connection API, <c>/api/v1/api-connections</c> (ADR 0017). Administrators only. The secret is write-only: no response carries it.</summary>
public static class ApiConnectionEndpoints
{
    public static IEndpointRouteBuilder MapApiConnectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/api-connections").WithTags("API connections").RequireAuthorization(Policies.Administration);

        group.MapGet("/", async (ApiConnectionService service, CancellationToken ct, bool includeInactive = false) =>
                Results.Ok(await service.ListAsync(includeInactive, ct)))
            .WithName("ListApiConnections")
            .Produces<IReadOnlyList<ApiConnectionDto>>();

        group.MapPost("/", async (SaveApiConnectionRequest request, ApiConnectionService service, CancellationToken ct) =>
            {
                var result = await service.CreateAsync(request, ct);
                return result.IsSuccess
                    ? Results.Created($"/api/v1/api-connections/{result.Value!.Id}", result.Value)
                    : result.Error!.ToProblem();
            })
            .WithName("CreateApiConnection")
            .Produces<ApiConnectionDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/{id:guid}", async (Guid id, SaveApiConnectionRequest request, ApiConnectionService service, CancellationToken ct) =>
                (await service.UpdateAsync(id, request, ct)).ToResult())
            .WithName("UpdateApiConnection")
            .Produces<ApiConnectionDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/deactivate", async (Guid id, ApiConnectionService service, CancellationToken ct) =>
                (await service.DeactivateAsync(id, ct)).ToResult())
            .WithName("DeactivateApiConnection")
            .Produces<ApiConnectionDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/activate", async (Guid id, ApiConnectionService service, CancellationToken ct) =>
                (await service.ActivateAsync(id, ct)).ToResult())
            .WithName("ActivateApiConnection")
            .Produces<ApiConnectionDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/tests", async (Guid id, ApiConnectionTests service, CancellationToken ct) =>
            {
                var result = await service.RequestAsync(id, ct);
                return result.IsSuccess
                    ? Results.Accepted($"/api/v1/api-connections/tests/{result.Value!.Id}", result.Value)
                    : result.Error!.ToProblem();
            })
            .WithName("RequestApiConnectionTest")
            .Produces<ApiConnectionTestDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/tests/{testId:guid}", async (Guid testId, ApiConnectionTests service, CancellationToken ct) =>
                (await service.GetAsync(testId, ct)).ToResult())
            .WithName("GetApiConnectionTest")
            .Produces<ApiConnectionTestDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Workflow authors pick a connection by name in the Fetch from API step: key, name and host only, for people who may edit workflows.
        app.MapGet("/api/v1/api-connection-options", async (ApiConnectionService service, CancellationToken ct) => Results.Ok(await service.ListOptionsAsync(ct)))
            .WithTags("API connections")
            .RequireAuthorization(Policies.WorkflowAuthoring)
            .WithName("ListApiConnectionOptions")
            .Produces<IReadOnlyList<ApiConnectionOptionDto>>();

        return app;
    }
}
