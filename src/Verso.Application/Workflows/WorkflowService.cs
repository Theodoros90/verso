using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Verso.Application.Templates;
using Verso.Domain.Audit;
using Verso.Domain.Templates;
using Verso.Domain.Workflows;

namespace Verso.Application.Workflows;

// Request and response shapes of the workflow API (docs/api.md#workflows).

public sealed record CreateWorkflowRequest(string? Key, string? Name, string? Description);

public sealed record SaveWorkflowDraftRequest(JsonElement? Definition);

/// <summary>Optional body of the validate endpoint: without a definition, the saved draft is validated.</summary>
public sealed record ValidateWorkflowRequest(JsonElement? Definition);

public sealed record WorkflowSummaryDto(Guid Id, string Key, string Name, int? LatestPublishedVersion, bool HasDraft, DateTimeOffset UpdatedAt);

public sealed record WorkflowDto(
    Guid Id, string Key, string Name, string? Description, DateTimeOffset CreatedAt, string CreatedBy, int? LatestPublishedVersion, Guid? DraftVersionId);

public sealed record WorkflowVersionSummaryDto(
    Guid VersionId, int? VersionNumber, string Status, string? ContentHash, DateTimeOffset UpdatedAt, string UpdatedBy, DateTimeOffset? PublishedAt, string? PublishedBy);

public sealed record WorkflowVersionDto(
    Guid VersionId, Guid WorkflowId, int? VersionNumber, string Status, JsonElement Definition, string? ContentHash,
    DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, string UpdatedBy, DateTimeOffset? PublishedAt, string? PublishedBy);

/// <summary>Workflow use cases: create, edit the draft, validate, publish, read. Every change is audited.</summary>
public sealed partial class WorkflowService(IVersoDbContext db, ICurrentUser user, TimeProvider clock)
{
    public const string EntityType = "Workflow";

    public static class Actions
    {
        public const string Created = "WorkflowCreated";
        public const string DraftCreated = "WorkflowDraftCreated";
        public const string DraftSaved = "WorkflowDraftSaved";
        public const string Published = "WorkflowPublished";
    }

    /// <summary>A new workflow starts with just its trigger (plan §5.2), configured with the catalog defaults.</summary>
    public static readonly string InitialDefinition =
        $$"""{"nodes":[{"id":"trigger","type":"api-trigger","title":"API / webhook","position":{"x":0,"y":0},"config":{{NodeCatalog.Find(NodeCatalog.ApiTrigger)!.Defaults.GetRawText()}}}],"edges":[]}""";

    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$")]
    private static partial Regex KeyPattern();

    public static IReadOnlyList<NodeTypeDto> NodeTypes() => NodeCatalog.All;

    public async Task<Result<WorkflowDto>> CreateAsync(CreateWorkflowRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var key = request.Key?.Trim() ?? "";
        if (key.Length is < 2 or > 48 || !KeyPattern().IsMatch(key))
        {
            errors["key"] = ["2–48 characters: lower-case letters, digits and single hyphens, starting with a letter (e.g. invoice-standard)."];
        }

        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 200)
        {
            errors["name"] = ["1–200 characters."];
        }

        if (request.Description is { Length: > 1000 })
        {
            errors["description"] = ["At most 1000 characters."];
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        if (await db.Workflows.AnyAsync(w => w.Key == key, ct))
        {
            return AppError.Conflict("workflow.key_taken", $"A workflow with key '{key}' already exists.");
        }

        var now = clock.GetUtcNow();
        var workflow = Workflow.Create(key, name, string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(), InitialDefinition, user.Id, now);
        db.Workflows.Add(workflow);
        Audit(Actions.Created, workflow.Id, now, new { key });
        await db.SaveChangesAsync(ct);
        return ToDto(workflow);
    }

