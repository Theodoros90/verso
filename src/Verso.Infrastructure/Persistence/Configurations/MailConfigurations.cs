using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Verso.Domain.Mail;

namespace Verso.Infrastructure.Persistence.Configurations;

internal sealed class EmailRelayConfiguration : IEntityTypeConfiguration<EmailRelay>
{
    public void Configure(EntityTypeBuilder<EmailRelay> builder)
    {
        builder.ToTable("EmailRelays", Schemas.Mail);
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Key).HasMaxLength(64).IsRequired().UseCollation("Latin1_General_100_CI_AS");
        builder.HasIndex(r => r.Key).IsUnique().HasDatabaseName("UX_EmailRelays_Key");
        builder.Property(r => r.Name).HasMaxLength(100).IsRequired();
        builder.Property(r => r.Host).HasMaxLength(255).IsRequired();
        builder.Property(r => r.UserName).HasMaxLength(200).IsRequired();
        builder.Property(r => r.FromAddress).HasMaxLength(320).IsRequired();
        builder.Property(r => r.FromName).HasMaxLength(100).IsRequired();
        builder.Property(r => r.AllowedRecipientDomains).HasMaxLength(4000).IsRequired();
        builder.Property(r => r.PasswordProtected).HasMaxLength(1000); // AES-GCM text, never the password
        builder.Property(r => r.CreatedBy).HasMaxLength(200).IsRequired();
        builder.Property(r => r.UpdatedBy).HasMaxLength(200).IsRequired();
    }
}

internal sealed class EmailRelayTestConfiguration : IEntityTypeConfiguration<EmailRelayTest>
{
    public void Configure(EntityTypeBuilder<EmailRelayTest> builder)
    {
        builder.ToTable("EmailRelayTests", Schemas.Mail);
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.RelayKey).HasMaxLength(64).IsRequired();
        builder.Property(t => t.ToAddress).HasMaxLength(320); // cleared when the test finishes
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(t => t.Code).HasMaxLength(64);
        builder.Property(t => t.Message).HasMaxLength(EmailRelayTest.MaxMessageLength);
        builder.Property(t => t.RequestedBy).HasMaxLength(200).IsRequired();
        builder.HasIndex(t => t.RequestedAt).HasDatabaseName("IX_EmailRelayTests_RequestedAt"); // cleanup
    }
}
