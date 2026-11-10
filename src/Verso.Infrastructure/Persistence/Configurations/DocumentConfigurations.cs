using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Verso.Domain.Documents;

namespace Verso.Infrastructure.Persistence.Configurations;

internal sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.ToTable("Documents", Schemas.Documents, t =>
        {
            t.HasCheckConstraint("CK_Documents_Archived",
                "([Status] = 'Generated' AND [ArchivedAt] IS NULL) OR ([Status] = 'Archived' AND [ArchivedAt] IS NOT NULL)");
        });
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();

        // Execution and template version ids reference other schemas: by value, no foreign keys (plan §3).
        builder.Property(d => d.NodeId).HasMaxLength(64).IsRequired();
        builder.Property(d => d.FileName).HasMaxLength(200).IsRequired();
        builder.Property(d => d.ContentType).HasMaxLength(100).IsUnicode(false).IsRequired();
        builder.Property(d => d.Sha256).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(d => d.StorageProvider).HasMaxLength(32).IsUnicode(false).IsRequired();
        builder.Property(d => d.StorageKey).HasMaxLength(300).IsUnicode(false).IsRequired();
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(16).IsRequired();

        // One document per generate-pdf step: a re-run reuses it, and the database refuses a duplicate.
        builder.HasIndex(d => new { d.ExecutionId, d.NodeId }).IsUnique().HasDatabaseName("UX_Documents_ExecutionNode");
    }
}

internal sealed class ArchiveRecordConfiguration : IEntityTypeConfiguration<ArchiveRecord>
{
    public void Configure(EntityTypeBuilder<ArchiveRecord> builder)
    {
        builder.ToTable("ArchiveRecords", Schemas.Documents);
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Retention).HasMaxLength(16).IsUnicode(false).IsRequired();
        builder.Property(a => a.FolderPath).HasMaxLength(300).IsRequired();
        builder.Property(a => a.Metadata).IsRequired();
        builder.Property(a => a.SearchText).IsRequired();
        builder.HasIndex(a => a.ArchivedAt).HasDatabaseName("IX_ArchiveRecords_ArchivedAt"); // archive search by date
        builder.HasOne<Document>().WithOne().HasForeignKey<ArchiveRecord>(a => a.DocumentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => a.DocumentId).IsUnique().HasDatabaseName("UX_ArchiveRecords_Document");
    }
}
