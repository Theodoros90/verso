using Verso.Api.Auth;
using Verso.Api.Common;
using Verso.Application.Connections;

namespace Verso.Api.Connections;

/// <summary>Database connection API, <c>/api/v1/database-connections</c> (ADR 0016). Administrators only. The password is write-only: no response carries it.</summary>
public static class DatabaseConnectionEndpoints
{
    public static IEndpointRouteBuilder MapDatabaseConnectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/database-connections").WithTags("Database connections").RequireAuthorization(Policies.Administration);

        group.MapGet("/", async (DatabaseConnectionService service, CancellationToken ct, bool includeInactive = false) =>
                Results.Ok(await service.ListAsync(includeInactive, ct)))
            .WithName("ListDatabaseConnections")
            .Produces<IReadOnlyList<DatabaseConnectionDto>>();

        group.MapPost("/", async (SaveDatabaseConnectionRequest request, DatabaseConnectionService service, CancellationToken ct) =>
            {
                var result = await service.CreateAsync(request, ct);
                return result.IsSuccess
                    ? Results.Created($"/api/v1/database-connections/{result.Value!.Id}", result.Value)
                    : result.Error!.ToProblem();
            })
            .WithName("CreateDatabaseConnection")
            .Produces<DatabaseConnectionDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/{id:guid}", async (Guid id, SaveDatabaseConnectionRequest request, DatabaseConnectionService service, CancellationToken ct) =>
                (await service.UpdateAsync(id, request, ct)).ToResult())
            .WithName("UpdateDatabaseConnection")
            .Produces<DatabaseConnectionDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/deactivate", async (Guid id, DatabaseConnectionService service, CancellationToken ct) =>
                (await service.DeactivateAsync(id, ct)).ToResult())
            .WithName("DeactivateDatabaseConnection")
            .Produces<DatabaseConnectionDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/activate", async (Guid id, DatabaseConnectionService service, CancellationToken ct) =>
                (await service.ActivateAsync(id, ct)).ToResult())
            .WithName("ActivateDatabaseConnection")
            .Produces<DatabaseConnectionDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/tests", async (Guid id, DatabaseConnectionTests service, CancellationToken ct) =>
            {
                var result = await service.RequestAsync(id, ct);
                return result.IsSuccess
                    ? Results.Accepted($"/api/v1/database-connections/tests/{result.Value!.Id}", result.Value)
                    : result.Error!.ToProblem();
            })
            .WithName("RequestDatabaseConnectionTest")
            .Produces<DatabaseConnectionTestDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/tests/{testId:guid}", async (Guid testId, DatabaseConnectionTests service, CancellationToken ct) =>
                (await service.GetAsync(testId, ct)).ToResult())
            .WithName("GetDatabaseConnectionTest")
            .Produces<DatabaseConnectionTestDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Workflow authors pick a connection by name in the Query database step: key, name and database type only, for people who may edit workflows.
        app.MapGet("/api/v1/database-connection-options", async (DatabaseConnectionService service, CancellationToken ct) => Results.Ok(await service.ListOptionsAsync(ct)))
            .WithTags("Database connections")
            .RequireAuthorization(Policies.WorkflowAuthoring)
            .WithName("ListDatabaseConnectionOptions")
            .Produces<IReadOnlyList<DatabaseConnectionOptionDto>>();

        return app;
    }
}
