using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Verso.Domain.Audit;
using Verso.Domain.Templates;

namespace Verso.Application.Templates;

/// <summary>Template use cases: create, edit the draft, publish, and read versions. Every change is audited.</summary>
public sealed class TemplateService(IVersoDbContext db, ICurrentUser user, TimeProvider clock)
{
    public const string EntityType = "Template";

    public static class Actions
    {
        public const string Created = "TemplateCreated";
        public const string DraftCreated = "TemplateDraftCreated";
        public const string DraftSaved = "TemplateDraftSaved";
        public const string Published = "TemplatePublished";
    }

    public async Task<Result<TemplateDto>> CreateAsync(CreateTemplateRequest request, CancellationToken ct)
    {
        if (TemplateValidation.ValidateCreate(request, out var documentType) is { } errors)
        {
            return AppError.Validation(errors);
        }

        var key = request.Key!;
        if (await db.Templates.AnyAsync(t => t.Key == key, ct))
        {
            return AppError.Conflict("template.key_taken", $"A template with key '{key}' already exists.");
        }

        var now = clock.GetUtcNow();
        var template = Template.Create(key, request.Name!.Trim(), request.Description?.Trim(), documentType, TemplateContent.Empty, user.Id, now);
        db.Templates.Add(template);
        Audit(Actions.Created, template.Id, now, new { key });
        await db.SaveChangesAsync(ct);

        return ToDto(template);
    }

