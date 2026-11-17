using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Verso.Domain.Connections;

namespace Verso.Infrastructure.Persistence.Configurations;

internal sealed class DatabaseConnectionConfiguration : IEntityTypeConfiguration<DatabaseConnection>
{
    public void Configure(EntityTypeBuilder<DatabaseConnection> builder)
    {
        builder.ToTable("DatabaseConnections", Schemas.Connections);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Key).HasMaxLength(64).IsRequired().UseCollation("Latin1_General_100_CI_AS");
        builder.HasIndex(c => c.Key).IsUnique().HasDatabaseName("UX_DatabaseConnections_Key");
        builder.Property(c => c.Provider).HasMaxLength(16).IsRequired().HasDefaultValue("SqlServer");
        builder.Property(c => c.Port).HasDefaultValue(0);
        builder.Property(c => c.Name).HasMaxLength(100).IsRequired();
        builder.Property(c => c.Server).HasMaxLength(255).IsRequired();
        builder.Property(c => c.Database).HasMaxLength(128).IsRequired();
        builder.Property(c => c.UserName).HasMaxLength(128).IsRequired();
        builder.Property(c => c.PasswordProtected).HasMaxLength(1000); // AES-GCM text, never the password
        builder.Property(c => c.CreatedBy).HasMaxLength(200).IsRequired();
        builder.Property(c => c.UpdatedBy).HasMaxLength(200).IsRequired();
    }
}

internal sealed class DatabaseConnectionTestConfiguration : IEntityTypeConfiguration<DatabaseConnectionTest>
{
    public void Configure(EntityTypeBuilder<DatabaseConnectionTest> builder)
    {
        builder.ToTable("DatabaseConnectionTests", Schemas.Connections);
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.ConnectionKey).HasMaxLength(64).IsRequired();
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(t => t.Code).HasMaxLength(64);
        builder.Property(t => t.Message).HasMaxLength(DatabaseConnectionTest.MaxMessageLength);
        builder.Property(t => t.ServerVersion).HasMaxLength(100);
        builder.Property(t => t.RequestedBy).HasMaxLength(200).IsRequired();
        builder.HasIndex(t => t.RequestedAt).HasDatabaseName("IX_DatabaseConnectionTests_RequestedAt"); // cleanup
    }
}

internal sealed class ApiConnectionConfiguration : IEntityTypeConfiguration<ApiConnection>
{
    public void Configure(EntityTypeBuilder<ApiConnection> builder)
    {
        builder.ToTable("ApiConnections", Schemas.Connections);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Key).HasMaxLength(64).IsRequired().UseCollation("Latin1_General_100_CI_AS");
        builder.HasIndex(c => c.Key).IsUnique().HasDatabaseName("UX_ApiConnections_Key");
        builder.Property(c => c.Name).HasMaxLength(100).IsRequired();
        builder.Property(c => c.BaseUrl).HasMaxLength(500).IsRequired();
        builder.Property(c => c.TestPath).HasMaxLength(500).IsRequired();
        builder.Property(c => c.AuthType).HasMaxLength(32).IsRequired();
        builder.Property(c => c.AuthName).HasMaxLength(200).IsRequired();
        builder.Property(c => c.TokenUrl).HasMaxLength(500).IsRequired();
        builder.Property(c => c.Scope).HasMaxLength(500).IsRequired();
        builder.Property(c => c.SecretProtected).HasMaxLength(4000); // AES-GCM text, never the secret
        builder.Property(c => c.CreatedBy).HasMaxLength(200).IsRequired();
        builder.Property(c => c.UpdatedBy).HasMaxLength(200).IsRequired();
    }
}

internal sealed class ApiConnectionTestConfiguration : IEntityTypeConfiguration<ApiConnectionTest>
{
    public void Configure(EntityTypeBuilder<ApiConnectionTest> builder)
    {
        builder.ToTable("ApiConnectionTests", Schemas.Connections);
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.ConnectionKey).HasMaxLength(64).IsRequired();
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(t => t.Code).HasMaxLength(64);
        builder.Property(t => t.Message).HasMaxLength(ApiConnectionTest.MaxMessageLength);
        builder.Property(t => t.ContentType).HasMaxLength(100);
        builder.Property(t => t.RequestedBy).HasMaxLength(200).IsRequired();
        builder.HasIndex(t => t.RequestedAt).HasDatabaseName("IX_ApiConnectionTests_RequestedAt"); // cleanup
    }
}
