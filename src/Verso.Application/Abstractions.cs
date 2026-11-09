using Microsoft.EntityFrameworkCore;
using Verso.Domain.Audit;
using Verso.Domain.Templates;

namespace Verso.Application;

/// <summary>Persistence as seen by use cases. Implemented by the EF Core context in Infrastructure.</summary>
public interface IVersoDbContext
{
    DbSet<Template> Templates { get; }
    DbSet<TemplateVersion> TemplateVersions { get; }
    DbSet<AuditEntry> AuditEntries { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Tells EF which rowversion the caller last saw, so a concurrent change fails the save.</summary>
    void SetOriginalRowVersion(TemplateVersion version, byte[] rowVersion);
}

/// <summary>The person (or service) performing the current request; recorded on changes and audit entries.</summary>
public interface ICurrentUser
{
    string Id { get; }
    string Name { get; }
}
