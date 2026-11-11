using Verso.Application.Ingestion;

namespace Verso.Worker;

/// <summary>
/// Folder input (runs only here, as the Worker's service identity). Reports this Worker's approved locations at
/// startup and on every poll interval (including locations administrators manage in Verso), then finishes unfinished files and scans due folders.
/// A failing pass is logged and the next one tries again; nothing is lost, because each file's state is in the database.
/// </summary>
internal sealed partial class FolderInputService(
    IServiceScopeFactory scopes, FolderIngestionOptions options, ILogger<FolderInputService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PollInterval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var ingestion = scope.ServiceProvider.GetRequiredService<FolderIngestion>();
                // Every pass: picks up locations administrators changed in Verso within one poll interval (writes only on change).
                await ingestion.SyncLocationsAsync(stoppingToken);

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
