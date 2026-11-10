using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Verso.Domain.Templates;
using Verso.Rendering;
using Verso.Rendering.Html;
using Verso.Rendering.Layout;

namespace Verso.Application.Templates;

/// <summary>Queues background jobs. Implemented with Hangfire in Infrastructure; the Worker runs them.</summary>
public interface IPreviewJobs
{
    void EnqueuePdfPreview(Guid previewId);
}

public sealed record PdfPreviewDto(
    Guid PreviewId,
    Guid TemplateId,
    string Status,
    IReadOnlyList<RenderIssueDto> Issues,
    string? Error,
    int? SizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

/// <summary>A finished preview whose "ready" notification still has to be sent.</summary>
public sealed record PreviewReadyNotice(Guid PreviewId, string Status);

/// <summary>PDF previews of template drafts: queue (API), read (API), and the ready-notification hand-off (API relay).</summary>
public sealed class PdfPreviews(IVersoDbContext db, IPreviewJobs jobs, ICurrentUser user, TimeProvider clock)
{
    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromHours(24);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Result<PdfPreviewDto>> QueueAsync(Guid templateId, PreviewRequest? request, CancellationToken ct)
    {
        if (request?.Data is { } data && (data.ValueKind != JsonValueKind.Object || data.GetRawText().Length > TemplatePreview.MaxDataChars))
        {
            return AppError.Validation(new Dictionary<string, string[]>
            {
                ["data"] = [$"Must be a JSON object of at most {TemplatePreview.MaxDataChars:N0} characters (synthetic data only)."],
            });
        }

        var template = await db.Templates.AsNoTracking().Include(t => t.Versions).SingleOrDefaultAsync(t => t.Id == templateId, ct);
        if (template is null)
        {
            return AppError.NotFound("template.not_found", $"Template {templateId} was not found.");
        }

        if (template.Draft is not { } draft)
        {
            return AppError.Conflict("template.no_draft", "This template has no draft to preview.");
        }

        if (LayoutParser.Parse(draft.Content.Layout).Layout is null)
        {
            return AppError.Conflict("template.layout_invalid", "The stored layout is invalid; save the draft again to fix it.");
        }

        var preview = new PdfPreview(
            template.Id, draft.Id, template.Name, draft.Content.PageSettings, draft.Content.Layout,
            request?.Data?.GetRawText() ?? draft.Content.SampleData, user.Id, clock.GetUtcNow());
        db.PdfPreviews.Add(preview);
        await db.SaveChangesAsync(ct);

        // Saved first, then queued. If queueing fails the row is marked failed, so nothing waits forever.
        // (Executions also have a dispatcher that re-queues lost jobs; for a throwaway preview this is enough.)
        try
        {
            jobs.EnqueuePdfPreview(preview.Id);
        }
        catch (Exception)
        {
            preview.Fail("The preview could not be queued.", clock.GetUtcNow());
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        return ToDto(preview);
    }

    public async Task<Result<PdfPreviewDto>> GetAsync(Guid previewId, CancellationToken ct)
    {
        var row = await db.PdfPreviews.AsNoTracking()
            .Where(p => p.Id == previewId)
            .Select(p => new
            {
                p.Id, p.TemplateId, p.Status, p.Issues, p.Error, p.CreatedAt, p.CompletedAt,
                Size = p.Content == null ? (int?)null : p.Content.Length,
            })
            .SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return NotFound(previewId);
        }

        return new PdfPreviewDto(row.Id, row.TemplateId, StatusText(row.Status), ParseIssues(row.Issues), row.Error, row.Size, row.CreatedAt, row.CompletedAt);
    }

    public async Task<Result<byte[]>> GetContentAsync(Guid previewId, CancellationToken ct)
    {
        var row = await db.PdfPreviews.AsNoTracking()
            .Where(p => p.Id == previewId)
            .Select(p => new { p.Status, p.Content })
            .SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return NotFound(previewId);
        }

        return row.Status switch
        {
            PdfPreviewStatus.Succeeded when row.Content is not null => row.Content,
            PdfPreviewStatus.Failed => AppError.Conflict("preview.failed", "The preview failed; see its error."),
            _ => AppError.Conflict("preview.not_ready", "The preview isn't ready yet."),
        };
    }

    /// <summary>Finished previews not yet announced. They are marked as announced right away: notifications are best effort, REST is the source of truth.</summary>
    public async Task<IReadOnlyList<PreviewReadyNotice>> TakeReadyNoticesAsync(int max, CancellationToken ct)
    {
        var finished = await db.PdfPreviews
            .Where(p => p.NotifiedAt == null && (p.Status == PdfPreviewStatus.Succeeded || p.Status == PdfPreviewStatus.Failed))
            .OrderBy(p => p.CompletedAt)
            .Take(max)
            .ToListAsync(ct);
        if (finished.Count == 0)
        {
            return [];
        }

        var now = clock.GetUtcNow();
        foreach (var p in finished)
        {
            p.MarkNotified(now);
        }

        await db.SaveChangesAsync(ct);
        return [.. finished.Select(p => new PreviewReadyNotice(p.Id, StatusText(p.Status)))];
    }

    internal static PdfPreviewDto ToDto(PdfPreview p) =>
        new(p.Id, p.TemplateId, StatusText(p.Status), ParseIssues(p.Issues), p.Error, p.Content?.Length, p.CreatedAt, p.CompletedAt);

    internal static string StatusText(PdfPreviewStatus status) => status.ToString();

    private static List<RenderIssueDto> ParseIssues(string json) =>
        JsonSerializer.Deserialize<List<RenderIssueDto>>(json, Json) ?? [];

    private static AppError NotFound(Guid id) => AppError.NotFound("preview.not_found", $"Preview {id} was not found.");
}

