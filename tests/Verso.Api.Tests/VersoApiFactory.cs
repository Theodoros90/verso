using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Verso.Api.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Verso.Api.Realtime;
using Verso.Application.Executions;
using Verso.Application.Templates;

namespace Verso.Api.Tests;

/// <summary>
/// Hosts the real API in memory, pointed at a given test database connection string, with Development sign-in.
/// Clients sign in as <see cref="DefaultUser"/> (Admin: every permission) unless a test picks a user with
/// <see cref="ApiClients.CreateClientAs"/>.
/// </summary>
internal sealed class VersoApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    public const string DefaultUser = "dev.admin";

    /// <summary>Records the "preview ready" notifications the relay sends (instead of pushing them over SignalR).</summary>
    public RecordingPreviewNotifier Notifications { get; } = new();

    /// <summary>Records the execution notifications the (hosted) relay sends.</summary>
    public RecordingExecutionNotifier ExecutionNotifications { get; } = new();

    /// <summary>Where the API reads archived documents from; tests that download point this at their own store.</summary>
    public string DocumentRoot { get; } = Path.Combine(Path.GetTempPath(), "verso-test-store", Guid.NewGuid().ToString("N"));

    /// <summary>Every log entry the API host writes (all categories and levels), with scopes.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging =>
        {
            logging.AddProvider(Logs);
            logging.AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace); // capture more than the console shows
        });
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Verso", connectionString);
        builder.UseSetting("Documents:RootPath", DocumentRoot);
        builder.UseSetting("Auth:Mode", "Development");
        // The whole suite shares one host and one clock minute: the production default (120 submissions per user per minute) would
        // make late tests fail with 429 as the suite grows. LimitsTests sets its own small value to test the limiter.
        builder.UseSetting("Limits:SubmissionsPerMinute", "100000");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IPreviewNotifier>(Notifications);
            services.AddSingleton<IExecutionNotifier>(ExecutionNotifications);
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DevUsers.Token(DefaultUser));
    }
}

internal static class ApiClients
{
    /// <summary>A client signed in as the given development user (e.g. <c>dev.viewer</c>), or anonymous for null.</summary>
    public static HttpClient CreateClientAs(this WebApplicationFactory<Program> factory, string? userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = userId is null ? null : new AuthenticationHeaderValue("Bearer", DevUsers.Token(userId));
        return client;
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
