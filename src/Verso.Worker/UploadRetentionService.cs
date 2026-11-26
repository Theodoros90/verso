using Verso.Application.Uploads;

namespace Verso.Worker;

/// <summary>
/// File upload retention (ADR 0023; runs only here): once an hour, staged uploads no run has taken within <c>Uploads:Retention</c> are
/// expired or deleted. A failing pass is logged and the next one tries again; each row's state is in the database.
/// </summary>
internal sealed partial class UploadRetentionService(IServiceScopeFactory scopes, ILogger<UploadRetentionService> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var changed = await scope.ServiceProvider.GetRequiredService<UploadRetention>().RunOnceAsync(stoppingToken);
                if (changed > 0)
                {
                    LogPass(logger, changed);
                }
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
        while (await DelayAsync(stoppingToken));
    }

    private static async Task<bool> DelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Interval, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Upload retention pass: {Changed} unused upload(s) expired or removed")]
    private static partial void LogPass(ILogger logger, int changed);

    [LoggerMessage(Level = LogLevel.Error, Message = "Upload retention pass failed")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