    public async Task<PageResult<WorkflowSummaryDto>> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.Workflows.AsNoTracking();
        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderBy(w => w.Key)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(w => new
            {
                w.Id,
                w.Key,
                w.Name,
                LatestPublished = w.Versions.Where(v => v.Status == WorkflowVersionStatus.Published).Max(v => v.VersionNumber),
                HasDraft = w.Versions.Any(v => v.Status == WorkflowVersionStatus.Draft),
                UpdatedAt = w.Versions.Max(v => v.UpdatedAt),
            })
            .ToListAsync(ct);
        return new PageResult<WorkflowSummaryDto>([.. rows.Select(r => new WorkflowSummaryDto(r.Id, r.Key, r.Name, r.LatestPublished, r.HasDraft, r.UpdatedAt))], page, pageSize, total);
    }

    public async Task<Result<WorkflowDto>> GetAsync(Guid id, CancellationToken ct) =>
        await LoadAsync(id, ct) is { } w ? ToDto(w) : NotFound(id);

    public async Task<Result<IReadOnlyList<WorkflowVersionSummaryDto>>> ListVersionsAsync(Guid id, CancellationToken ct)
    {
        if (await LoadAsync(id, ct) is not { } workflow)
        {
            return NotFound(id);
        }

        IReadOnlyList<WorkflowVersionSummaryDto> versions = [.. workflow.Versions
            .OrderBy(v => v.Status == WorkflowVersionStatus.Draft ? 0 : 1)
            .ThenByDescending(v => v.VersionNumber)
            .Select(v => new WorkflowVersionSummaryDto(v.Id, v.VersionNumber, v.Status.ToString(), v.ContentHash, v.UpdatedAt, v.UpdatedBy, v.PublishedAt, v.PublishedBy))];
        return Result.Success(versions);
    }

    public async Task<Result<Versioned<WorkflowVersionDto>>> GetVersionAsync(Guid id, Guid versionId, CancellationToken ct)
    {
        if (await LoadAsync(id, ct) is not { } workflow)
        {
            return NotFound(id);
        }

        return workflow.Versions.SingleOrDefault(v => v.Id == versionId) is { } version
            ? ToVersioned(version)
            : AppError.NotFound("workflow.version_not_found", $"Version {versionId} does not belong to workflow {id}.");
    }

    public async Task<Result<Versioned<WorkflowVersionDto>>> GetDraftAsync(Guid id, CancellationToken ct)
    {
        if (await LoadAsync(id, ct) is not { } workflow)
        {
            return NotFound(id);
        }

        return workflow.Draft is { } draft ? ToVersioned(draft) : NoDraft();
    }

    public async Task<Result<Versioned<WorkflowVersionDto>>> StartDraftAsync(Guid id, CancellationToken ct)
    {
        if (await LoadAsync(id, ct) is not { } workflow)
        {
            return NotFound(id);
        }

        if (workflow.Draft is not null)
        {
            return AppError.Conflict("workflow.draft_exists", "This workflow already has a draft. Edit it, or publish it first.");
        }

        if (workflow.LatestPublished is null)
        {
            return AppError.Conflict("workflow.nothing_published", "There is no published version to start a draft from.");
        }

        var now = clock.GetUtcNow();
        var draft = workflow.StartDraft(user.Id, now);
        Audit(Actions.DraftCreated, workflow.Id, now, new { versionId = draft.Id, fromVersion = workflow.LatestPublished.VersionNumber });
        await db.SaveChangesAsync(ct);
        return ToVersioned(draft);
    }

    /// <summary>Saves the definition. Only its structure is checked here; graph issues don't block saving a draft.</summary>
    public async Task<Result<Versioned<WorkflowVersionDto>>> SaveDraftAsync(Guid id, string? ifMatch, SaveWorkflowDraftRequest request, CancellationToken ct)
    {
        if (await LoadAsync(id, ct) is not { } workflow)
        {
            return NotFound(id);
        }

        if (workflow.Draft is not { } draft)
        {
            return NoDraft();
        }

        if (ETag.Check(ifMatch, draft.RowVersion, out var expected) is { } preconditionError)
        {
            return preconditionError;
        }

        if (request.Definition is not { } definition)
        {
            return AppError.Validation(new Dictionary<string, string[]> { ["definition"] = ["Required."] });
        }

        var (_, errors) = WorkflowDefinitionParser.Parse(definition);
        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var now = clock.GetUtcNow();
        workflow.SaveDraft(definition.GetRawText(), user.Id, now);
        db.SetOriginalRowVersion(draft, expected);
        Audit(Actions.DraftSaved, workflow.Id, now, new { versionId = draft.Id });
        return await SaveWithConcurrencyAsync(draft, ct);
    }

    /// <summary>Validates the supplied definition (unsaved edits) or, without one, the saved draft.</summary>
    public async Task<Result<WorkflowValidationDto>> ValidateAsync(Guid id, ValidateWorkflowRequest? request, CancellationToken ct)
    {
        if (await LoadAsync(id, ct) is not { } workflow)
        {
            return NotFound(id);
        }

        ParsedDefinition? parsed;
        IReadOnlyDictionary<string, string[]> errors;
        if (request?.Definition is { } supplied)
        {
            (parsed, errors) = WorkflowDefinitionParser.Parse(supplied);
        }
        else if (workflow.Draft is { } draft)
        {
            (parsed, errors) = WorkflowDefinitionParser.Parse(draft.Definition);
        }
        else
        {
            return NoDraft();
        }

        if (parsed is null)
        {
            return AppError.Validation(errors);
        }

        return await ValidateGraphAsync(parsed, ct);
    }

    public async Task<Result<Versioned<WorkflowVersionDto>>> PublishAsync(Guid id, string? ifMatch, CancellationToken ct)
    {
        if (await LoadAsync(id, ct) is not { } workflow)
        {
            return NotFound(id);
        }

        if (workflow.Draft is not { } draft)
        {
            return NoDraft();
        }

        if (ETag.Check(ifMatch, draft.RowVersion, out var expected) is { } preconditionError)
        {
            return preconditionError;
        }

        var (parsed, structure) = WorkflowDefinitionParser.Parse(draft.Definition);
        var validation = parsed is null ? null : await ValidateGraphAsync(parsed, ct);
        if (parsed is null || !validation!.Valid)
        {
            var issues = validation?.Issues ?? [.. structure.Select(e => new WorkflowIssueDto(GraphValidator.Error, "definition.invalid", $"{e.Key}: {e.Value[0]}"))];
            return new AppError(ErrorKind.Conflict, "workflow.invalid",
                $"The workflow has {issues.Count(i => i.Severity == GraphValidator.Error)} error(s) and can't be published. Fix them and try again.",
                Extensions: new Dictionary<string, object?> { ["issues"] = issues });
        }

        // Paths typed into Folder input steps are recorded (and audited) so the Worker can resolve them; same save as the publish.
        await new Ingestion.FolderPathRegistry(db, user, clock).RegisterAsync(
            parsed.Nodes.Where(n => n.Type == NodeCatalog.FolderInput).SelectMany(n => Ingestion.FolderInputSettings.TypedPaths(n.Config)), ct);

        var now = clock.GetUtcNow();
        var published = workflow.Publish(CanonicalJson.Sha256Hex(draft.Definition), user.Id, now);
        db.SetOriginalRowVersion(draft, expected);
        Audit(Actions.Published, workflow.Id, now, new { versionId = published.Id, versionNumber = published.VersionNumber, contentHash = published.ContentHash });
        return await SaveWithConcurrencyAsync(published, ct);
    }

    private async Task<WorkflowValidationDto> ValidateGraphAsync(ParsedDefinition definition, CancellationToken ct)
    {
        // Load only the templates the graph refers to: their names and published version numbers.
        var ids = definition.Nodes
            .Where(n => NodeCatalog.IsDocumentStep(n.Type) && n.Config.TryGetProperty("templateId", out var t) && t.ValueKind == JsonValueKind.String)
            .Select(n => Guid.TryParse(n.Config.GetProperty("templateId").GetString(), out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct()
            .ToList();
        var needsModel = definition.Nodes.Any(n => n.Type == NodeCatalog.MapFields); // the data model is only read for the Map fields check
        var rows = ids.Count == 0
            ? []
            : await db.Templates.AsNoTracking()
                .Where(t => ids.Contains(t.Id))
                .Select(t => new
                {
                    t.Id,
                    t.Name,
                    Published = t.Versions.Where(v => v.Status == TemplateVersionStatus.Published).Select(v => v.VersionNumber!.Value).ToList(),
                    Model = needsModel
                        ? t.Versions.Where(v => v.Status == TemplateVersionStatus.Published).OrderByDescending(v => v.VersionNumber).Select(v => v.Content.DataSchema).FirstOrDefault()
                        : null,
                })
                .ToListAsync(ct);
        var templates = rows.ToDictionary(r => r.Id, r => new TemplateRef(r.Id, r.Name, r.Published.ToHashSet(), Executions.FieldMapper.TemplateFields(r.Model)));

        // Approved folder locations (reported by the Worker), only when a folder-input step needs them.
        var locations = definition.Nodes.Any(n => n.Type == NodeCatalog.FolderInput)
            ? await db.FolderLocations.AsNoTracking()
                .Select(l => new FolderLocationRef(l.Key, l.Name, l.AllowInput, l.AllowOutput, l.Enabled))
                .ToDictionaryAsync(l => l.Key, StringComparer.Ordinal, ct)
            : [];
        return GraphValidator.Validate(definition, templates, locations);
    }

    private async Task<Result<Versioned<WorkflowVersionDto>>> SaveWithConcurrencyAsync(WorkflowVersion version, CancellationToken ct)
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

    private Task<Workflow?> LoadAsync(Guid id, CancellationToken ct) =>
        db.Workflows.Include(w => w.Versions).SingleOrDefaultAsync(w => w.Id == id, ct);

    private void Audit(string action, Guid workflowId, DateTimeOffset now, object details) =>
        db.AuditEntries.Add(AuditEntry.Record(
            action, EntityType, workflowId.ToString(), user.Id, user.Name, now,
            JsonSerializer.Serialize(details), Activity.Current?.TraceId.ToHexString()));

    private static AppError NotFound(Guid id) => AppError.NotFound("workflow.not_found", $"Workflow {id} was not found.");

    private static AppError NoDraft() => AppError.Conflict("workflow.no_draft",
        "This workflow has no draft. Published versions cannot be changed; start a new draft with POST …/draft.");

    private static WorkflowDto ToDto(Workflow w) =>
        new(w.Id, w.Key, w.Name, w.Description, w.CreatedAt, w.CreatedBy, w.LatestPublished?.VersionNumber, w.Draft?.Id);

    private static Versioned<WorkflowVersionDto> ToVersioned(WorkflowVersion v)
    {
        using var document = JsonDocument.Parse(v.Definition);
        var dto = new WorkflowVersionDto(
            v.Id, v.WorkflowId, v.VersionNumber, v.Status.ToString(), document.RootElement.Clone(), v.ContentHash,
            v.CreatedAt, v.CreatedBy, v.UpdatedAt, v.UpdatedBy, v.PublishedAt, v.PublishedBy);
        return new Versioned<WorkflowVersionDto>(dto, ETag.From(v.RowVersion));
    }
}
