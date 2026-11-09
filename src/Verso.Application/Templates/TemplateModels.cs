using System.Text.Json;

namespace Verso.Application.Templates;

// Request and response shapes of the template API. Documented in docs/api.md; changes must update it.

public sealed record CreateTemplateRequest(string? Key, string? Name, string? Description, string? DocumentType);

public sealed record MarginsDto(decimal Top, decimal Right, decimal Bottom, decimal Left);

public sealed record PageSettingsDto(string? Size, string? Orientation, MarginsDto? MarginsMm);

/// <summary>Full replacement of the draft content. All four parts are required.</summary>
public sealed record SaveDraftRequest(PageSettingsDto? PageSettings, JsonElement? Layout, JsonElement? DataSchema, JsonElement? SampleData);

public sealed record TemplateSummaryDto(
    Guid Id, string Key, string Name, string DocumentType, int? LatestPublishedVersion, bool HasDraft, DateTimeOffset UpdatedAt);

public sealed record TemplateDto(
    Guid Id, string Key, string Name, string? Description, string DocumentType,
    DateTimeOffset CreatedAt, string CreatedBy, int? LatestPublishedVersion, Guid? DraftVersionId);

public sealed record TemplateVersionSummaryDto(
    Guid VersionId, int? VersionNumber, string Status, string? ContentHash,
    DateTimeOffset UpdatedAt, string UpdatedBy, DateTimeOffset? PublishedAt, string? PublishedBy);

public sealed record TemplateVersionDto(
    Guid VersionId, Guid TemplateId, int? VersionNumber, string Status,
    PageSettingsDto PageSettings, JsonElement Layout, JsonElement DataSchema, JsonElement SampleData,
    string? ContentHash, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, string UpdatedBy,
    DateTimeOffset? PublishedAt, string? PublishedBy);

public sealed record PageResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);
