namespace Verso.Domain.Templates;

public enum PdfPreviewStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
}

/// <summary>
/// A PDF rendering of a template draft, produced by the Worker. Holds a snapshot of what to render (taken when it
/// was requested, so later draft edits don't change it) and, when done, the PDF. Temporary: deleted after a day.
/// </summary>
public sealed class PdfPreview
{
    public const int MaxErrorLength = 1000;

    private PdfPreview() { } // EF Core

    public PdfPreview(Guid templateId, Guid templateVersionId, string templateName, PageSettings pageSettings, string layout, string data, string actor, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7(now);
        TemplateId = templateId;
        TemplateVersionId = templateVersionId;
        TemplateName = templateName;
        PageSettings = pageSettings;
        Layout = layout;
        Data = data;
        Status = PdfPreviewStatus.Queued;
        CreatedAt = now;
        CreatedBy = actor;
    }

    public Guid Id { get; private set; }
    public Guid TemplateId { get; private set; }
    public Guid TemplateVersionId { get; private set; }
    public string TemplateName { get; private set; } = "";
    public PageSettings PageSettings { get; private set; } = PageSettings.DefaultA4;
    public string Layout { get; private set; } = "";

    /// <summary>The (synthetic) data rendered: the request's data or the draft's sample data at request time.</summary>
    public string Data { get; private set; } = "";

    public PdfPreviewStatus Status { get; private set; }
    public byte[]? Content { get; private set; }

    /// <summary>Render issues as a JSON array (same shape as the HTML preview's issues).</summary>
    public string Issues { get; private set; } = "[]";

    public string? Error { get; private set; }

    /// <summary>Render attempts so far (a technical failure is retried).</summary>
    public int Attempts { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>When the API relayed the "ready" notification (null until then).</summary>
    public DateTimeOffset? NotifiedAt { get; private set; }

    public bool IsFinished => Status is PdfPreviewStatus.Succeeded or PdfPreviewStatus.Failed;

    public void Start(DateTimeOffset now)
    {
        EnsureNotFinished();
        Status = PdfPreviewStatus.Running;
        Attempts++;
        StartedAt = now;
    }

    public void Succeed(byte[] content, string issuesJson, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(content);
        EnsureNotFinished();
        Status = PdfPreviewStatus.Succeeded;
        Content = content;
        Issues = issuesJson;
        Error = null;
        CompletedAt = now;
    }

    public void Fail(string error, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(error);
        EnsureNotFinished();
        Status = PdfPreviewStatus.Failed;
        Error = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        CompletedAt = now;
    }

    /// <summary>Records a failed attempt that will be retried.</summary>
    public void AttemptFailed(string error)
    {
        ArgumentNullException.ThrowIfNull(error);
        EnsureNotFinished();
        Status = PdfPreviewStatus.Queued;
        Error = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
    }

    public void MarkNotified(DateTimeOffset now) => NotifiedAt = now;

    private void EnsureNotFinished()
    {
        if (IsFinished)
        {
            throw new DomainRuleException("preview.finished", $"Preview {Id} is already {Status}.");
        }
    }
}
