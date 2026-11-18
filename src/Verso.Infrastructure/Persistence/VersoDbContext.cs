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
    public DbSet<DocumentCategory> DocumentCategories => Set<DocumentCategory>();
    public DbSet<Verso.Domain.Mail.EmailRelay> EmailRelays => Set<Verso.Domain.Mail.EmailRelay>();
    public DbSet<Verso.Domain.Mail.EmailRelayTest> EmailRelayTests => Set<Verso.Domain.Mail.EmailRelayTest>();
    public DbSet<Verso.Domain.Connections.DatabaseConnection> DatabaseConnections => Set<Verso.Domain.Connections.DatabaseConnection>();
    public DbSet<Verso.Domain.Connections.DatabaseConnectionTest> DatabaseConnectionTests => Set<Verso.Domain.Connections.DatabaseConnectionTest>();
    public DbSet<Verso.Domain.Connections.ApiConnection> ApiConnections => Set<Verso.Domain.Connections.ApiConnection>();
    public DbSet<Verso.Domain.Connections.ApiConnectionTest> ApiConnectionTests => Set<Verso.Domain.Connections.ApiConnectionTest>();
    public DbSet<Verso.Domain.Connections.SmsConnection> SmsConnections => Set<Verso.Domain.Connections.SmsConnection>();
    public DbSet<Verso.Domain.Connections.SmsConnectionTest> SmsConnectionTests => Set<Verso.Domain.Connections.SmsConnectionTest>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<PdfPreview> PdfPreviews => Set<PdfPreview>();
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<WorkflowVersion> WorkflowVersions => Set<WorkflowVersion>();
    public DbSet<Execution> Executions => Set<Execution>();
    public DbSet<ExecutionStep> ExecutionSteps => Set<ExecutionStep>();
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
