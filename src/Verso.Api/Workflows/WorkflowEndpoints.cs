using Microsoft.AspNetCore.Mvc;
using Verso.Api.Auth;
using Verso.Api.Common;
using Verso.Application.Templates;
using Verso.Application.Workflows;

namespace Verso.Api.Workflows;

/// <summary>Workflow API, <c>/api/v1/workflows</c> and <c>/api/v1/node-types</c>. Contract documented in docs/api.md.</summary>
public static class WorkflowEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/node-types", () => Results.Ok(WorkflowService.NodeTypes()))
            .WithTags("Workflows")
            .WithName("ListNodeTypes")
            .RequireAuthorization(Policies.Read)
            .Produces<IReadOnlyList<NodeTypeDto>>();

        var group = app.MapGroup("/api/v1/workflows").WithTags("Workflows").RequireAuthorization(Policies.Read);

        group.MapGet("/", async (WorkflowService service, CancellationToken ct, int page = 1, int pageSize = 25, bool inactive = false) =>
                Results.Ok(await service.ListAsync(page, pageSize, inactive, ct)))
            .WithName("ListWorkflows")
            .Produces<PageResult<WorkflowSummaryDto>>();

        group.MapPost("/", async (CreateWorkflowRequest request, WorkflowService service, CancellationToken ct) =>
            {
                var result = await service.CreateAsync(request, ct);
                return result.IsSuccess
                    ? Results.Created($"/api/v1/workflows/{result.Value!.Id}", result.Value)
                    : result.Error!.ToProblem();
            })
            .WithName("CreateWorkflow")
            .RequireAuthorization(Policies.WorkflowAuthoring)
            .Produces<WorkflowDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{id:guid}", async (Guid id, WorkflowService service, CancellationToken ct) =>
                (await service.GetAsync(id, ct)).ToResult())
            .WithName("GetWorkflow")
            .Produces<WorkflowDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/versions", async (Guid id, WorkflowService service, CancellationToken ct) =>
                (await service.ListVersionsAsync(id, ct)).ToResult())
            .WithName("ListWorkflowVersions")
            .Produces<IReadOnlyList<WorkflowVersionSummaryDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/versions/{versionId:guid}", async (Guid id, Guid versionId, WorkflowService service, HttpContext http, CancellationToken ct) =>
                (await service.GetVersionAsync(id, versionId, ct)).ToVersionedResult(http))
            .WithName("GetWorkflowVersion")
            .Produces<WorkflowVersionDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/draft", async (Guid id, WorkflowService service, HttpContext http, CancellationToken ct) =>
                (await service.GetDraftAsync(id, ct)).ToVersionedResult(http))
            .WithName("GetWorkflowDraft")
            .Produces<WorkflowVersionDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/draft", async (Guid id, WorkflowService service, HttpContext http, CancellationToken ct) =>
                (await service.StartDraftAsync(id, ct)).ToVersionedResult(http, v => Results.Created($"/api/v1/workflows/{id}/draft", v)))
            .WithName("StartWorkflowDraft")
            .RequireAuthorization(Policies.WorkflowAuthoring)
            .Produces<WorkflowVersionDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/{id:guid}/draft", async (
                    Guid id, SaveWorkflowDraftRequest request, [FromHeader(Name = "If-Match")] string? ifMatch,
                    WorkflowService service, HttpContext http, CancellationToken ct) =>
                (await service.SaveDraftAsync(id, ifMatch, request, ct)).ToVersionedResult(http))
            .WithName("SaveWorkflowDraft")
            .RequireAuthorization(Policies.WorkflowAuthoring)
            .Produces<WorkflowVersionDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/{id:guid}/draft/validate", async (Guid id, ValidateWorkflowRequest? request, WorkflowService service, CancellationToken ct) =>
                (await service.ValidateAsync(id, request, ct)).ToResult())
            .WithName("ValidateWorkflowDraft")
            .RequireAuthorization(Policies.WorkflowAuthoring)
            .Produces<WorkflowValidationDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/draft/publish", async (
                    Guid id, [FromHeader(Name = "If-Match")] string? ifMatch,
                    WorkflowService service, HttpContext http, CancellationToken ct) =>
                (await service.PublishAsync(id, ifMatch, ct)).ToVersionedResult(http))
            .WithName("PublishWorkflowDraft")
            .RequireAuthorization(Policies.WorkflowAuthoring)
            .Produces<WorkflowVersionDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/{id:guid}/activate", async (Guid id, WorkflowService service, CancellationToken ct) =>
                (await service.SetActiveAsync(id, true, ct)).ToResult())
            .WithName("ActivateWorkflow")
            .RequireAuthorization(Policies.WorkflowAuthoring)
            .Produces<WorkflowDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/deactivate", async (Guid id, WorkflowService service, CancellationToken ct) =>
                (await service.SetActiveAsync(id, false, ct)).ToResult())
            .WithName("DeactivateWorkflow")
            .RequireAuthorization(Policies.WorkflowAuthoring)
            .Produces<WorkflowDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}
