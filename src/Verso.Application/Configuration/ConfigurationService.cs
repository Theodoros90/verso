using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Verso.Application.Templates;
using Verso.Application.Workflows;
using Verso.Domain.Audit;
using Verso.Domain.Templates;
using Verso.Domain.Workflows;

namespace Verso.Application.Configuration;

// Request and response shapes of the configuration API (docs/api.md#configuration, ADR 0010).

public sealed record ConfigurationExportRequest(IReadOnlyList<Guid>? TemplateIds, IReadOnlyList<Guid>? WorkflowIds, string? Environment);

/// <summary>A template as carried between environments: its latest published version, or its draft when nothing is published.</summary>
public sealed record PackageTemplate(
    string? Key, string? Name, string? Description, string? DocumentType, bool Published,
    PageSettingsDto? PageSettings, JsonElement? Layout, JsonElement? DataSchema, JsonElement? SampleData);

/// <summary>A workflow as carried between environments. Document steps name their template by key (<c>config.templateKey</c>), not by id.</summary>
public sealed record PackageWorkflow(string? Key, string? Name, string? Description, JsonElement? Definition);

public sealed record ConfigurationPackage(
    string? Product, int SchemaVersion, string? Environment, DateTimeOffset? ExportedAt,
    IReadOnlyList<PackageTemplate>? Templates, IReadOnlyList<PackageWorkflow>? Workflows);

public sealed record ConfigurationImportRequest(ConfigurationPackage? Package, bool IncludeWorkflows = true);

/// <param name="Kind"><c>template</c> or <c>workflow</c>.</param>
/// <param name="Outcome"><c>created</c>, <c>updated</c>, <c>unchanged</c> or <c>skipped</c> (see <paramref name="Message"/>).</param>
public sealed record ImportItemResult(string Kind, string Key, string Name, string Outcome, string? Message);

public sealed record ConfigurationImportResult(bool DryRun, IReadOnlyList<ImportItemResult> Items, IReadOnlyList<string> Warnings);

/// <summary>
/// Moves templates and workflows between environments as one package. Templates and workflows are matched by key; nothing is
/// ever deleted or overwritten in place: published versions stay frozen, and an item with an unpublished draft is skipped.
/// Connections and global variables are not stored by the server and travel in the same file, handled by the SPA.
/// </summary>
public sealed class ConfigurationService(IVersoDbContext db, ICurrentUser user, TimeProvider clock)
{
    public const string Product = "Verso configuration package";
    public const int SchemaVersion = 1;
    public const string EntityType = "Configuration";
    public const int MaxItems = 200;
    public static readonly string[] Environments = ["Development", "Test", "Production"];

    public static class Actions
    {
        public const string Exported = "ConfigurationExported";
        public const string Imported = "ConfigurationImported";
    }

    public async Task<Result<ConfigurationPackage>> ExportAsync(ConfigurationExportRequest request, CancellationToken ct)
    {
        var templateIds = (request.TemplateIds ?? []).Distinct().ToList();
        var workflowIds = (request.WorkflowIds ?? []).Distinct().ToList();
        if (request.Environment is not null && !Environments.Contains(request.Environment))
        {
            return AppError.Validation(new Dictionary<string, string[]> { ["environment"] = [$"Use one of: {string.Join(", ", Environments)}."] });
        }

        if (templateIds.Count > MaxItems || workflowIds.Count > MaxItems)
        {
            return AppError.Validation(new Dictionary<string, string[]> { ["templateIds"] = [$"At most {MaxItems} templates and {MaxItems} workflows."] });
        }

        var templates = await db.Templates.AsNoTracking().Include(t => t.Versions).Where(t => templateIds.Contains(t.Id)).OrderBy(t => t.Key).ToListAsync(ct);
        var missing = templateIds.Except(templates.Select(t => t.Id)).ToList();
        if (missing.Count > 0)
        {
            return AppError.NotFound("configuration.item_not_found", $"Template {missing[0]} was not found.");
        }

        var workflows = await db.Workflows.AsNoTracking().Include(w => w.Versions).Where(w => workflowIds.Contains(w.Id)).OrderBy(w => w.Key).ToListAsync(ct);
        var missingWorkflow = workflowIds.Except(workflows.Select(w => w.Id)).ToList();
        if (missingWorkflow.Count > 0)
        {
            return AppError.NotFound("configuration.item_not_found", $"Workflow {missingWorkflow[0]} was not found.");
        }

        // Template ids differ between environments, so workflows carry template keys.
        var keysById = await db.Templates.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Key, ct);

