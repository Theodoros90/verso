using Verso.Application.Ingestion;

namespace Verso.Worker;

/// <summary>
/// Folder input (runs only here, as the Worker's service identity). Reports this Worker's approved locations at
/// startup and every few minutes, then on every poll interval finishes unfinished files and scans due folders.
/// A failing pass is logged and the next one tries again; nothing is lost, because each file's state is in the database.
/// </summary>
internal sealed partial class FolderInputService(
    IServiceScopeFactory scopes, FolderIngestionOptions options, ILogger<FolderInputService> logger) : BackgroundService
{
    private static readonly TimeSpan LocationSyncInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastSync = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(options.PollInterval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var ingestion = scope.ServiceProvider.GetRequiredService<FolderIngestion>();
                if (DateTimeOffset.UtcNow - lastSync >= LocationSyncInterval)
                {
                    await ingestion.SyncLocationsAsync(stoppingToken);
                    lastSync = DateTimeOffset.UtcNow;
                }

                var result = await ingestion.RunOnceAsync(stoppingToken);
                if (result.Claimed + result.Delivered + result.Errors > 0)
                {
                    LogPass(logger, result.Scanned, result.Claimed, result.Submitted, result.Delivered, result.Errors);
                }

                await scope.ServiceProvider.GetRequiredService<FolderAccessTestRunner>().CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogPassFailed(logger, ex); // e.g. database not migrated yet; the next pass tries again
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Folder input pass: {Scanned} folder(s) scanned, {Claimed} file(s) picked up, {Submitted} run(s) started, {Delivered} file(s) moved, {Errors} error(s)")]
    private static partial void LogPass(ILogger logger, int scanned, int claimed, int submitted, int delivered, int errors);

    [LoggerMessage(Level = LogLevel.Error, Message = "Folder input pass failed")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
