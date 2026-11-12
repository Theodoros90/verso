namespace Verso.Domain.Workflows;

public enum WorkflowVersionStatus
{
    Draft,
    Published,
}

/// <summary>
/// A workflow and its versions. Same invariants as templates: at most one draft; published versions are never
/// modified; version numbers are assigned on publish, starting at 1, without gaps.
/// </summary>
public sealed class Workflow
{
    private readonly List<WorkflowVersion> _versions = [];

    private Workflow() { } // EF Core

    public Guid Id { get; private set; }

    /// <summary>Stable key used in the submission URL, e.g. <c>invoice-standard</c>. Unique; never changes.</summary>
    public string Key { get; private set; } = "";

    public string Name { get; private set; } = "";
    public string? Description { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; } = "";

    /// <summary>
    /// Whether the workflow accepts new runs (submissions) and, for a Folder input trigger, scans its folder. An inactive
    /// workflow is hidden from the default list but never deleted (its versions, runs, archive and audit stay); it can
    /// still be edited and published, and runs already started carry on.
    /// </summary>
    public bool IsActive { get; private set; } = true;

    public IReadOnlyList<WorkflowVersion> Versions => _versions;

    public WorkflowVersion? Draft => _versions.SingleOrDefault(v => v.Status == WorkflowVersionStatus.Draft);

    public WorkflowVersion? LatestPublished => _versions
        .Where(v => v.Status == WorkflowVersionStatus.Published)
        .MaxBy(v => v.VersionNumber);

    public static Workflow Create(string key, string name, string? description, string initialDefinition, string actor, DateTimeOffset now)
    {
        var workflow = new Workflow
        {
            Id = Guid.CreateVersion7(now),
            Key = key,
            Name = name,
            Description = description,
            CreatedAt = now,
            CreatedBy = actor,
        };
        workflow._versions.Add(new WorkflowVersion(workflow.Id, initialDefinition, actor, now));
        return workflow;
    }

    public WorkflowVersion StartDraft(string actor, DateTimeOffset now)
    {
        if (Draft is not null)
        {
            throw new DomainRuleException("workflow.draft_exists", "This workflow already has a draft.");
        }

        var source = LatestPublished
            ?? throw new DomainRuleException("workflow.nothing_published", "There is no published version to start a draft from.");
        var draft = new WorkflowVersion(Id, source.Definition, actor, now);
        _versions.Add(draft);
        return draft;
    }

    public WorkflowVersion SaveDraft(string definition, string actor, DateTimeOffset now)
    {
        var draft = Draft ?? throw NoDraft();
        draft.Update(definition, actor, now);
        return draft;
    }

    public WorkflowVersion Publish(string contentHash, string actor, DateTimeOffset now)
    {
        var draft = Draft ?? throw NoDraft();
        var next = (LatestPublished?.VersionNumber ?? 0) + 1;
        draft.MarkPublished(next, contentHash, actor, now);
        return draft;
    }

    /// <summary>Stops new runs. Returns false when it was already inactive.</summary>
    public bool Deactivate()
    {
        var changed = IsActive;
        IsActive = false;
        return changed;
    }

    /// <summary>Allows new runs again. Returns false when it was already active.</summary>
    public bool Activate()
    {
        var changed = !IsActive;
        IsActive = true;
        return changed;
    }

    private static DomainRuleException NoDraft() =>
        new("workflow.no_draft", "This workflow has no draft. Published versions cannot be changed; start a new draft instead.");
}

/// <summary>One version of a workflow: its graph definition (nodes and edges, as JSON).</summary>
public sealed class WorkflowVersion
{
    private WorkflowVersion() { } // EF Core

    internal WorkflowVersion(Guid workflowId, string definition, string actor, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7(now);
        WorkflowId = workflowId;
        Status = WorkflowVersionStatus.Draft;
        Definition = definition;
        CreatedAt = UpdatedAt = now;
        CreatedBy = UpdatedBy = actor;
    }

    public Guid Id { get; private set; }
    public Guid WorkflowId { get; private set; }
    public int? VersionNumber { get; private set; }
    public WorkflowVersionStatus Status { get; private set; }

    /// <summary><c>{ nodes: [{ id, type, title, position, config }], edges: [{ id, source, sourcePort, target, targetPort }] }</c></summary>
    public string Definition { get; private set; } = "";

    /// <summary>SHA-256 (hex) of the canonical definition; set on publish.</summary>
    public string? ContentHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset UpdatedAt { get; private set; }
    public string UpdatedBy { get; private set; } = "";
    public DateTimeOffset? PublishedAt { get; private set; }
    public string? PublishedBy { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    internal void Update(string definition, string actor, DateTimeOffset now)
    {
        EnsureDraft();
        Definition = definition;
        UpdatedAt = now;
        UpdatedBy = actor;
    }

    internal void MarkPublished(int versionNumber, string contentHash, string actor, DateTimeOffset now)
    {
        EnsureDraft();
        Status = WorkflowVersionStatus.Published;
        VersionNumber = versionNumber;
        ContentHash = contentHash;
        PublishedAt = UpdatedAt = now;
        PublishedBy = UpdatedBy = actor;
    }

    private void EnsureDraft()
    {
        if (Status != WorkflowVersionStatus.Draft)
        {
            throw new DomainRuleException("workflow.version_immutable", $"Version {VersionNumber} is published and cannot be changed.");
        }
    }
}
