using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Verso.Domain.Templates;

namespace Verso.Infrastructure.Persistence.Configurations;

internal sealed class TemplateConfiguration : IEntityTypeConfiguration<Template>
{
    public void Configure(EntityTypeBuilder<Template> builder)
    {
        builder.ToTable("Templates", Schemas.Templates);
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Key).HasMaxLength(32).IsRequired();
        builder.HasIndex(t => t.Key).IsUnique();
        builder.Property(t => t.Name).HasMaxLength(200).IsRequired();
        builder.Property(t => t.Description).HasMaxLength(1000);
        builder.Property(t => t.DocumentType).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(t => t.CreatedBy).HasMaxLength(200).IsRequired();

        builder.HasMany(t => t.Versions)
            .WithOne()
            .HasForeignKey(v => v.TemplateId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(t => t.Versions).HasField("_versions").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(t => t.Draft);
        builder.Ignore(t => t.LatestPublished);
    }
}

internal sealed class TemplateVersionConfiguration : IEntityTypeConfiguration<TemplateVersion>
{
    public void Configure(EntityTypeBuilder<TemplateVersion> builder)
    {
        builder.ToTable("TemplateVersions", Schemas.Templates, t =>
        {
            // Published rows always carry a number and hash; drafts never do.
            t.HasCheckConstraint("CK_TemplateVersions_PublishedFields",
                "([Status] = 'Draft' AND [VersionNumber] IS NULL AND [ContentHash] IS NULL AND [PublishedAt] IS NULL) OR " +
                "([Status] = 'Published' AND [VersionNumber] IS NOT NULL AND [ContentHash] IS NOT NULL AND [PublishedAt] IS NOT NULL)");
        });
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(v => v.ContentHash).HasMaxLength(64).IsFixedLength().IsUnicode(false);
        builder.Property(v => v.CreatedBy).HasMaxLength(200).IsRequired();
        builder.Property(v => v.UpdatedBy).HasMaxLength(200).IsRequired();
        builder.Property(v => v.PublishedBy).HasMaxLength(200);
        builder.Property(v => v.RowVersion).IsRowVersion();

        builder.ComplexProperty(v => v.Content, content =>
        {
            content.Property(c => c.Layout).HasColumnName("Layout").IsRequired();
            content.Property(c => c.DataSchema).HasColumnName("DataSchema").IsRequired();
            content.Property(c => c.SampleData).HasColumnName("SampleData").IsRequired();
            content.ComplexProperty(c => c.PageSettings, page =>
            {
                page.Property(p => p.Size).HasColumnName("PageSize").HasMaxLength(16).IsRequired();
                page.Property(p => p.Orientation).HasColumnName("PageOrientation").HasMaxLength(16).IsRequired();
                page.Property(p => p.MarginTopMm).HasColumnName("MarginTopMm").HasPrecision(5, 2);
                page.Property(p => p.MarginRightMm).HasColumnName("MarginRightMm").HasPrecision(5, 2);
                page.Property(p => p.MarginBottomMm).HasColumnName("MarginBottomMm").HasPrecision(5, 2);
                page.Property(p => p.MarginLeftMm).HasColumnName("MarginLeftMm").HasPrecision(5, 2);
            });
        });

        // At most one draft per template, and version numbers unique per template: enforced by the database too.
        builder.HasIndex(v => v.TemplateId).IsUnique().HasFilter("[Status] = 'Draft'").HasDatabaseName("UX_TemplateVersions_OneDraft");
        builder.HasIndex(v => new { v.TemplateId, v.VersionNumber }).IsUnique().HasFilter("[VersionNumber] IS NOT NULL").HasDatabaseName("UX_TemplateVersions_Number");
    }
}
