using Verso.Api.Common;
using Verso.Application.Templates;

namespace Verso.Api.Templates;

/// <summary>PDF previews of template drafts (rendered by the Worker). Contract documented in docs/api.md.</summary>
public static class PdfPreviewEndpoints
{
    public static IEndpointRouteBuilder MapPdfPreviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/templates/{id:guid}/draft/preview/pdf", async (Guid id, PreviewRequest? request, PdfPreviews previews, CancellationToken ct) =>
            {
                var result = await previews.QueueAsync(id, request, ct);
                return result.IsSuccess
                    ? Results.Accepted($"/api/v1/previews/{result.Value!.PreviewId}", result.Value)
                    : result.Error!.ToProblem();
            })
            .WithTags("Templates")
            .WithName("PreviewTemplateDraftPdf")
            .Produces<PdfPreviewDto>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        var group = app.MapGroup("/api/v1/previews").WithTags("Previews");

        group.MapGet("/{previewId:guid}", async (Guid previewId, PdfPreviews previews, CancellationToken ct) =>
                (await previews.GetAsync(previewId, ct)).ToResult())
            .WithName("GetPdfPreview")
            .Produces<PdfPreviewDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{previewId:guid}/content", async (Guid previewId, PdfPreviews previews, HttpContext http, CancellationToken ct) =>
            {
                var result = await previews.GetContentAsync(previewId, ct);
                if (!result.IsSuccess)
                {
                    return result.Error!.ToProblem();
                }

                // Shown inline in the designer. A preview is temporary and personal: don't cache it anywhere.
                http.Response.Headers.CacheControl = "no-store";
                http.Response.Headers.XContentTypeOptions = "nosniff";
                http.Response.Headers.ContentDisposition = $"inline; filename=\"preview-{previewId:N}.pdf\"";
                return Results.File(result.Value!, "application/pdf");
            })
            .WithName("GetPdfPreviewContent")
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }
}
