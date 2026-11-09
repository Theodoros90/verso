using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Verso.Domain.Templates;
using Verso.Rendering;
using Verso.Rendering.Html;
using Verso.Rendering.Layout;

namespace Verso.Application.Templates;

/// <summary>Optional body of the preview endpoint. Without <see cref="Data"/>, the draft's sample data is used.</summary>
public sealed record PreviewRequest(JsonElement? Data);

public sealed record RenderIssueDto(string Severity, string Code, string Message, string ElementId, string? Path);

public sealed record HtmlPreviewDto(string Html, IReadOnlyList<RenderIssueDto> Issues);

/// <summary>Renders a template draft to HTML in preview mode (missing values shown as placeholders and listed as issues).</summary>
public sealed class TemplatePreview(IVersoDbContext db)
{
    public const int MaxDataChars = 1_000_000;

    public async Task<Result<HtmlPreviewDto>> PreviewDraftAsync(Guid id, PreviewRequest? request, CancellationToken ct)
    {
        if (request?.Data is { } data && (data.ValueKind != JsonValueKind.Object || data.GetRawText().Length > MaxDataChars))
        {
            return AppError.Validation(new Dictionary<string, string[]>
            {
                ["data"] = [$"Must be a JSON object of at most {MaxDataChars:N0} characters (synthetic data only)."],
            });
        }

        var template = await db.Templates.AsNoTracking().Include(t => t.Versions).SingleOrDefaultAsync(t => t.Id == id, ct);
        if (template is null)
        {
            return AppError.NotFound("template.not_found", $"Template {id} was not found.");
        }

        if (template.Draft is not { } draft)
        {
            return AppError.Conflict("template.no_draft", "This template has no draft to preview.");
        }

        var (layout, errors) = LayoutParser.Parse(draft.Content.Layout);
        if (layout is null)
        {
            // Can only happen for drafts saved before layout validation existed (M2); re-saving fixes it.
            return AppError.Conflict("template.layout_invalid",
                $"The stored layout is invalid: {string.Join("; ", errors.Take(5).Select(e => $"{e.Path}: {e.Message}"))}");
        }

        using var sample = JsonDocument.Parse(draft.Content.SampleData);
        var input = request?.Data ?? sample.RootElement;
        var result = HtmlRenderer.Render(layout, ToPageSetup(draft.Content.PageSettings), input, RenderMode.Preview, template.Name);

        return new HtmlPreviewDto(
            result.Html,
            [.. result.Issues.Select(i => new RenderIssueDto(i.Severity == IssueSeverity.Error ? "error" : "warning", i.Code, i.Message, i.ElementId, i.Path))]);
    }

    public static PageSetup ToPageSetup(PageSettings p) =>
        PageSetup.From(p.Size, p.Orientation, p.MarginTopMm, p.MarginRightMm, p.MarginBottomMm, p.MarginLeftMm);
}
