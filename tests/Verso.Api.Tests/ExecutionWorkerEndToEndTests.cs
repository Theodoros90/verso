using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Verso.Application;
using Verso.Infrastructure;
using Verso.Infrastructure.Documents;
using Verso.Infrastructure.Jobs;
using Verso.Infrastructure.Persistence;
using Verso.Rendering;

namespace Verso.Api.Tests;

/// <summary>
/// Its own database, an API host, and a Worker-like host running the real Hangfire server with the Worker's
/// registrations (fake printer instead of Chromium). Nothing is called by hand between submit and Succeeded.
/// Runs in the API test collection so no other Hangfire host configures itself at the same time (Hangfire keeps
/// its configuration in a static).
/// </summary>
public sealed class ExecutionWorkerFixture : IAsyncLifetime
{
    private readonly TestDatabase _database = new("worker");
    private IHost? _worker;

    internal VersoApiFactory Factory { get; private set; } = null!;
    internal string StoreRoot { get; } = Path.Combine(Path.GetTempPath(), "verso-test-store", Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        await _database.MigrateAsync(); // before any host polls it
        Factory = new VersoApiFactory(_database.ConnectionString);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Verso"] = _database.ConnectionString,
            ["Documents:RootPath"] = StoreRoot,
            ["Worker:JobWorkers"] = "2",
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddVersoPersistence(builder.Configuration);
        builder.Services.AddVersoWorkerUseCases();
        builder.Services.AddSingleton<IPdfPrinter, FakePdfPrinter>();
        builder.Services.AddSingleton<Verso.Rendering.Docx.IDocxWriter, FakeDocxWriter>();
        builder.Services.AddSingleton<IPdfCombiner, FakePdfCombiner>();
        builder.Services.AddVersoDocumentStore(builder.Configuration);
        builder.Services.AddVersoJobServer(builder.Configuration);
        _worker = builder.Build();
        await _worker.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_worker is not null)
        {
            await _worker.StopAsync();
            _worker.Dispose();
        }

        await Factory.DisposeAsync();
        await _database.DropAsync();
        if (Directory.Exists(StoreRoot))
        {
            Directory.Delete(StoreRoot, recursive: true);
        }
    }
}

[Collection(ApiTestGroup.Name)]
public sealed class ExecutionWorkerEndToEndTests(ExecutionWorkerFixture worker) : IClassFixture<ExecutionWorkerFixture>
{
    [Fact]
    public async Task Submit_IsPickedUpByTheWorker_AndArchived()
    {
        using var client = worker.Factory.CreateClient();
        var kit = new ExecutionTestKit(worker.Factory, client);
        var key = await kit.PublishInvoiceWorkflowAsync();

        var valid = await kit.SubmitNewAsync(key, ExecutionTestKit.Sample);
        var broken = await kit.SubmitNewAsync(key, ExecutionTestKit.Broken);

        var done = await WaitForFinishAsync(kit, valid);
        var rejected = await WaitForFinishAsync(kit, broken);

        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        var document = Assert.Single(done.GetProperty("documents").EnumerateArray());
        var archivePath = Path.Combine(worker.StoreRoot, "archive");
        var file = Assert.Single(Directory.GetFiles(archivePath, $"{document.GetProperty("id").GetGuid():N}.pdf", SearchOption.AllDirectories));
        Assert.Equal(document.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(file))));

        Assert.Equal("Rejected", rejected.GetProperty("status").GetString());
        Assert.Empty(rejected.GetProperty("documents").EnumerateArray());
    }

    private static async Task<JsonElement> WaitForFinishAsync(ExecutionTestKit kit, Guid id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var execution = await kit.GetExecutionAsync(id);
            if (execution.GetProperty("status").GetString() is "Succeeded" or "Rejected" or "Failed" || DateTime.UtcNow > deadline)
            {
                return execution;
            }

            await Task.Delay(250);
        }
    }
}
