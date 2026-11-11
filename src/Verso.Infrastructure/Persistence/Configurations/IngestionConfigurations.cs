using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Verso.Domain.Ingestion;

namespace Verso.Infrastructure.Persistence.Configurations;

internal sealed class FolderLocationConfiguration : IEntityTypeConfiguration<FolderLocation>
{
    public void Configure(EntityTypeBuilder<FolderLocation> builder)
    {
        builder.ToTable("FolderLocations", Schemas.Ingestion);
        builder.HasKey(l => l.Key);
        builder.Property(l => l.Key).HasMaxLength(64).IsUnicode(false);
        builder.Property(l => l.Name).HasMaxLength(200).IsRequired();
        builder.Property(l => l.Problem).HasMaxLength(FolderLocation.MaxProblemLength);
        builder.Property(l => l.ReportedBy).HasMaxLength(200).IsRequired();
    }
}

internal sealed class FolderWatchConfiguration : IEntityTypeConfiguration<FolderWatch>
{
    public void Configure(EntityTypeBuilder<FolderWatch> builder)
    {
        // Workflow ids reference the workflows schema by value, no foreign key (plan §3).
        builder.ToTable("FolderWatches", Schemas.Ingestion);
        builder.HasKey(w => w.WorkflowId);
        builder.Property(w => w.WorkflowId).ValueGeneratedNever();
        builder.Property(w => w.WorkflowKey).HasMaxLength(48).IsRequired();
        builder.Property(w => w.NodeId).HasMaxLength(64).IsRequired();
        builder.Property(w => w.LastStatus).HasConversion<string>().HasMaxLength(16);
        builder.Property(w => w.LastError).HasMaxLength(FolderWatch.MaxErrorLength);
        builder.Property(w => w.RowVersion).IsRowVersion();
    }
}

internal sealed class FolderFileConfiguration : IEntityTypeConfiguration<FolderFile>
{
    public void Configure(EntityTypeBuilder<FolderFile> builder)
    {
        builder.ToTable("FolderFiles", Schemas.Ingestion, t =>
        {
            t.HasCheckConstraint("CK_FolderFiles_Completed",
                "([Status] = 'Completed' AND [CompletedAt] IS NOT NULL) OR ([Status] <> 'Completed' AND [CompletedAt] IS NULL)");
            t.HasCheckConstraint("CK_FolderFiles_Submitted", "[Status] <> 'Submitted' OR [ExecutionId] IS NOT NULL");
        });
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.WorkflowKey).HasMaxLength(48).IsRequired();
        builder.Property(f => f.NodeId).HasMaxLength(64).IsRequired();
        foreach (var location in new[] { nameof(FolderFile.SourceLocation), nameof(FolderFile.ProcessedLocation), nameof(FolderFile.FailedLocation) })
        {
            builder.Property(location).HasMaxLength(64).IsUnicode(false).IsRequired();
        }

        foreach (var subfolder in new[] { nameof(FolderFile.SourceSubfolder), nameof(FolderFile.ProcessedSubfolder), nameof(FolderFile.FailedSubfolder) })
        {
            builder.Property(subfolder).HasMaxLength(200).IsRequired();
        }

        builder.Property(f => f.RelativePath).HasMaxLength(400).IsRequired();
        builder.Property(f => f.FileName).HasMaxLength(255).IsRequired();
        builder.Property(f => f.DeliveredName).HasMaxLength(300);
        builder.Property(f => f.Sha256).HasMaxLength(64).IsFixedLength().IsUnicode(false);
        builder.Property(f => f.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(f => f.Outcome).HasConversion<string>().HasMaxLength(16);
        builder.Property(f => f.ErrorCode).HasMaxLength(64).IsUnicode(false);
        builder.Property(f => f.ErrorMessage).HasMaxLength(FolderFile.MaxErrorLength);
        builder.Property(f => f.DeliveryError).HasMaxLength(FolderFile.MaxErrorLength);
        builder.Property(f => f.ClaimedBy).HasMaxLength(200).IsRequired();
        builder.Property(f => f.RowVersion).IsRowVersion();

        // The claim: while a file is Claimed, no other Worker can claim the same source file (insert fails).
        builder.HasIndex(f => new { f.SourceLocation, f.SourceSubfolder, f.RelativePath })
            .IsUnique().HasFilter("[Status] = 'Claimed'").HasDatabaseName("UX_FolderFiles_Claim");

        // One file per run: two files with the same data can't both own the run their content maps to.
        builder.HasIndex(f => f.ExecutionId).IsUnique().HasFilter("[ExecutionId] IS NOT NULL").HasDatabaseName("UX_FolderFiles_Execution");

        builder.HasIndex(f => new { f.Status, f.ClaimedAt }).HasFilter("[Status] <> 'Completed'").HasDatabaseName("IX_FolderFiles_Unfinished");
        builder.HasIndex(f => new { f.WorkflowId, f.ClaimedAt }).HasDatabaseName("IX_FolderFiles_Workflow");
    }
}

internal sealed class FolderAccessTestConfiguration : IEntityTypeConfiguration<FolderAccessTest>
{
    public void Configure(EntityTypeBuilder<FolderAccessTest> builder)
    {
        builder.ToTable("FolderAccessTests", Schemas.Ingestion);
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Targets).HasMaxLength(2000).IsRequired();
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(t => t.Identity).HasMaxLength(200);
        builder.Property(t => t.Machine).HasMaxLength(200);
        builder.Property(t => t.Error).HasMaxLength(FolderAccessTest.MaxErrorLength);
        builder.Property(t => t.RequestedBy).HasMaxLength(200).IsRequired();
        builder.HasIndex(t => t.RequestedAt).HasDatabaseName("IX_FolderAccessTests_RequestedAt"); // 24 h cleanup
    }
}
