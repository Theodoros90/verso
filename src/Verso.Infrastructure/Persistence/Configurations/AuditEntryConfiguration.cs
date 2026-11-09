using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Verso.Domain.Audit;

namespace Verso.Infrastructure.Persistence.Configurations;

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("AuditEntries", Schemas.Audit);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Sequence).UseIdentityColumn();
        builder.HasIndex(e => e.Sequence).IsUnique();
        builder.Property(e => e.ActorId).HasMaxLength(200).IsRequired();
        builder.Property(e => e.ActorName).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Action).HasMaxLength(64).IsRequired();
        builder.Property(e => e.EntityType).HasMaxLength(64).IsRequired();
        builder.Property(e => e.EntityId).HasMaxLength(64).IsRequired();
        builder.Property(e => e.TraceId).HasMaxLength(32).IsUnicode(false);
        builder.HasIndex(e => new { e.EntityType, e.EntityId, e.Sequence });
    }
}
