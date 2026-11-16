using Verso.Api.Auth;
using Verso.Api.Common;
using Verso.Application.Templates;

namespace Verso.Api.Templates;

/// <summary>Document category API, <c>/api/v1/document-categories</c>. Contract documented in docs/api.md.</summary>
public static class DocumentCategoryEndpoints
{
    public static IEndpointRouteBuilder MapDocumentCategoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/document-categories").WithTags("Document categories").RequireAuthorization(Policies.Read);

        group.MapGet("/", async (DocumentCategoryService service, CancellationToken ct, bool includeInactive = false) =>
                Results.Ok(await service.ListAsync(includeInactive, ct)))
            .WithName("ListDocumentCategories")
            .Produces<IReadOnlyList<DocumentCategoryDto>>();

        group.MapPost("/", async (CreateCategoryRequest request, DocumentCategoryService service, CancellationToken ct) =>
            {
                var result = await service.CreateAsync(request, ct);
                return result.IsSuccess
                    ? Results.Created($"/api/v1/document-categories/{result.Value!.Id}", result.Value)
                    : result.Error!.ToProblem();
            })
            .WithName("CreateDocumentCategory")
            .RequireAuthorization(Policies.TemplateAuthoring)
            .Produces<DocumentCategoryDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/{id:guid}", async (Guid id, RenameCategoryRequest request, DocumentCategoryService service, CancellationToken ct) =>
                (await service.RenameAsync(id, request, ct)).ToResult())
            .WithName("RenameDocumentCategory")
            .RequireAuthorization(Policies.TemplateAuthoring)
            .Produces<DocumentCategoryDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/deactivate", async (Guid id, DocumentCategoryService service, CancellationToken ct) =>
                (await service.DeactivateAsync(id, ct)).ToResult())
            .WithName("DeactivateDocumentCategory")
            .RequireAuthorization(Policies.TemplateAuthoring)
            .Produces<DocumentCategoryDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/activate", async (Guid id, DocumentCategoryService service, CancellationToken ct) =>
                (await service.ActivateAsync(id, ct)).ToResult())
            .WithName("ActivateDocumentCategory")
            .RequireAuthorization(Policies.TemplateAuthoring)
            .Produces<DocumentCategoryDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}
