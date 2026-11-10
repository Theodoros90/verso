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
        await _database.MigrateAsync(); // before the host starts polling it (see TestDatabase.MigrateAsync)
        Factory = new VersoApiFactory(_database.ConnectionString);
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
