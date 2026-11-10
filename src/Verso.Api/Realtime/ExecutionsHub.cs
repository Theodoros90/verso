using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Verso.Application.Executions;
using Verso.Application.Templates;
using Verso.Domain.Executions;
using Verso.Infrastructure.Persistence;

namespace Verso.Api.Realtime;

/// <summary>
/// Real-time notifications, plan §5.5 (<c>/hubs/executions</c>): <c>PreviewReady</c> (M5) and, from M8,
/// <c>ExecutionUpdated</c> and <c>StepUpdated</c>, relayed from the execution events the Worker writes.
/// Messages are notifications only: clients read the state from the REST API and catch up with
/// <c>GET /executions/{id}/events?after=</c> after (re)connecting; group memberships don't survive a reconnect.
/// No authentication yet, like the rest of the API (Development/Testing only).
/// </summary>
public sealed class ExecutionsHub : Hub
{
    public const string Path = "/hubs/executions";
    public const string PreviewReadyMethod = "PreviewReady";
    public const string ExecutionUpdatedMethod = "ExecutionUpdated";
    public const string StepUpdatedMethod = "StepUpdated";

    public static string PreviewGroup(Guid previewId) => $"preview:{previewId:N}";

    public static string ExecutionGroup(Guid executionId) => $"execution:{executionId:N}";

    public static string WorkflowGroup(Guid workflowId) => $"workflow:{workflowId:N}";

    /// <summary>Every execution update (the history list uses it). An addition to the plan's two group kinds.</summary>
    public const string AllExecutionsGroup = "executions";

    public Task SubscribePreview(Guid previewId) => Groups.AddToGroupAsync(Context.ConnectionId, PreviewGroup(previewId));

    public Task UnsubscribePreview(Guid previewId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, PreviewGroup(previewId));

    public Task SubscribeExecution(Guid executionId) => Groups.AddToGroupAsync(Context.ConnectionId, ExecutionGroup(executionId));

    public Task UnsubscribeExecution(Guid executionId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, ExecutionGroup(executionId));

    public Task SubscribeWorkflow(Guid workflowId) => Groups.AddToGroupAsync(Context.ConnectionId, WorkflowGroup(workflowId));

    public Task UnsubscribeWorkflow(Guid workflowId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, WorkflowGroup(workflowId));

    public Task SubscribeExecutions() => Groups.AddToGroupAsync(Context.ConnectionId, AllExecutionsGroup);

    public Task UnsubscribeExecutions() => Groups.RemoveFromGroupAsync(Context.ConnectionId, AllExecutionsGroup);
}

/// <summary>Sends the notifications for one execution event.</summary>
public interface IExecutionNotifier
{
    Task NotifyAsync(RelayEvent ev, CancellationToken ct);
}

internal sealed class SignalRExecutionNotifier(IHubContext<ExecutionsHub> hub) : IExecutionNotifier
{
    public async Task NotifyAsync(RelayEvent ev, CancellationToken ct)
    {
        var updated = new { executionId = ev.ExecutionId, status = ev.ExecutionStatus, eventId = ev.Id, type = ev.Type };
        await hub.Clients.Groups(
                ExecutionsHub.ExecutionGroup(ev.ExecutionId), ExecutionsHub.WorkflowGroup(ev.WorkflowId), ExecutionsHub.AllExecutionsGroup)
            .SendAsync(ExecutionsHub.ExecutionUpdatedMethod, updated, ct);

        if (ExecutionEventRelay.StepStatus(ev) is { } step)
        {
            await hub.Clients.Group(ExecutionsHub.ExecutionGroup(ev.ExecutionId))
                .SendAsync(ExecutionsHub.StepUpdatedMethod, new { executionId = ev.ExecutionId, nodeId = ev.NodeId, status = step.Status, outputPort = step.Port, eventId = ev.Id }, ct);
        }
    }
}

/// <summary>
/// Relays execution events (written by the Worker) to SignalR (plan §6.2 step 8): the API polls the table every
/// ~500 ms; the two processes share only the database. Starts at the latest event, so history isn't replayed.
/// <para>
/// Event ids are identity values, and two runs saving at once can commit out of order (id 101 visible before 100).
/// A plain "id &gt; watermark" poll would then skip 100 for good. So each poll also re-reads the last
/// <see cref="Lookback"/> ids and skips the ones already sent; commits take milliseconds, the window is far larger.
/// </para>
/// </summary>
internal sealed partial class ExecutionEventRelay(IServiceScopeFactory scopes, IExecutionNotifier notifier, ILogger<ExecutionEventRelay> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);
    public const int Lookback = 100;
    public const int BatchSize = 600;

    private long _watermark = -1; // -1: not started yet
    private readonly HashSet<long> _sent = [];

    internal static (string Status, string? Port)? StepStatus(RelayEvent ev)
    {
        var status = ev.Type switch
        {
            ExecutionEventTypes.StepStarted => "Running",
            ExecutionEventTypes.StepSucceeded => "Succeeded",
            ExecutionEventTypes.StepFailed => "Failed",
            _ => null,
        };
        if (status is null || ev.NodeId is null)
        {
            return null;
        }

        string? port = null;
        if (ev.Data is not null)
        {
            using var data = JsonDocument.Parse(ev.Data);
            port = data.RootElement.TryGetProperty("port", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        }

        return (status, port);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

    /// <summary>One poll. The first call only records where to start. Returns the number of events relayed.</summary>
    internal async Task<int> RelayOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<ExecutionQueries>();
        if (_watermark < 0)
        {
            _watermark = await queries.LatestEventIdAsync(ct);
            return 0;
        }

        var window = await queries.RelayWindowAsync(_watermark - Lookback, BatchSize, ct);
        var relayed = 0;
        foreach (var ev in window.Where(e => !_sent.Contains(e.Id)))
        {
            await notifier.NotifyAsync(ev, ct);
            _sent.Add(ev.Id);
            _watermark = Math.Max(_watermark, ev.Id);
            relayed++;
        }

        _sent.RemoveWhere(id => id <= _watermark - Lookback);
        return relayed;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Relaying execution events failed; retrying")]
    private static partial void LogRelayFailed(ILogger logger, Exception exception);
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
