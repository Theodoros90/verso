using Microsoft.AspNetCore.Mvc;
using Verso.Api.Common;
using Verso.Application.Templates;

namespace Verso.Api.Templates;

/// <summary>Template API, <c>/api/v1/templates</c>. Contract documented in docs/api.md.</summary>
public static class TemplateEndpoints
{
    public static IEndpointRouteBuilder MapTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/templates").WithTags("Templates");

        group.MapGet("/", async (TemplateService service, CancellationToken ct, int page = 1, int pageSize = 25) =>
                Results.Ok(await service.ListAsync(page, pageSize, ct)))
            .WithName("ListTemplates")
            .Produces<PageResult<TemplateSummaryDto>>();

        group.MapPost("/", async (CreateTemplateRequest request, TemplateService service, CancellationToken ct) =>
            {
                var result = await service.CreateAsync(request, ct);
                return result.IsSuccess
                    ? Results.Created($"/api/v1/templates/{result.Value!.Id}", result.Value)
                    : result.Error!.ToProblem();
            })
            .WithName("CreateTemplate")
            .Produces<TemplateDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{id:guid}", async (Guid id, TemplateService service, CancellationToken ct) =>
                (await service.GetAsync(id, ct)).ToResult())
            .WithName("GetTemplate")
            .Produces<TemplateDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/versions", async (Guid id, TemplateService service, CancellationToken ct) =>
                (await service.ListVersionsAsync(id, ct)).ToResult())
            .WithName("ListTemplateVersions")
            .Produces<IReadOnlyList<TemplateVersionSummaryDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/versions/{versionId:guid}", async (Guid id, Guid versionId, TemplateService service, HttpContext http, CancellationToken ct) =>
                (await service.GetVersionAsync(id, versionId, ct)).ToVersionedResult(http))
            .WithName("GetTemplateVersion")
            .Produces<TemplateVersionDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/draft", async (Guid id, TemplateService service, HttpContext http, CancellationToken ct) =>
                (await service.GetDraftAsync(id, ct)).ToVersionedResult(http))
            .WithName("GetTemplateDraft")
            .Produces<TemplateVersionDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/draft", async (Guid id, TemplateService service, HttpContext http, CancellationToken ct) =>
                (await service.StartDraftAsync(id, ct)).ToVersionedResult(http, v => Results.Created($"/api/v1/templates/{id}/draft", v)))
            .WithName("StartTemplateDraft")
            .Produces<TemplateVersionDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/{id:guid}/draft", async (
                    Guid id, SaveDraftRequest request, [FromHeader(Name = "If-Match")] string? ifMatch,
                    TemplateService service, HttpContext http, CancellationToken ct) =>
                (await service.SaveDraftAsync(id, ifMatch, request, ct)).ToVersionedResult(http))
            .WithName("SaveTemplateDraft")
            .Produces<TemplateVersionDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/{id:guid}/draft/publish", async (
                    Guid id, [FromHeader(Name = "If-Match")] string? ifMatch,
                    TemplateService service, HttpContext http, CancellationToken ct) =>
                (await service.PublishAsync(id, ifMatch, ct)).ToVersionedResult(http))
            .WithName("PublishTemplateDraft")
            .Produces<TemplateVersionDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        return app;
    }
}