/// <summary>Runs one attempt of a PDF preview (in the Worker, called by the background job).</summary>
public sealed class PdfPreviewRunner(IVersoDbContext db, IPdfPrinter printer, TimeProvider clock)
{
    /// <summary>Total attempts for technical failures (Chromium crash, timeout). The job scheduler retries up to this.</summary>
    public const int MaxAttempts = 3;

    /// <summary>
    /// Renders and prints. A technical failure is recorded and rethrown so the scheduler retries; on the last attempt
    /// the preview is marked failed instead. Problems that retrying can't fix (invalid snapshot) fail at once.
    /// </summary>
    public async Task RunAsync(Guid previewId, CancellationToken ct)
    {
        var preview = await db.PdfPreviews.SingleOrDefaultAsync(p => p.Id == previewId, ct);
        if (preview is null || preview.IsFinished)
        {
            return; // deleted by cleanup, or a duplicate delivery
        }

        preview.Start(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);

        var (layout, errors) = LayoutParser.Parse(preview.Layout);
        if (layout is null)
        {
            preview.Fail("Invalid layout: " + string.Join("; ", errors.Take(3).Select(e => $"{e.Path}: {e.Message}")), clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            return;
        }

        RenderResult rendered;
        using (var data = JsonDocument.Parse(preview.Data))
        {
            rendered = HtmlRenderer.Render(layout, TemplatePreview.ToPageSetup(preview.PageSettings), data.RootElement, RenderMode.Preview, preview.TemplateName);
        }

        try
        {
            var output = await printer.PrintAsync(rendered.Print, ct);
            var issues = rendered.Issues.Select(i => new RenderIssueDto(i.Severity == IssueSeverity.Error ? "error" : "warning", i.Code, i.Message, i.ElementId, i.Path));
            preview.Succeed(output.Content, JsonSerializer.Serialize(issues, PdfPreviews.Json), clock.GetUtcNow());
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var message = $"PDF rendering failed (attempt {preview.Attempts} of {MaxAttempts}): {ex.Message}";
            if (preview.Attempts >= MaxAttempts)
            {
                preview.Fail(message, clock.GetUtcNow());
                await db.SaveChangesAsync(CancellationToken.None);
                return;
            }

            preview.AttemptFailed(message);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }
}

/// <summary>Deletes previews older than <see cref="PdfPreviews.RetentionPeriod"/> (recurring job in the Worker).</summary>
public sealed class PdfPreviewCleanup(IVersoDbContext db, TimeProvider clock)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - PdfPreviews.RetentionPeriod;
        return await db.PdfPreviews.Where(p => p.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
    }
}
