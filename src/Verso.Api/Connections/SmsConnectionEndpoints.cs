using Verso.Api.Auth;
using Verso.Api.Common;
using Verso.Application.Connections;

namespace Verso.Api.Connections;

/// <summary>SMS connection API, <c>/api/v1/sms-connections</c> (ADR 0018). Administrators only, except the options list for workflow authors. The API key is write-only: no response carries it.</summary>
public static class SmsConnectionEndpoints
{
    public static IEndpointRouteBuilder MapSmsConnectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/sms-connections").WithTags("SMS connections").RequireAuthorization(Policies.Administration);

        group.MapGet("/", async (SmsConnectionService service, CancellationToken ct, bool includeInactive = false) =>
                Results.Ok(await service.ListAsync(includeInactive, ct)))
            .WithName("ListSmsConnections")
            .Produces<IReadOnlyList<SmsConnectionDto>>();

        group.MapPost("/", async (SaveSmsConnectionRequest request, SmsConnectionService service, CancellationToken ct) =>
            {
                var result = await service.CreateAsync(request, ct);
                return result.IsSuccess
                    ? Results.Created($"/api/v1/sms-connections/{result.Value!.Id}", result.Value)
                    : result.Error!.ToProblem();
            })
            .WithName("CreateSmsConnection")
            .Produces<SmsConnectionDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/{id:guid}", async (Guid id, SaveSmsConnectionRequest request, SmsConnectionService service, CancellationToken ct) =>
                (await service.UpdateAsync(id, request, ct)).ToResult())
            .WithName("UpdateSmsConnection")
            .Produces<SmsConnectionDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/deactivate", async (Guid id, SmsConnectionService service, CancellationToken ct) =>
                (await service.DeactivateAsync(id, ct)).ToResult())
            .WithName("DeactivateSmsConnection")
            .Produces<SmsConnectionDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/activate", async (Guid id, SmsConnectionService service, CancellationToken ct) =>
                (await service.ActivateAsync(id, ct)).ToResult())
            .WithName("ActivateSmsConnection")
            .Produces<SmsConnectionDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/tests", async (Guid id, RequestSmsConnectionTest request, SmsConnectionTests service, CancellationToken ct) =>
            {
                var result = await service.RequestAsync(id, request, ct);
                return result.IsSuccess
                    ? Results.Accepted($"/api/v1/sms-connections/tests/{result.Value!.Id}", result.Value)
                    : result.Error!.ToProblem();
            })
            .WithName("RequestSmsConnectionTest")
            .ProducesValidationProblem()
            .Produces<SmsConnectionTestDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/tests/{testId:guid}", async (Guid testId, SmsConnectionTests service, CancellationToken ct) =>
                (await service.GetAsync(testId, ct)).ToResult())
            .WithName("GetSmsConnectionTest")
            .Produces<SmsConnectionTestDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Workflow authors pick a connection by name in the Send SMS step: key, name, type and whether sending is on, for people who may edit workflows.
        app.MapGet("/api/v1/sms-connection-options", async (SmsConnectionService service, CancellationToken ct) => Results.Ok(await service.ListOptionsAsync(ct)))
            .WithTags("SMS connections")
            .RequireAuthorization(Policies.WorkflowAuthoring)
            .WithName("ListSmsConnectionOptions")
            .Produces<IReadOnlyList<SmsConnectionOptionDto>>();

        return app;
    }
}
