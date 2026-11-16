namespace Verso.Domain.Templates;

/// <summary>
/// A named group for templates (e.g. "Loan documents"). Deactivated categories stay on existing templates
/// but are not offered for new assignments. Name rules (length, uniqueness) are enforced by the service.
/// </summary>
public sealed class DocumentCategory
{
    private DocumentCategory() { } // EF Core

    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; } = "";

    public static DocumentCategory Create(string name, string actor, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        Name = name.Trim(),
        IsActive = true,
        CreatedAt = now,
        CreatedBy = actor,
    };

    public void Rename(string name) => Name = name.Trim();

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}
