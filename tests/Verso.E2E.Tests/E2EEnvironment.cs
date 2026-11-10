using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Verso.Infrastructure.Persistence;

namespace Verso.E2E.Tests;

/// <summary>
/// Everything the MVP runs on, started for real: a throwaway database (migrated), Verso.Api and Verso.Worker as
/// processes (Development sign-in, their own document folder), the production build of the SPA served by
/// <c>vite preview</c> (which proxies /api and /hubs like the dev server), and Chromium. Process output is kept in
/// files, so tests can check what the services logged.
/// </summary>
public sealed class E2EEnvironment : IAsyncLifetime
{
    private static readonly string Server =
        Environment.GetEnvironmentVariable("VERSO_TEST_SQL_SERVER") is { Length: > 0 } s ? s : @"(localdb)\MSSQLLocalDB";

    private readonly string _database = $"Verso_Test_e2e_{Guid.NewGuid():N}";
    private readonly List<Process> _processes = [];
    private IPlaywright? _playwright;

    public string RepoRoot { get; } = FindRepoRoot();
    public string WorkDir { get; } = Path.Combine(Path.GetTempPath(), "verso-e2e", Guid.NewGuid().ToString("N"));
    public string ApiUrl { get; private set; } = "";
    public string SpaUrl { get; private set; } = "";
    public string ApiLog => Path.Combine(WorkDir, "api.log");
    public string WorkerLog => Path.Combine(WorkDir, "worker.log");
    public IBrowser Browser { get; private set; } = null!;

    private string ConnectionString => new SqlConnectionStringBuilder
    {
        DataSource = Server, InitialCatalog = _database, IntegratedSecurity = true, TrustServerCertificate = true, ConnectTimeout = 5,
    }.ConnectionString;

    public async Task InitializeAsync()
    {
        try
        {
            await StartAsync();
        }
        catch
        {
            await DisposeAsync(); // xUnit doesn't dispose a fixture whose setup failed: don't leave processes running
            throw;
        }
    }

    private async Task StartAsync()
    {
        Directory.CreateDirectory(WorkDir);

        var options = new DbContextOptionsBuilder<VersoDbContext>().UseSqlServer(ConnectionString).Options;
        await using (var db = new VersoDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name; // …/bin/<Configuration>/net10.0/
        var apiPort = FreePort();
        ApiUrl = $"http://127.0.0.1:{apiPort}";
        var common = new Dictionary<string, string>
        {
            ["ConnectionStrings__Verso"] = ConnectionString,
            ["Documents__RootPath"] = Path.Combine(WorkDir, "documents"),
            ["Logging__Console__FormatterName"] = "json",
        };

        Start("api", "dotnet", $"\"{Path.Combine(RepoRoot, "src", "Verso.Api", "bin", configuration, "net10.0", "Verso.Api.dll")}\"",
            Path.Combine(RepoRoot, "src", "Verso.Api"), ApiLog,
            new(common) { ["ASPNETCORE_ENVIRONMENT"] = "Development", ["ASPNETCORE_URLS"] = ApiUrl, ["Auth__Mode"] = "Development" });
        Start("worker", "dotnet", $"\"{Path.Combine(RepoRoot, "src", "Verso.Worker", "bin", configuration, "net10.0", "Verso.Worker.dll")}\"",
            Path.Combine(RepoRoot, "src", "Verso.Worker"), WorkerLog,
            new(common) { ["DOTNET_ENVIRONMENT"] = "Development" });

        // The production build of the SPA, served with the same proxy the dev server uses.
        var web = Path.Combine(RepoRoot, "src", "verso-web");
        // `npm run build` without cmd/npm: under the test host, cmd.exe crashes (0xC0000409) with redirected streams.
        await RunToEndAsync("node", "node_modules/typescript/bin/tsc -b", web, Path.Combine(WorkDir, "spa-typecheck.log"), TimeSpan.FromMinutes(3));
        await RunToEndAsync("node", "node_modules/vite/bin/vite.js build", web, Path.Combine(WorkDir, "spa-build.log"), TimeSpan.FromMinutes(3));
        var spaPort = FreePort();
        SpaUrl = $"http://127.0.0.1:{spaPort}";
        Start("spa", "node", $"node_modules/vite/bin/vite.js preview --host 127.0.0.1 --port {spaPort} --strictPort", web,
            Path.Combine(WorkDir, "spa.log"), new() { ["VERSO_API_URL"] = ApiUrl });

        await WaitForAsync($"{ApiUrl}/api/health", TimeSpan.FromSeconds(60));
        await WaitForAsync(SpaUrl, TimeSpan.FromSeconds(30));

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.DisposeAsync();
        }

        _playwright?.Dispose();
        foreach (var process in _processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            catch (InvalidOperationException)
            {
                // already gone
            }

            process.Dispose();
        }

        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"IF DB_ID(N'{_database}') IS NOT NULL BEGIN ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>A browser tab signed in (Development sign-in) as the given synthetic user, e.g. "Alex Admin".</summary>
    public async Task<IPage> SignInAsync(string userName)
    {
        var context = await Browser.NewContextAsync(new() { BaseURL = SpaUrl, AcceptDownloads = true, ViewportSize = new() { Width = 2200, Height = 1300 } });
        context.SetDefaultTimeout(20_000);
        var page = await context.NewPageAsync();
        page.Dialog += (_, dialog) => _ = dialog.AcceptAsync(); // "replace the data model?" and similar confirmations
        await page.GotoAsync("/");
        await page.GetByRole(AriaRole.List, new() { Name = "Development users" }).GetByRole(AriaRole.Button, new() { Name = userName }).ClickAsync();
        await page.GetByRole(AriaRole.Navigation, new() { Name = "Main" }).WaitForAsync();
        return page;
    }

    /// <summary>An HTTP client for the API, signed in as a synthetic user (dev token).</summary>
    public HttpClient ApiClient(string userId)
    {
        var client = new HttpClient { BaseAddress = new Uri(ApiUrl) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"dev:{userId}");
        return client;
    }

    public static string Fixture(string relative) => Path.Combine(AppContext.BaseDirectory, "fixtures", relative);

    private void Start(string name, string file, string arguments, string workingDirectory, string logFile, Dictionary<string, string> environment)
    {
        var info = new ProcessStartInfo(file, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var (key, value) in environment)
        {
            info.Environment[key] = value;
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {name}.");
        var log = new StreamWriter(logFile, append: true) { AutoFlush = true };
        var gate = new object();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (gate) { log.WriteLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (gate) { log.WriteLine(e.Data); } } };
        process.Exited += (_, _) => { lock (gate) { log.Dispose(); } };
        process.EnableRaisingEvents = true;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _processes.Add(process);
    }

    private static async Task RunToEndAsync(string file, string arguments, string workingDirectory, string logFile, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(file, arguments)
        {
            WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        await process.WaitForExitAsync(cts.Token);
        await File.WriteAllTextAsync(logFile, await output + await error);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'{file} {arguments}' failed with exit code {process.ExitCode}; see {logFile}.");
        }
    }

    private static async Task WaitForAsync(string url, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                using var response = await http.GetAsync(new Uri(url));
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                // not up yet
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"{url} did not come up within {timeout.TotalSeconds:0} s.");
            }

            await Task.Delay(500);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Verso.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Verso.slnx) not found.");
    }
}

[CollectionDefinition(Name)]
public sealed class E2EGroup : ICollectionFixture<E2EEnvironment>
{
    public const string Name = "Running MVP";
}