    public async Task<PageResult<TemplateSummaryDto>> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Templates.AsNoTracking();
        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderBy(t => t.Key)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new
            {
                t.Id,
                t.Key,
                t.Name,
                t.DocumentType,
                LatestPublished = t.Versions.Where(v => v.Status == TemplateVersionStatus.Published).Max(v => v.VersionNumber),
                HasDraft = t.Versions.Any(v => v.Status == TemplateVersionStatus.Draft),
                UpdatedAt = t.Versions.Max(v => v.UpdatedAt),
            })
            .ToListAsync(ct);

        var items = rows.Select(r => new TemplateSummaryDto(r.Id, r.Key, r.Name, r.DocumentType.ToString(), r.LatestPublished, r.HasDraft, r.UpdatedAt)).ToList();
        return new PageResult<TemplateSummaryDto>(items, page, pageSize, total);
    }

    public async Task<Result<TemplateDto>> GetAsync(Guid id, CancellationToken ct) =>
        await LoadAsync(id, ct) is { } template ? ToDto(template) : NotFound(id);

    public async Task<Result<IReadOnlyList<TemplateVersionSummaryDto>>> ListVersionsAsync(Guid id, CancellationToken ct)
    {
        if (await LoadAsync(id, ct) is not { } template)
        {
            return NotFound(id);
        }

        // Draft first, then published versions newest first.
        IReadOnlyList<TemplateVersionSummaryDto> versions = template.Versions
            .OrderBy(v => v.Status == TemplateVersionStatus.Draft ? 0 : 1)
            .ThenByDescending(v => v.VersionNumber)
            .Select(v => new TemplateVersionSummaryDto(v.Id, v.VersionNumber, v.Status.ToString(), v.ContentHash, v.UpdatedAt, v.UpdatedBy, v.PublishedAt, v.PublishedBy))
            .ToList();
        return Result.Success(versions);
    }

    public async Task<Result<Versioned<TemplateVersionDto>>> GetVersionAsync(Guid id, Guid versionId, CancellationToken ct)
    {
        if (await LoadAsync(id, ct) is not { } template)
        {
            return NotFound(id);
        }

        var version = template.Versions.SingleOrDefault(v => v.Id == versionId);
        return version is null
            ? AppError.NotFound("template.version_not_found", $"Version {versionId} does not belong to template {id}.")
            : ToVersioned(version);
    }

    public async Task<Result<Versioned<TemplateVersionDto>>> GetDraftAsync(Guid id, CancellationToken ct)
    {
        if (await LoadAsync(id, ct) is not { } template)
        {
            return NotFound(id);
        }

        return template.Draft is { } draft ? ToVersioned(draft) : NoDraft();
    }

    public async Task<Result<Versioned<TemplateVersionDto>>> StartDraftAsync(Guid id, CancellationToken ct)
    {
        if (await LoadAsync(id, ct) is not { } template)
        {
            return NotFound(id);
        }

        if (template.Draft is not null)
        {
            return AppError.Conflict("template.draft_exists", "This template already has a draft. Edit it, or publish it first.");
        }

        if (template.LatestPublished is null)
        {
            return AppError.Conflict("template.nothing_published", "There is no published version to start a draft from.");
        }

        var now = clock.GetUtcNow();
        var draft = template.StartDraft(user.Id, now);
        Audit(Actions.DraftCreated, template.Id, now, new { versionId = draft.Id, fromVersion = template.LatestPublished.VersionNumber });
        await db.SaveChangesAsync(ct);

        return ToVersioned(draft);
    }

    public async Task<Result<Versioned<TemplateVersionDto>>> SaveDraftAsync(Guid id, string? ifMatch, SaveDraftRequest request, CancellationToken ct)
    {
        if (await LoadAsync(id, ct) is not { } template)
        {
            return NotFound(id);
        }

        if (template.Draft is not { } draft)
        {
            return NoDraft();
        }

        if (ETag.Check(ifMatch, draft.RowVersion, out var expected) is { } preconditionError)
        {
            return preconditionError;
        }

        if (TemplateValidation.ValidateContent(request, out var content) is { } errors)
        {
            return AppError.Validation(errors);
        }

        var now = clock.GetUtcNow();
        template.SaveDraft(content!, user.Id, now);
        db.SetOriginalRowVersion(draft, expected);
        Audit(Actions.DraftSaved, template.Id, now, new { versionId = draft.Id });

        return await SaveWithConcurrencyAsync(draft, ct);
    }

    public async Task<Result<Versioned<TemplateVersionDto>>> PublishAsync(Guid id, string? ifMatch, CancellationToken ct)
    {
        if (await LoadAsync(id, ct) is not { } template)
        {
            return NotFound(id);
        }

        if (template.Draft is not { } draft)
        {
            return NoDraft();
        }

        if (ETag.Check(ifMatch, draft.RowVersion, out var expected) is { } preconditionError)
        {
            return preconditionError;
        }

        var now = clock.GetUtcNow();
        var published = template.Publish(ComputeContentHash(draft.Content), user.Id, now);
        db.SetOriginalRowVersion(draft, expected);
        Audit(Actions.Published, template.Id, now, new { versionId = published.Id, versionNumber = published.VersionNumber, contentHash = published.ContentHash });

        return await SaveWithConcurrencyAsync(published, ct);
    }

    /// <summary>SHA-256 over the parts that determine document output: page settings, layout, data schema (not sample data).</summary>
    public static string ComputeContentHash(TemplateContent content)
    {
        var p = content.PageSettings;
        var page = JsonSerializer.Serialize(new
        {
            size = p.Size,
            orientation = p.Orientation,
            marginsMm = new { top = Normalize(p.MarginTopMm), right = Normalize(p.MarginRightMm), bottom = Normalize(p.MarginBottomMm), left = Normalize(p.MarginLeftMm) },
        });
        return CanonicalJson.Sha256Hex($$"""{"pageSettings":{{page}},"layout":{{content.Layout}},"dataSchema":{{content.DataSchema}}}""");

        // 18, 18.0 and 18.00 must hash the same (the database stores decimal(5,2)).
        static decimal Normalize(decimal d) => decimal.Parse(d.ToString("0.############", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    private async Task<Result<Versioned<TemplateVersionDto>>> SaveWithConcurrencyAsync(TemplateVersion version, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ETag.StaleError();
        }

        return ToVersioned(version);
    }

    private Task<Template?> LoadAsync(Guid id, CancellationToken ct) =>
        db.Templates.Include(t => t.Versions).SingleOrDefaultAsync(t => t.Id == id, ct);

    private void Audit(string action, Guid templateId, DateTimeOffset now, object details) =>
        db.AuditEntries.Add(AuditEntry.Record(
            action, EntityType, templateId.ToString(), user.Id, user.Name, now,
            JsonSerializer.Serialize(details), Activity.Current?.TraceId.ToHexString()));

    private static AppError NotFound(Guid id) => AppError.NotFound("template.not_found", $"Template {id} was not found.");

    private static AppError NoDraft() => AppError.Conflict("template.no_draft",
        "This template has no draft. Published versions cannot be changed; start a new draft with POST …/draft.");

    private static TemplateDto ToDto(Template t) => new(
        t.Id, t.Key, t.Name, t.Description, t.DocumentType.ToString(), t.CreatedAt, t.CreatedBy,
        t.LatestPublished?.VersionNumber, t.Draft?.Id);

    private static Versioned<TemplateVersionDto> ToVersioned(TemplateVersion v)
    {
        var p = v.Content.PageSettings;
        var dto = new TemplateVersionDto(
            v.Id, v.TemplateId, v.VersionNumber, v.Status.ToString(),
            new PageSettingsDto(p.Size, p.Orientation, new MarginsDto(p.MarginTopMm, p.MarginRightMm, p.MarginBottomMm, p.MarginLeftMm)),
            Parse(v.Content.Layout), Parse(v.Content.DataSchema), Parse(v.Content.SampleData),
            v.ContentHash, v.CreatedAt, v.CreatedBy, v.UpdatedAt, v.UpdatedBy, v.PublishedAt, v.PublishedBy);
        return new Versioned<TemplateVersionDto>(dto, ETag.From(v.RowVersion));
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
