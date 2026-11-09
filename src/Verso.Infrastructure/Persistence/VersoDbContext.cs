using Microsoft.EntityFrameworkCore;
using Verso.Application;
using Verso.Domain.Audit;
using Verso.Domain.Templates;

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

    public void SetOriginalRowVersion(TemplateVersion version, byte[] rowVersion) =>
        Entry(version).Property(v => v.RowVersion).OriginalValue = rowVersion;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(VersoDbContext).Assembly);
    }
}
