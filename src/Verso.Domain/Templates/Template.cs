namespace Verso.Domain.Templates;

public enum DocumentType
{
    Invoice,
}

/// <summary>
/// A document template and its versions. Invariants: at most one draft at a time; published versions are
/// never modified; version numbers are assigned on publish, starting at 1, without gaps.
/// </summary>
public sealed class Template
{
    private readonly List<TemplateVersion> _versions = [];

    private Template() { } // EF Core

    public Guid Id { get; private set; }

    /// <summary>Stable business key, e.g. <c>INV-STD</c>. Unique; never changes.</summary>
    public string Key { get; private set; } = "";

    public string Name { get; private set; } = "";
    public string? Description { get; private set; }
    public DocumentType DocumentType { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; } = "";

    public IReadOnlyList<TemplateVersion> Versions => _versions;

    public TemplateVersion? Draft => _versions.SingleOrDefault(v => v.Status == TemplateVersionStatus.Draft);

    public TemplateVersion? LatestPublished => _versions
        .Where(v => v.Status == TemplateVersionStatus.Published)
        .MaxBy(v => v.VersionNumber);

    /// <summary>Creates a template with an initial draft holding <paramref name="initialContent"/>.</summary>
    public static Template Create(
        string key, string name, string? description, DocumentType documentType,
        TemplateContent initialContent, string actor, DateTimeOffset now)
    {
        var template = new Template
        {
            Id = Guid.CreateVersion7(now),
            Key = key,
            Name = name,
            Description = description,
            DocumentType = documentType,
            CreatedAt = now,
            CreatedBy = actor,
        };
        template._versions.Add(new TemplateVersion(template.Id, initialContent, actor, now));
        return template;
    }

    /// <summary>Starts a new draft from the latest published version.</summary>
    public TemplateVersion StartDraft(string actor, DateTimeOffset now)
    {
        if (Draft is not null)
        {
            throw new DomainRuleException("template.draft_exists", "This template already has a draft.");
        }

        var source = LatestPublished
            ?? throw new DomainRuleException("template.nothing_published", "There is no published version to start a draft from.");
        var draft = new TemplateVersion(Id, source.Content, actor, now);
        _versions.Add(draft);
        return draft;
    }

    public TemplateVersion SaveDraft(TemplateContent content, string actor, DateTimeOffset now)
    {
        var draft = Draft ?? throw NoDraft();
        draft.Update(content, actor, now);
        return draft;
    }

    /// <summary>Freezes the draft as the next version number.</summary>
    public TemplateVersion Publish(string contentHash, string actor, DateTimeOffset now)
    {
        var draft = Draft ?? throw NoDraft();
        var next = (LatestPublished?.VersionNumber ?? 0) + 1;
        draft.MarkPublished(next, contentHash, actor, now);
        return draft;
    }

    private static DomainRuleException NoDraft() =>
        new("template.no_draft", "This template has no draft. Published versions cannot be changed; start a new draft instead.");
}
