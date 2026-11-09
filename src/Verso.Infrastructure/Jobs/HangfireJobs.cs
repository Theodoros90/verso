using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        return services;
    }

    /// <summary>Registers the recurring jobs (idempotent; called at Worker startup).</summary>
    public static void RegisterRecurringJobs(IRecurringJobManager recurring)
    {
        ArgumentNullException.ThrowIfNull(recurring);
        recurring.AddOrUpdate<PdfPreviewCleanupJob>(PreviewCleanupJobId, j => j.RunAsync(CancellationToken.None), Cron.Hourly());
    }

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
