using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Verso.Api.Realtime;
using Verso.Application.Executions;
using Verso.Application.Templates;

namespace Verso.Api.Tests;

/// <summary>Hosts the real API in memory, pointed at a given test database connection string.</summary>
internal sealed class VersoApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    /// <summary>Records the "preview ready" notifications the relay sends (instead of pushing them over SignalR).</summary>
    public RecordingPreviewNotifier Notifications { get; } = new();

    /// <summary>Records the execution notifications the (hosted) relay sends.</summary>
    public RecordingExecutionNotifier ExecutionNotifications { get; } = new();

    /// <summary>Where the API reads archived documents from; tests that download point this at their own store.</summary>
    public string DocumentRoot { get; } = Path.Combine(Path.GetTempPath(), "verso-test-store", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Verso", connectionString);
        builder.UseSetting("Documents:RootPath", DocumentRoot);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IPreviewNotifier>(Notifications);
            services.AddSingleton<IExecutionNotifier>(ExecutionNotifications);
        });
    }
}

internal sealed class RecordingExecutionNotifier : IExecutionNotifier
{
    public ConcurrentQueue<RelayEvent> Sent { get; } = new();

    public Task NotifyAsync(RelayEvent ev, CancellationToken ct)
    {
        Sent.Enqueue(ev);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingPreviewNotifier : IPreviewNotifier
{
    public ConcurrentQueue<PreviewReadyNotice> Sent { get; } = new();

    public Task PreviewReadyAsync(PreviewReadyNotice notice, CancellationToken ct)
    {
        Sent.Enqueue(notice);
        return Task.CompletedTask;
    }
}
