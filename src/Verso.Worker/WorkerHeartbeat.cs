using Microsoft.EntityFrameworkCore;
using Verso.Infrastructure.Persistence;

namespace Verso.Worker;

/// <summary>
/// Placeholder background service until the execution engine arrives. It checks at startup that the
/// Worker can reach the database, then logs a periodic heartbeat so operators can see the process is alive.
/// </summary>
internal sealed partial class WorkerHeartbeat(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<WorkerHeartbeat> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LogDatabaseStatusAsync(stoppingToken);

        var interval = configuration.GetValue("Worker:HeartbeatInterval", TimeSpan.FromMinutes(1));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            LogHeartbeat(logger, Environment.ProcessId);
        }
    }

    private async Task LogDatabaseStatusAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VersoDbContext>();
        try
        {
            if (!await db.Database.CanConnectAsync(cancellationToken))
            {
                LogDatabaseUnreachable(logger);
                return;
            }

            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
            LogDatabaseReachable(logger, pending);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDatabaseCheckFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker heartbeat (process {ProcessId})")]
    private static partial void LogHeartbeat(ILogger logger, int processId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database reachable; {PendingMigrations} pending migration(s)")]
    private static partial void LogDatabaseReachable(ILogger logger, int pendingMigrations);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database unreachable or does not exist")]
    private static partial void LogDatabaseUnreachable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Database check failed")]
    private static partial void LogDatabaseCheckFailed(ILogger logger, Exception exception);
}
