using Verso.Api.Auth;
using Verso.Api.Common;
using Verso.Application.Mail;

namespace Verso.Api.Mail;

/// <summary>Email relay API, <c>/api/v1/email-relays</c> (ADR 0015). Administrators only. The password is write-only: no response carries it.</summary>
public static class EmailRelayEndpoints
{
    public static IEndpointRouteBuilder MapEmailRelayEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/email-relays").WithTags("Email relays").RequireAuthorization(Policies.Administration);

        group.MapGet("/", async (EmailRelayService service, CancellationToken ct, bool includeInactive = false) =>
                Results.Ok(await service.ListAsync(includeInactive, ct)))
            .WithName("ListEmailRelays")
            .Produces<IReadOnlyList<EmailRelayDto>>();

        group.MapPost("/", async (SaveEmailRelayRequest request, EmailRelayService service, CancellationToken ct) =>
            {
                var result = await service.CreateAsync(request, ct);
                return result.IsSuccess
                    ? Results.Created($"/api/v1/email-relays/{result.Value!.Id}", result.Value)
                    : result.Error!.ToProblem();
            })
            .WithName("CreateEmailRelay")
            .Produces<EmailRelayDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/{id:guid}", async (Guid id, SaveEmailRelayRequest request, EmailRelayService service, CancellationToken ct) =>
                (await service.UpdateAsync(id, request, ct)).ToResult())
            .WithName("UpdateEmailRelay")
            .Produces<EmailRelayDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/deactivate", async (Guid id, EmailRelayService service, CancellationToken ct) =>
                (await service.DeactivateAsync(id, ct)).ToResult())
            .WithName("DeactivateEmailRelay")
            .Produces<EmailRelayDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/activate", async (Guid id, EmailRelayService service, CancellationToken ct) =>
                (await service.ActivateAsync(id, ct)).ToResult())
            .WithName("ActivateEmailRelay")
            .Produces<EmailRelayDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/tests", async (Guid id, EmailRelayTestRequest request, EmailRelayTests service, CancellationToken ct) =>
            {
                var result = await service.RequestAsync(id, request, ct);
                return result.IsSuccess
                    ? Results.Accepted($"/api/v1/email-relays/tests/{result.Value!.Id}", result.Value)
                    : result.Error!.ToProblem();
            })
            .WithName("RequestEmailRelayTest")
            .Produces<EmailRelayTestDto>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/tests/{testId:guid}", async (Guid testId, EmailRelayTests service, CancellationToken ct) =>
                (await service.GetAsync(testId, ct)).ToResult())
            .WithName("GetEmailRelayTest")
            .Produces<EmailRelayTestDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Workflow authors pick a relay by name in the Send email step: names and keys only, readable by any role.
        app.MapGet("/api/v1/email-relay-options", async (EmailRelayService service, CancellationToken ct) => Results.Ok(await service.ListOptionsAsync(ct)))
            .WithTags("Email relays")
            .RequireAuthorization(Policies.Read)
            .WithName("ListEmailRelayOptions")
            .Produces<IReadOnlyList<EmailRelayOptionDto>>();

        return app;
    }
}
