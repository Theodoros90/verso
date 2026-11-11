using Microsoft.EntityFrameworkCore;
using Verso.Domain.Audit;
using Verso.Domain.Documents;
using Verso.Domain.Executions;
using Verso.Domain.Ingestion;
using Verso.Domain.Templates;
using Verso.Domain.Workflows;

namespace Verso.Application;

/// <summary>Persistence as seen by use cases. Implemented by the EF Core context in Infrastructure.</summary>
public interface IVersoDbContext
{
    DbSet<Template> Templates { get; }
    DbSet<TemplateVersion> TemplateVersions { get; }
    DbSet<AuditEntry> AuditEntries { get; }
    DbSet<PdfPreview> PdfPreviews { get; }
    DbSet<Workflow> Workflows { get; }
    DbSet<WorkflowVersion> WorkflowVersions { get; }
    DbSet<Execution> Executions { get; }
    DbSet<ExecutionEvent> ExecutionEvents { get; }
    DbSet<Document> Documents { get; }
    DbSet<ArchiveRecord> ArchiveRecords { get; }
    DbSet<FolderLocation> FolderLocations { get; }
    DbSet<ManagedFolderLocation> ManagedFolderLocations { get; }
    DbSet<FolderWatch> FolderWatches { get; }
    DbSet<FolderFile> FolderFiles { get; }
    DbSet<FolderAccessTest> FolderAccessTests { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Lets a use case discard tracked changes after a lost race (e.g. another Worker claimed the same file).</summary>
    Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker ChangeTracker { get; }

    /// <summary>Tells EF which rowversion the caller last saw, so a concurrent change fails the save.</summary>
    void SetOriginalRowVersion(TemplateVersion version, byte[] rowVersion);

    /// <inheritdoc cref="SetOriginalRowVersion(TemplateVersion, byte[])"/>
    void SetOriginalRowVersion(WorkflowVersion version, byte[] rowVersion);
}

/// <summary>The person (or service) performing the current request; recorded on changes and audit entries.</summary>
public interface ICurrentUser
{
    string Id { get; }
    string Name { get; }

    /// <summary>Verso roles of the person (see <see cref="Roles"/>). Until Entra ID exists these come from configuration.</summary>
    IReadOnlyCollection<string> Roles { get; }
}

/// <summary>The Worker's own identity, recorded on runs it starts by itself (folder input).</summary>
public sealed class WorkerUser : ICurrentUser
{
    public string Id => Ingestion.FolderIngestion.ActorId;
    public string Name => Ingestion.FolderIngestion.ActorName;
    public IReadOnlyCollection<string> Roles => [];
}

/// <summary>
/// Proposed roles (plan §7). M8 only enforces the payload restriction; the full endpoint × role matrix is M9.
/// </summary>
public static class Roles
{
    public const string Viewer = "Viewer";
    public const string Operator = "Operator";
    public const string TemplateAuthor = "TemplateAuthor";
    public const string WorkflowAuthor = "WorkflowAuthor";
    public const string Auditor = "Auditor";
    public const string Admin = "Admin";

    /// <summary>Submitted payloads may hold personal data: only auditors and admins read them (plan §7).</summary>
    public static bool CanReadPayloads(this ICurrentUser user) =>
        user.Roles.Contains(Auditor, StringComparer.OrdinalIgnoreCase) || user.Roles.Contains(Admin, StringComparer.OrdinalIgnoreCase);
}
