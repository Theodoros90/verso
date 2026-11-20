using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Verso.Application;
using Verso.Application.Connections;
using Verso.Application.Documents;
using Verso.Application.Executions;
using Verso.Databases;
using Verso.Infrastructure;
using Verso.Rendering;
using Verso.Rendering.Docx;

namespace Verso.Api.Tests;

/// <summary>
/// Final review: the Worker's registrations give the execution engine the real query service with all four runners, and the API never references
/// the database drivers (ADR 0019 §5: the API never connects to a customer database).
/// </summary>
public sealed class QueryDatabaseWiringTests
{
    [Fact]
    public void WorkerRegistrations_ResolveTheEngine_WithTheRealQueryService_AndAllFourRunners()
    {
        // The Query-database part of src/Verso.Worker/Program.cs, plus fakes for what needs Chromium, files or Hangfire. Nothing connects: resolving opens no connection.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Verso"] = "Server=(localdb)\\MSSQLLocalDB;Database=verso-wiring-not-opened;Integrated Security=true",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVersoPersistence(configuration);
        services.AddVersoWorkerUseCases();
        services.AddVersoDatabaseConnections(configuration);
        services.AddVersoDatabaseDrivers();
        services.AddVersoApiConnections(configuration);
        services.AddSingleton<IPdfPrinter, FakePdfPrinter>();
        services.AddSingleton<IDocxWriter, FakeDocxWriter>();
        services.AddSingleton<IPdfCombiner, FakePdfCombiner>();
        services.AddSingleton<IEmailSender, FakeEmailSender>();
        services.AddSingleton<IPrintSpooler, FakePrintSpooler>();
        services.AddSingleton<IDocumentStore, TestDocumentStore>();
        services.AddSingleton<IExecutionJobs, RecordingExecutionJobs>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ExecutionEngine>());
        Assert.IsType<DatabaseQueryService>(scope.ServiceProvider.GetRequiredService<IDatabaseQuery>());
        var providers = scope.ServiceProvider.GetServices<IProviderQueryRunner>().Select(r => r.Provider).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { DatabaseProviders.MySql, DatabaseProviders.Oracle, DatabaseProviders.PostgreSql, DatabaseProviders.SqlServer }.Order(StringComparer.Ordinal), providers);

        // Write to database step (Phase 2): the write service resolves in a scope; the SQL Server write runner is registered (the other providers arrive with Verso.Databases).
        Assert.IsType<DatabaseWriteService>(scope.ServiceProvider.GetRequiredService<IDatabaseWrite>());
        Assert.Contains(scope.ServiceProvider.GetServices<IProviderWriteRunner>(), r => r.Provider == DatabaseProviders.SqlServer);
    }

    [Fact]
    public void Worker_DI_resolves_the_engine_with_IDatabaseWrite()
    {
        // Write to database step, Task 6: the engine now needs IDatabaseWrite. With the Worker's registrations it resolves (ValidateScopes: the scoped write
        // service is taken in a scope); without IDatabaseWrite it does not, so the engine really asks for it.
        using (var provider = WorkerServices().BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }))
        using (var scope = provider.CreateScope())
        {
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<ExecutionEngine>());
            Assert.IsType<DatabaseWriteService>(scope.ServiceProvider.GetRequiredService<IDatabaseWrite>());
        }

        var withoutWrite = WorkerServices();
        withoutWrite.RemoveAll<IDatabaseWrite>();
        using var broken = withoutWrite.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var brokenScope = broken.CreateScope();
        var ex = Assert.Throws<InvalidOperationException>(() => brokenScope.ServiceProvider.GetRequiredService<ExecutionEngine>());
        Assert.Contains(nameof(IDatabaseWrite), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Worker_DI_resolves_the_engine_with_IApiFetch()
    {
        // Fetch from API step (Phase 3): the engine needs IApiFetch. With the Worker's registrations (AddVersoApiConnections) it resolves the real, scoped
        // ApiFetchService (ValidateScopes); without IApiFetch it does not, so the engine really asks for it. Resolving sends nothing.
        using (var provider = WorkerServices().BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }))
        using (var scope = provider.CreateScope())
        {
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<ExecutionEngine>());
            Assert.IsType<ApiFetchService>(scope.ServiceProvider.GetRequiredService<IApiFetch>());
        }

        var withoutFetch = WorkerServices();
        withoutFetch.RemoveAll<IApiFetch>();
        using var broken = withoutFetch.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var brokenScope = broken.CreateScope();
        var ex = Assert.Throws<InvalidOperationException>(() => brokenScope.ServiceProvider.GetRequiredService<ExecutionEngine>());
        Assert.Contains(nameof(IApiFetch), ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The Worker's registrations (src/Verso.Worker/Program.cs) relevant to the engine, with fakes for what needs Chromium, files or Hangfire. Nothing connects.</summary>
    private static ServiceCollection WorkerServices()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Verso"] = "Server=(localdb)\\MSSQLLocalDB;Database=verso-wiring-not-opened;Integrated Security=true",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVersoPersistence(configuration);
        services.AddVersoWorkerUseCases();
        services.AddVersoDatabaseConnections(configuration);
        services.AddVersoDatabaseDrivers();
        services.AddVersoApiConnections(configuration);
        services.AddSingleton<IPdfPrinter, FakePdfPrinter>();
        services.AddSingleton<IDocxWriter, FakeDocxWriter>();
        services.AddSingleton<IPdfCombiner, FakePdfCombiner>();
        services.AddSingleton<IEmailSender, FakeEmailSender>();
        services.AddSingleton<IPrintSpooler, FakePrintSpooler>();
        services.AddSingleton<IDocumentStore, TestDocumentStore>();
        services.AddSingleton<IExecutionJobs, RecordingExecutionJobs>();
        return services;
    }

    [Fact]
    public async Task TheApiHost_CannotResolve_TheWriteService_OrAnyWriteRunner()
    {
        await using var factory = new VersoApiFactory(new TestDatabase("nowrite").ConnectionString); // no DB access needed
        using var scope = factory.Services.CreateScope();

        Assert.Null(scope.ServiceProvider.GetService<IDatabaseWrite>());
        Assert.Empty(scope.ServiceProvider.GetServices<IProviderWriteRunner>());
    }

    [Fact]
    public void TheApi_DoesNotReference_TheDatabaseDrivers()
    {
        // Read from the compiled Verso.Api assembly's metadata (as RendererPurityTests does for the renderer).
        using var stream = File.OpenRead(typeof(Program).Assembly.Location);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var assemblies = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToList();

        Assert.Contains("Verso.Infrastructure", assemblies); // the check reads the right assembly
        Assert.DoesNotContain("Verso.Databases", assemblies);
        Assert.DoesNotContain(assemblies, a => a is "Npgsql" or "MySqlConnector" or "Oracle.ManagedDataAccess");

        // And the project file itself names no reference to it (a reference whose types are unused would not show in the metadata).
        var project = Path.Combine(RepositoryRoot(), "src", "Verso.Api", "Verso.Api.csproj");
        Assert.DoesNotContain("Verso.Databases", File.ReadAllText(project), StringComparison.OrdinalIgnoreCase);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Verso.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Verso.slnx not found above the test output.");
    }
}
