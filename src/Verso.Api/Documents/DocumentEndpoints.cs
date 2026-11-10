using Verso.Api.Common;
using Verso.Application.Documents;
using Verso.Application.Templates;

namespace Verso.Api.Documents;

/// <summary>Archive search, document details and download (plan §5.4). Contract documented in docs/api.md.</summary>
public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/documents").WithTags("Documents");

        group.MapGet("/", async (
                    DocumentService service, CancellationToken ct,
                    string? q = null, DateTimeOffset? from = null, DateTimeOffset? to = null, int page = 1, int pageSize = 25) =>
                (await service.SearchAsync(q, from, to, page, pageSize, ct)).ToResult())
            .WithName("SearchDocuments")
            .Produces<PageResult<ArchivedDocumentDto>>()
            .ProducesValidationProblem();

        group.MapGet("/{id:guid}", async (Guid id, DocumentService service, CancellationToken ct) =>
                (await service.GetAsync(id, ct)).ToResult())
            .WithName("GetDocument")
            .Produces<ArchivedDocumentDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/content", async (Guid id, DocumentService service, HttpContext http, CancellationToken ct) =>
            {
                var result = await service.OpenContentAsync(id, ct);
                if (!result.IsSuccess)
                {
                    return result.Error!.ToProblem();
                }

                // An archived record leaves the browser as a download, never cached, never sniffed.
                http.Response.Headers.CacheControl = "no-store";
                http.Response.Headers.XContentTypeOptions = "nosniff";
                return Results.File(result.Value!.Bytes, result.Value.ContentType, result.Value.FileName);
            })
            .WithName("GetDocumentContent")
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }
}
