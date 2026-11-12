using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Verso.Domain.Workflows;

namespace Verso.Infrastructure.Persistence.Configurations;

internal sealed class WorkflowConfiguration : IEntityTypeConfiguration<Workflow>
{
    public void Configure(EntityTypeBuilder<Workflow> builder)
    {
        builder.ToTable("Workflows", Schemas.Workflows);
        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id).ValueGeneratedNever();
        builder.Property(w => w.Key).HasMaxLength(48).IsRequired();
        builder.HasIndex(w => w.Key).IsUnique();
        builder.Property(w => w.Name).HasMaxLength(200).IsRequired();
        builder.Property(w => w.Description).HasMaxLength(1000);
        builder.Property(w => w.CreatedBy).HasMaxLength(200).IsRequired();
        builder.Property(w => w.IsActive).HasDefaultValue(true);

        builder.HasMany(w => w.Versions).WithOne().HasForeignKey(v => v.WorkflowId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(w => w.Versions).HasField("_versions").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(w => w.Draft);
        builder.Ignore(w => w.LatestPublished);
    }
}

internal sealed class WorkflowVersionConfiguration : IEntityTypeConfiguration<WorkflowVersion>
{
    public void Configure(EntityTypeBuilder<WorkflowVersion> builder)
    {
        builder.ToTable("WorkflowVersions", Schemas.Workflows, t =>
        {
            t.HasCheckConstraint("CK_WorkflowVersions_PublishedFields",
                "([Status] = 'Draft' AND [VersionNumber] IS NULL AND [ContentHash] IS NULL AND [PublishedAt] IS NULL) OR " +
                "([Status] = 'Published' AND [VersionNumber] IS NOT NULL AND [ContentHash] IS NOT NULL AND [PublishedAt] IS NOT NULL)");
        });
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(v => v.Definition).IsRequired();
        builder.Property(v => v.ContentHash).HasMaxLength(64).IsFixedLength().IsUnicode(false);
        builder.Property(v => v.CreatedBy).HasMaxLength(200).IsRequired();
        builder.Property(v => v.UpdatedBy).HasMaxLength(200).IsRequired();
        builder.Property(v => v.PublishedBy).HasMaxLength(200);
        builder.Property(v => v.RowVersion).IsRowVersion();

        builder.HasIndex(v => v.WorkflowId).IsUnique().HasFilter("[Status] = 'Draft'").HasDatabaseName("UX_WorkflowVersions_OneDraft");
        builder.HasIndex(v => new { v.WorkflowId, v.VersionNumber }).IsUnique().HasFilter("[VersionNumber] IS NOT NULL").HasDatabaseName("UX_WorkflowVersions_Number");
    }
}
