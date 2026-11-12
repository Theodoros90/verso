using Microsoft.EntityFrameworkCore;
using Verso.Application;
using Verso.Domain.Audit;
using Verso.Domain.Documents;
using Verso.Domain.Executions;
using Verso.Domain.Ingestion;
using Verso.Domain.Templates;
using Verso.Domain.Workflows;

namespace Verso.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context for Verso. Each module keeps its tables in its own SQL schema
/// (see <see cref="Schemas"/>); mappings live in Persistence/Configurations.
/// </summary>
public sealed class VersoDbContext(DbContextOptions<VersoDbContext> options) : DbContext(options), IVersoDbContext
{
    public DbSet<Template> Templates => Set<Template>();
    public DbSet<TemplateVersion> TemplateVersions => Set<TemplateVersion>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<PdfPreview> PdfPreviews => Set<PdfPreview>();
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<WorkflowVersion> WorkflowVersions => Set<WorkflowVersion>();
    public DbSet<Execution> Executions => Set<Execution>();
    public DbSet<ExecutionEvent> ExecutionEvents => Set<ExecutionEvent>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<ArchiveRecord> ArchiveRecords => Set<ArchiveRecord>();
    public DbSet<FolderLocation> FolderLocations => Set<FolderLocation>();
    public DbSet<TypedFolderPath> TypedFolderPaths => Set<TypedFolderPath>();
    public DbSet<FolderWatch> FolderWatches => Set<FolderWatch>();
    public DbSet<FolderFile> FolderFiles => Set<FolderFile>();
    public DbSet<FolderAccessTest> FolderAccessTests => Set<FolderAccessTest>();

    public void SetOriginalRowVersion(TemplateVersion version, byte[] rowVersion) =>
        Entry(version).Property(v => v.RowVersion).OriginalValue = rowVersion;

    public void SetOriginalRowVersion(WorkflowVersion version, byte[] rowVersion) =>
        Entry(version).Property(v => v.RowVersion).OriginalValue = rowVersion;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(VersoDbContext).Assembly);
    }
}