        var packageTemplates = templates.Select(t =>
        {
            var source = t.LatestPublished ?? t.Draft!;
            var p = source.Content.PageSettings;
            return new PackageTemplate(
                t.Key, t.Name, t.Description, t.DocumentType.ToString(), t.LatestPublished is not null,
                new PageSettingsDto(p.Size, p.Orientation, new MarginsDto(p.MarginTopMm, p.MarginRightMm, p.MarginBottomMm, p.MarginLeftMm)),
                Parse(source.Content.Layout), Parse(source.Content.DataSchema), Parse(source.Content.SampleData));
        }).ToList();

        var packageWorkflows = workflows.Select(w =>
        {
            var source = w.LatestPublished ?? w.Draft!;
            return new PackageWorkflow(w.Key, w.Name, w.Description, RewriteTemplateReferences(source.Definition, id => keysById.GetValueOrDefault(id)));
        }).ToList();

        var now = clock.GetUtcNow();
        db.AuditEntries.Add(AuditEntry.Record(
            Actions.Exported, EntityType, "package", user.Id, user.Name, now,
            JsonSerializer.Serialize(new { environment = request.Environment, templates = packageTemplates.Count, workflows = packageWorkflows.Count }),
            Activity.Current?.TraceId.ToHexString()));
        await db.SaveChangesAsync(ct);

