using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Verso.Api.Realtime;
using Verso.Application.Templates;

namespace Verso.Api.Tests;

/// <summary>Hosts the real API in memory, pointed at a given test database connection string.</summary>
internal sealed class VersoApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    /// <summary>Records the "preview ready" notifications the relay sends (instead of pushing them over SignalR).</summary>
    public RecordingPreviewNotifier Notifications { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Verso", connectionString);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IPreviewNotifier>(Notifications);
        });
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
