using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verso.Infrastructure.Persistence;

namespace Verso.Api.Tests;

/// <summary>
/// One migrated throwaway database plus API host, shared by the tests in <see cref="ApiTestGroup"/>.
/// Tests isolate themselves by creating their own templates (unique keys), not by resetting the database.
/// </summary>
public sealed class MigratedDatabaseFixture : IAsyncLifetime
{
    private readonly TestDatabase _database = new("api");

    internal VersoApiFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Factory = new VersoApiFactory(_database.ConnectionString);
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<VersoDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _database.DropAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class ApiTestGroup : ICollectionFixture<MigratedDatabaseFixture>
{
    public const string Name = "API with migrated database";
}