        return new ConfigurationPackage(Product, SchemaVersion, request.Environment, now, packageTemplates, packageWorkflows);
    }

    /// <summary>
    /// Checks the package and, unless <paramref name="dryRun"/>, applies it in one transaction: either everything is
    /// imported or (on any invalid item) nothing is. A dry run reports exactly what an import would do.
    /// </summary>
    public async Task<Result<ConfigurationImportResult>> ImportAsync(ConfigurationImportRequest request, bool dryRun, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var package = request.Package;
        if (package is null || package.Product != Product)
        {
            return AppError.Validation(new Dictionary<string, string[]> { ["package"] = ["This file is not a Verso configuration package."] });
        }

        if (package.SchemaVersion != SchemaVersion)
        {
            return AppError.Validation(new Dictionary<string, string[]> { ["package.schemaVersion"] = [$"Unsupported package version {package.SchemaVersion}."] });
        }

        var templates = package.Templates ?? [];
        var workflows = request.IncludeWorkflows ? package.Workflows ?? [] : [];
        if (templates.Count > MaxItems || workflows.Count > MaxItems)
        {
            return AppError.Validation(new Dictionary<string, string[]> { ["package"] = [$"At most {MaxItems} templates and {MaxItems} workflows per package."] });
        }

        var contents = new TemplateContent?[templates.Count];
        var types = new DocumentType[templates.Count];
        for (var i = 0; i < templates.Count; i++)
        {
            ValidateTemplate(templates[i], i, errors, out contents[i], out types[i]);
        }

        var definitions = new JsonElement[workflows.Count];
        for (var i = 0; i < workflows.Count; i++)
        {
            ValidateWorkflow(workflows[i], i, errors, out definitions[i]);
        }

        FlagDuplicates(templates.Select(t => t.Key), "templates", errors);
        FlagDuplicates(workflows.Select(w => w.Key), "workflows", errors);
        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var now = clock.GetUtcNow();
        var results = new List<ImportItemResult>();
        var warnings = new List<string>();

        var keys = templates.Select(t => t.Key!).ToList();
        var existingTemplates = await db.Templates.Include(t => t.Versions).Where(t => keys.Contains(t.Key)).ToDictionaryAsync(t => t.Key, ct);
        var idsByKey = await db.Templates.AsNoTracking().ToDictionaryAsync(t => t.Key, t => t.Id, ct);

        for (var i = 0; i < templates.Count; i++)
        {
            var item = templates[i];
            var content = contents[i]!;
            var key = item.Key!;
            if (!existingTemplates.TryGetValue(key, out var template))
            {
                template = Template.Create(key, item.Name!.Trim(), Blank(item.Description), types[i], content, user.Id, now);
                db.Templates.Add(template);
                Audit(TemplateService.EntityType, TemplateService.Actions.Created, template.Id, now, new { key, viaImport = true });
                if (item.Published)
                {
                    Publish(template, content, now);
                }

                idsByKey[key] = template.Id;
                results.Add(new("template", key, item.Name!, "created", item.Published ? "Published as version 1." : "Created as a draft."));
                continue;
            }

            if (template.Draft is not null)
            {
                results.Add(new("template", key, template.Name, "skipped", "It has an unpublished draft here. Publish the draft first, then import again."));
                continue;
            }

            var hash = TemplateService.ComputeContentHash(content);
            if (item.Published && template.LatestPublished?.ContentHash == hash)
            {
                results.Add(new("template", key, template.Name, "unchanged", $"Version {template.LatestPublished.VersionNumber} already has this content."));
                continue;
            }

            var draft = template.StartDraft(user.Id, now);
            Audit(TemplateService.EntityType, TemplateService.Actions.DraftCreated, template.Id, now, new { versionId = draft.Id, fromVersion = template.LatestPublished!.VersionNumber, viaImport = true });
            template.SaveDraft(content, user.Id, now);
            Audit(TemplateService.EntityType, TemplateService.Actions.DraftSaved, template.Id, now, new { versionId = draft.Id, viaImport = true });
            if (item.Published)
            {
                var published = Publish(template, content, now);
                results.Add(new("template", key, template.Name, "updated", $"Published as version {published.VersionNumber}."));
            }
            else
            {
                results.Add(new("template", key, template.Name, "updated", "Saved as a draft."));
            }
        }

        var workflowKeys = workflows.Select(w => w.Key!.Trim()).ToList();
        var existingWorkflows = await db.Workflows.Include(w => w.Versions).Where(w => workflowKeys.Contains(w.Key)).ToDictionaryAsync(w => w.Key, ct);
        for (var i = 0; i < workflows.Count; i++)
        {
            var item = workflows[i];
            var key = item.Key!.Trim();
            var unresolved = new HashSet<string>(StringComparer.Ordinal);
            var json = RewriteTemplateReferences(definitions[i].GetRawText(), idsByKey, unresolved);
            foreach (var missingKey in unresolved)
            {
                warnings.Add($"Workflow '{key}' uses template '{missingKey}', which does not exist here. Choose a template in that step.");
            }

            if (!existingWorkflows.TryGetValue(key, out var workflow))
            {
                workflow = Workflow.Create(key, item.Name!.Trim(), Blank(item.Description), json, user.Id, now);
                db.Workflows.Add(workflow);
                Audit(WorkflowService.EntityType, WorkflowService.Actions.Created, workflow.Id, now, new { key, viaImport = true });
                results.Add(new("workflow", key, item.Name!, "created", "Created as a draft. Review it and publish it here."));
                continue;
            }

            if (workflow.Draft is not null)
            {
                results.Add(new("workflow", key, workflow.Name, "skipped", "It has an unpublished draft here. Publish the draft first, then import again."));
                continue;
            }

            if (workflow.LatestPublished is { } latest && CanonicalJson.Sha256Hex(latest.Definition) == CanonicalJson.Sha256Hex(json))
            {
                results.Add(new("workflow", key, workflow.Name, "unchanged", $"Version {latest.VersionNumber} already has this graph."));
                continue;
            }

            var draft = workflow.StartDraft(user.Id, now);
            Audit(WorkflowService.EntityType, WorkflowService.Actions.DraftCreated, workflow.Id, now, new { versionId = draft.Id, fromVersion = workflow.LatestPublished!.VersionNumber, viaImport = true });
            workflow.SaveDraft(json, user.Id, now);
            Audit(WorkflowService.EntityType, WorkflowService.Actions.DraftSaved, workflow.Id, now, new { versionId = draft.Id, viaImport = true });
            results.Add(new("workflow", key, workflow.Name, "updated", "Saved as a new draft. Review it and publish it here."));
        }

        if (!dryRun)
        {
            Audit(EntityType, Actions.Imported, null, now, new
            {
                environment = package.Environment,
                templates = results.Count(r => r.Kind == "template" && r.Outcome is "created" or "updated"),
                workflows = results.Count(r => r.Kind == "workflow" && r.Outcome is "created" or "updated"),
                skipped = results.Count(r => r.Outcome == "skipped"),
            });
            await db.SaveChangesAsync(ct);
        }

        return new ConfigurationImportResult(dryRun, results, warnings);
    }

    private TemplateVersion Publish(Template template, TemplateContent content, DateTimeOffset now)
    {
        var published = template.Publish(TemplateService.ComputeContentHash(content), user.Id, now);
        Audit(TemplateService.EntityType, TemplateService.Actions.Published, template.Id, now,
            new { versionId = published.Id, versionNumber = published.VersionNumber, contentHash = published.ContentHash, viaImport = true });
        return published;
    }

    private static void ValidateTemplate(PackageTemplate item, int index, Dictionary<string, string[]> errors, out TemplateContent? content, out DocumentType type)
    {
        var path = $"package.templates[{index}]";
        content = null;
        if (TemplateValidation.ValidateCreate(new CreateTemplateRequest(item.Key, item.Name, item.Description, item.DocumentType), out type) is { } identity)
        {
            foreach (var (field, messages) in identity)
            {
                errors[$"{path}.{field}"] = messages;
            }
        }

        if (TemplateValidation.ValidateContent(new SaveDraftRequest(item.PageSettings, item.Layout, item.DataSchema, item.SampleData), out content) is { } contentErrors)
        {
            foreach (var (field, messages) in contentErrors)
            {
                errors[$"{path}.{field}"] = messages;
            }
        }
    }

    private static void ValidateWorkflow(PackageWorkflow item, int index, Dictionary<string, string[]> errors, out JsonElement definition)
    {
        var path = $"package.workflows[{index}]";
        definition = default;
        foreach (var (field, messages) in WorkflowService.ValidateIdentity(item.Key, item.Name))
        {
            errors[$"{path}.{field}"] = messages;
        }

        if (item.Description is { Length: > 1000 })
        {
            errors[$"{path}.description"] = ["At most 1000 characters."];
        }

        if (item.Definition is not { } element)
        {
            errors[$"{path}.definition"] = ["Required."];
            return;
        }

        var (_, definitionErrors) = WorkflowDefinitionParser.Parse(element);
        foreach (var (field, messages) in definitionErrors)
        {
            errors[$"{path}.{field}"] = messages;
        }

        definition = element;
    }

    private static void FlagDuplicates(IEnumerable<string?> keys, string section, Dictionary<string, string[]> errors)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, index) in keys.Select((k, i) => (k?.Trim(), i)))
        {
            if (!string.IsNullOrEmpty(key) && !seen.Add(key))
            {
                errors[$"package.{section}[{index}].key"] = [$"'{key}' appears twice in the package."];
            }
        }
    }

    /// <summary>Document steps point at a template by id here and by key in a package; this swaps one for the other.</summary>
    private static JsonElement RewriteTemplateReferences(string definition, Func<Guid, string?> keyOf)
    {
        var root = JsonNode.Parse(definition)!;
        foreach (var config in Configs(root))
        {
            if (config["templateId"] is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id))
            {
                config.Remove("templateId");
                if (keyOf(id) is { } key)
                {
                    config["templateKey"] = key;
                }
            }
        }

        return JsonSerializer.SerializeToElement(root);
    }

    private static string RewriteTemplateReferences(string definition, Dictionary<string, Guid> idsByKey, HashSet<string> unresolved)
    {
        var root = JsonNode.Parse(definition)!;
        foreach (var config in Configs(root))
        {
            if (config["templateKey"] is JsonValue value && value.TryGetValue<string>(out var key))
            {
                config.Remove("templateKey");
                if (idsByKey.TryGetValue(key, out var id))
                {
                    config["templateId"] = id.ToString();
                }
                else
                {
                    unresolved.Add(key);
                }
            }
        }

        return root.ToJsonString();
    }

    private static IEnumerable<JsonObject> Configs(JsonNode root) =>
        (root["nodes"] as JsonArray ?? []).Select(n => n?["config"] as JsonObject).OfType<JsonObject>();

    private void Audit(string entityType, string action, Guid? entityId, DateTimeOffset now, object details) =>
        db.AuditEntries.Add(AuditEntry.Record(
            action, entityType, entityId?.ToString() ?? "package", user.Id, user.Name, now,
            JsonSerializer.Serialize(details), Activity.Current?.TraceId.ToHexString()));

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
