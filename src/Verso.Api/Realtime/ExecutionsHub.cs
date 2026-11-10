using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Verso.Infrastructure.Persistence;
using Verso.Application.Templates;

namespace Verso.Api.Realtime;

/// <summary>
/// Real-time notifications, plan §5.5 (<c>/hubs/executions</c>). M5 carries <c>PreviewReady</c>; execution and step
/// updates join in M8 (the Worker already writes the execution events they'll relay). Messages are notifications
/// only: clients read the state from the REST API.
/// No authentication yet, like the rest of the API (Development/Testing only).
/// </summary>
public sealed class ExecutionsHub : Hub
{
    public const string Path = "/hubs/executions";
    public const string PreviewReadyMethod = "PreviewReady";

    public static string PreviewGroup(Guid previewId) => $"preview:{previewId:N}";

    public Task SubscribePreview(Guid previewId) => Groups.AddToGroupAsync(Context.ConnectionId, PreviewGroup(previewId));

    public Task UnsubscribePreview(Guid previewId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, PreviewGroup(previewId));
}

/// <summary>Sends <c>PreviewReady</c> to the clients subscribed to a preview.</summary>
public interface IPreviewNotifier
{
    Task PreviewReadyAsync(PreviewReadyNotice notice, CancellationToken ct);
}

internal sealed class SignalRPreviewNotifier(IHubContext<ExecutionsHub> hub) : IPreviewNotifier
{
    public Task PreviewReadyAsync(PreviewReadyNotice notice, CancellationToken ct) =>
        hub.Clients.Group(ExecutionsHub.PreviewGroup(notice.PreviewId))
            .SendAsync(ExecutionsHub.PreviewReadyMethod, new { previewId = notice.PreviewId, status = notice.Status }, ct);
}

/// <summary>
/// Relays finished previews (written by the Worker) to SignalR. The Worker and the API share only the database,
/// so the API polls it (~500 ms), as plan §6 describes for execution events.
/// </summary>
internal sealed partial class PreviewReadyRelay(IServiceScopeFactory scopes, IPreviewNotifier notifier, ILogger<PreviewReadyRelay> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Don't poll a database that is missing or mid-migration: its queries would wait on the migration's locks
        // (or fail). Start once the schema is up to date, checking every few seconds.
        while (!await DatabaseReadyAsync(stoppingToken))
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }

        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RelayOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRelayFailed(logger, ex); // e.g. database briefly unavailable; try again next tick
            }
        }
    }

    private async Task<bool> DatabaseReadyAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<VersoDbContext>().Database;
            return await db.CanConnectAsync(ct) && !(await db.GetPendingMigrationsAsync(ct)).Any();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRelayFailed(logger, ex);
            return false;
        }
    }

    internal async Task<int> RelayOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var previews = scope.ServiceProvider.GetRequiredService<PdfPreviews>();
        var notices = await previews.TakeReadyNoticesAsync(50, ct);
        foreach (var notice in notices)
        {
            await notifier.PreviewReadyAsync(notice, ct);
        }

        return notices.Count;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Relaying preview notifications failed; retrying")]
    private static partial void LogRelayFailed(ILogger logger, Exception exception);
}
