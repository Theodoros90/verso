using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Verso.Application.Executions;
using Verso.Application.Ingestion;
using Verso.Application.Templates;
using Verso.Infrastructure.Persistence;

namespace Verso.Infrastructure.Jobs;

/// <summary>
/// Hangfire with SQL Server storage in the <c>hangfire</c> schema. The schema is created by our EF migrations
/// (AddPdfPreviews), never by Hangfire at startup, in line with "migrations are applied with dotnet ef".
/// The API only enqueues; the Worker also runs the server.
/// </summary>
public static class HangfireJobs
{
    public const string PreviewCleanupJobId = "pdf-preview-cleanup";

    public static IServiceCollection AddVersoJobClient(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(DependencyInjection.ConnectionStringName)
                               ?? throw new InvalidOperationException($"Connection string '{DependencyInjection.ConnectionStringName}' is not configured.");

        services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseSqlServerStorage(connectionString, new SqlServerStorageOptions
            {
                SchemaName = Schemas.Jobs,
                PrepareSchemaIfNecessary = false,
                QueuePollInterval = TimeSpan.FromSeconds(1), // previews are interactive
            }));
        services.AddScoped<IPreviewJobs, HangfirePreviewJobs>();
        services.AddScoped<IExecutionJobs, HangfireExecutionJobs>();
        services.AddScoped<IFolderAccessJobs, HangfireFolderAccessJobs>();
        return services;
    }

    /// <summary>Worker only: processes jobs.</summary>
    public static IServiceCollection AddVersoJobServer(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddVersoJobClient(configuration);
        services.AddHangfireServer(options =>
        {
            options.WorkerCount = int.TryParse(configuration["Worker:JobWorkers"], out var workers) && workers > 0 ? workers : 4;
            options.ServerName = $"verso-worker:{Environment.MachineName}:{Environment.ProcessId}";
        });
        services.AddScoped<PdfPreviewJob>();
        services.AddScoped<PdfPreviewCleanupJob>();
        services.AddScoped<ExecutionJob>();
        services.AddScoped<ExecutionDispatchJob>();
        services.AddScoped<FolderAccessTestJob>();
        return services;
    }

    /// <summary>Registers the recurring jobs (idempotent; called at Worker startup).</summary>
    public static void RegisterRecurringJobs(IRecurringJobManager recurring)
    {
        ArgumentNullException.ThrowIfNull(recurring);
        recurring.AddOrUpdate<PdfPreviewCleanupJob>(PreviewCleanupJobId, j => j.RunAsync(CancellationToken.None), Cron.Hourly());
        recurring.AddOrUpdate<ExecutionDispatchJob>(ExecutionDispatchJobId, j => j.RunAsync(CancellationToken.None), Cron.Minutely());
    }

    public const string ExecutionDispatchJobId = "execution-dispatch";

    /// <summary>The SQL that creates the Hangfire schema; used by the migration so the schema is versioned with ours.</summary>
    internal static string InstallScript() => SqlServerObjectsInstaller.GetInstallScript(Schemas.Jobs, enableHeavyMigrations: false);
}

internal sealed class HangfirePreviewJobs(IBackgroundJobClient client) : IPreviewJobs
{
    public void EnqueuePdfPreview(Guid previewId) => client.Enqueue<PdfPreviewJob>(j => j.RunAsync(previewId, CancellationToken.None));
}

/// <summary>Hangfire entry point for a PDF preview. Retries technical failures; the runner marks the last attempt failed.</summary>
public sealed class PdfPreviewJob(PdfPreviewRunner runner)
{
    [AutomaticRetry(Attempts = PdfPreviewRunner.MaxAttempts - 1, DelaysInSeconds = [2, 5], OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task RunAsync(Guid previewId, CancellationToken cancellationToken) => runner.RunAsync(previewId, cancellationToken);
}

public sealed class PdfPreviewCleanupJob(PdfPreviewCleanup cleanup)
{
    [AutomaticRetry(Attempts = 0)]
    public Task RunAsync(CancellationToken cancellationToken) => cleanup.RunAsync(cancellationToken);
}

internal sealed class HangfireExecutionJobs(IBackgroundJobClient client) : IExecutionJobs
{
    public void Enqueue(Guid executionId) => client.Enqueue<ExecutionJob>(j => j.RunAsync(executionId, CancellationToken.None));

    public void Schedule(Guid executionId, TimeSpan delay) => client.Schedule<ExecutionJob>(j => j.RunAsync(executionId, CancellationToken.None), delay);
}

/// <summary>
/// Hangfire entry point for an execution. Hangfire's automatic retry is off: the engine records step failures and
/// schedules its own retries (plan §6.3). If the job itself dies (e.g. the database is unreachable), the run's
/// lock expires and <see cref="ExecutionDispatcher"/> queues it again.
/// </summary>
public sealed class ExecutionJob(ExecutionEngine engine)
{
    [AutomaticRetry(Attempts = 0)]
    public Task RunAsync(Guid executionId, CancellationToken cancellationToken) => engine.RunAsync(executionId, cancellationToken);
}

internal sealed class HangfireFolderAccessJobs(IBackgroundJobClient client) : IFolderAccessJobs
{
    public void Enqueue(Guid testId) => client.Enqueue<FolderAccessTestJob>(j => j.RunAsync(testId, CancellationToken.None));
}

/// <summary>Hangfire entry point for "Test access": checks folders as the Worker's service identity. Not retried (the user can test again).</summary>
public sealed class FolderAccessTestJob(FolderAccessTestRunner runner)
{
    [AutomaticRetry(Attempts = 0)]
    public Task RunAsync(Guid testId, CancellationToken cancellationToken) => runner.RunAsync(testId, cancellationToken);
}

/// <summary>Every minute: re-queues executions whose job went missing (transactional outbox sweeper).</summary>
public sealed class ExecutionDispatchJob(ExecutionDispatcher dispatcher)
{
    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 50)]
    public Task RunAsync(CancellationToken cancellationToken) => dispatcher.RunOnceAsync(cancellationToken);
}
