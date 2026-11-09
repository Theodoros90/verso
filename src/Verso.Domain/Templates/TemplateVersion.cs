namespace Verso.Domain.Templates;

public enum TemplateVersionStatus
{
    Draft,
    Published,
}

/// <summary>
/// One version of a template. A draft is editable and has no version number; publishing assigns the
/// next number and freezes it for good.
/// </summary>
public sealed class TemplateVersion
{
    private TemplateVersion() { } // EF Core

    internal TemplateVersion(Guid templateId, TemplateContent content, string actor, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7(now);
        TemplateId = templateId;
        Status = TemplateVersionStatus.Draft;
        Content = content;
        CreatedAt = UpdatedAt = now;
        CreatedBy = UpdatedBy = actor;
    }

    public Guid Id { get; private set; }
    public Guid TemplateId { get; private set; }

    /// <summary>Null while a draft; 1, 2, 3… once published.</summary>
    public int? VersionNumber { get; private set; }

    public TemplateVersionStatus Status { get; private set; }
    public TemplateContent Content { get; private set; } = TemplateContent.Empty;

    /// <summary>SHA-256 (hex) of the canonical page settings, layout and data schema; set on publish.</summary>
    public string? ContentHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset UpdatedAt { get; private set; }
    public string UpdatedBy { get; private set; } = "";
    public DateTimeOffset? PublishedAt { get; private set; }
    public string? PublishedBy { get; private set; }

    /// <summary>Optimistic concurrency token (SQL Server rowversion); exposed to clients as the ETag.</summary>
    public byte[] RowVersion { get; private set; } = [];

    internal void Update(TemplateContent content, string actor, DateTimeOffset now)
    {
        EnsureDraft();
        Content = content;
        UpdatedAt = now;
        UpdatedBy = actor;
    }

    internal void MarkPublished(int versionNumber, string contentHash, string actor, DateTimeOffset now)
    {
        EnsureDraft();
        Status = TemplateVersionStatus.Published;
        VersionNumber = versionNumber;
        ContentHash = contentHash;
        PublishedAt = UpdatedAt = now;
        PublishedBy = UpdatedBy = actor;
    }

    private void EnsureDraft()
    {
        if (Status != TemplateVersionStatus.Draft)
        {
            throw new DomainRuleException("template.version_immutable", $"Version {VersionNumber} is published and cannot be changed.");
        }
    }
}
