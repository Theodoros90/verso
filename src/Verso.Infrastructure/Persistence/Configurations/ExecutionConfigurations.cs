using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Verso.Domain.Executions;

namespace Verso.Infrastructure.Persistence.Configurations;

internal sealed class ExecutionConfiguration : IEntityTypeConfiguration<Execution>
{
    public void Configure(EntityTypeBuilder<Execution> builder)
    {
        builder.ToTable("Executions", Schemas.Executions, t =>
        {
            t.HasCheckConstraint("CK_Executions_Completed",
                "([Status] IN ('Succeeded', 'Rejected', 'Failed') AND [CompletedAt] IS NOT NULL) OR " +
                "([Status] IN ('Queued', 'Running', 'AwaitingApproval') AND [CompletedAt] IS NULL)");
        });
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        // Workflow ids are references into the workflows schema: by value, no foreign key (plan §3).
        builder.Property(e => e.WorkflowKey).HasMaxLength(48).IsRequired();
        builder.Property(e => e.ResolvedTemplateVersions).IsRequired();
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(e => e.IdempotencyKey).HasMaxLength(100).IsUnicode(false).IsRequired();
        builder.Property(e => e.CorrelationId).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(e => e.InputPayload).IsRequired();
        builder.Property(e => e.InputHash).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(e => e.SubmittedBy).HasMaxLength(200).IsRequired();
        builder.Property(e => e.FailureCode).HasMaxLength(64).IsUnicode(false);
        builder.Property(e => e.FailureMessage).HasMaxLength(Execution.MaxFailureMessageLength);
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasMany(e => e.Steps).WithOne().HasForeignKey(s => s.ExecutionId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(e => e.Steps).HasField("_steps").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(e => new { e.WorkflowId, e.IdempotencyKey }).IsUnique().HasDatabaseName("UX_Executions_IdempotencyKey");
        builder.HasIndex(e => e.DispatchedAt).HasFilter("[Status] IN ('Queued', 'Running')").HasDatabaseName("IX_Executions_Unfinished"); // dispatcher
        builder.HasIndex(e => e.SubmittedAt).HasDatabaseName("IX_Executions_SubmittedAt"); // history (M8)
    }
}

internal sealed class ExecutionStepConfiguration : IEntityTypeConfiguration<ExecutionStep>
{
    public void Configure(EntityTypeBuilder<ExecutionStep> builder)
    {
        builder.ToTable("ExecutionSteps", Schemas.Executions);
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.NodeId).HasMaxLength(64).IsRequired();
        builder.Property(s => s.NodeType).HasMaxLength(64).IsRequired();
        builder.Property(s => s.Title).HasMaxLength(100).IsRequired();
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(s => s.OutputPort).HasMaxLength(32);
        builder.Property(s => s.Error).HasMaxLength(ExecutionStep.MaxErrorLength);
        builder.HasIndex(s => new { s.ExecutionId, s.NodeId, s.Iteration }).IsUnique().HasDatabaseName("UX_ExecutionSteps_Node");
    }
}

internal sealed class ExecutionEventConfiguration : IEntityTypeConfiguration<ExecutionEvent>
{
    public void Configure(EntityTypeBuilder<ExecutionEvent> builder)
    {
        builder.ToTable("ExecutionEvents", Schemas.Executions);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).UseIdentityColumn(); // monotonic watermark for catch-up (plan §4)
        builder.Property(e => e.NodeId).HasMaxLength(64);
        builder.Property(e => e.Type).HasMaxLength(32).IsUnicode(false).IsRequired();
        builder.Property(e => e.Data).HasMaxLength(2000);
        builder.HasOne<Execution>().WithMany().HasForeignKey(e => e.ExecutionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => new { e.ExecutionId, e.Id }).HasDatabaseName("IX_ExecutionEvents_Execution");
    }
}
