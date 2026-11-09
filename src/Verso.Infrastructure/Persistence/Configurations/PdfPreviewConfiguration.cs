using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Verso.Domain.Templates;

namespace Verso.Infrastructure.Persistence.Configurations;

internal sealed class PdfPreviewConfiguration : IEntityTypeConfiguration<PdfPreview>
{
    public void Configure(EntityTypeBuilder<PdfPreview> builder)
    {
        builder.ToTable("PdfPreviews", Schemas.Templates);
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.TemplateName).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Layout).IsRequired();
        builder.Property(p => p.Data).IsRequired();
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(p => p.Issues).IsRequired();
        builder.Property(p => p.Error).HasMaxLength(PdfPreview.MaxErrorLength);
        builder.Property(p => p.CreatedBy).HasMaxLength(200).IsRequired();

        builder.ComplexProperty(p => p.PageSettings, page =>
        {
            page.Property(s => s.Size).HasColumnName("PageSize").HasMaxLength(16).IsRequired();
            page.Property(s => s.Orientation).HasColumnName("PageOrientation").HasMaxLength(16).IsRequired();
            page.Property(s => s.MarginTopMm).HasColumnName("MarginTopMm").HasPrecision(5, 2);
            page.Property(s => s.MarginRightMm).HasColumnName("MarginRightMm").HasPrecision(5, 2);
            page.Property(s => s.MarginBottomMm).HasColumnName("MarginBottomMm").HasPrecision(5, 2);
            page.Property(s => s.MarginLeftMm).HasColumnName("MarginLeftMm").HasPrecision(5, 2);
        });

        // Same module as TemplateVersions, so a foreign key is allowed; previews go when their template version would.
        builder.HasOne<TemplateVersion>().WithMany().HasForeignKey(p => p.TemplateVersionId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => p.CreatedAt).HasDatabaseName("IX_PdfPreviews_CreatedAt"); // cleanup
        builder.HasIndex(p => p.CompletedAt).HasFilter("[NotifiedAt] IS NULL AND [CompletedAt] IS NOT NULL").HasDatabaseName("IX_PdfPreviews_Unnotified"); // relay
    }
}
