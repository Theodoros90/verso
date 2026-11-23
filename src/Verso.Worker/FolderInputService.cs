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
        var wait = options.PollInterval;
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var ingestion = scope.ServiceProvider.GetRequiredService<FolderIngestion>();
                // Every pass: picks up locations administrators changed in Verso within one poll interval (writes only on change).
                await ingestion.SyncLocationsAsync(stoppingToken);

                var result = await ingestion.RunOnceAsync(stoppingToken);
                // A workflow with a short custom scan interval makes the Worker look more often (never below the floor).
                wait = ingestion.ShortestInterval is { } shortest && shortest < options.PollInterval
                    ? TimeSpan.FromMilliseconds(Math.Max(shortest.TotalMilliseconds, MinimumWait.TotalMilliseconds))
                    : options.PollInterval;
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
        while (await DelayAsync(wait, stoppingToken));
    }

    private static readonly TimeSpan MinimumWait = TimeSpan.FromMilliseconds(250);

    private static async Task<bool> DelayAsync(TimeSpan wait, CancellationToken ct)
    {
        try
        {
            await Task.Delay(wait, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Folder input pass: {Scanned} folder(s) scanned, {Claimed} file(s) picked up, {Submitted} run(s) started, {Delivered} file(s) moved, {Errors} error(s)")]
    private static partial void LogPass(ILogger logger, int scanned, int claimed, int submitted, int delivered, int errors);

    [LoggerMessage(Level = LogLevel.Error, Message = "Folder input pass failed")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
