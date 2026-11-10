using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Verso.Infrastructure.Persistence;

namespace Verso.Api.Tests;

/// <summary>
/// Throwaway SQL Server databases for integration tests. Each instance uses a uniquely named database,
/// so test runs never touch Verso_Dev. Server defaults to LocalDB; override with VERSO_TEST_SQL_SERVER.
/// </summary>
internal sealed class TestDatabase
{
    private static readonly string Server =
        Environment.GetEnvironmentVariable("VERSO_TEST_SQL_SERVER") is { Length: > 0 } s ? s : @"(localdb)\MSSQLLocalDB";

    public TestDatabase(string purpose) => Name = $"Verso_Test_{purpose}_{Guid.NewGuid():N}";

    public string Name { get; }

    public string ConnectionString => BuildConnectionString(Name);

    /// <summary>Creates the database empty (no migrations applied).</summary>
    public async Task CreateEmptyAsync()
    {
        await using var connection = new SqlConnection(BuildConnectionString("master"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{Name}]";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Creates the database and applies all migrations <b>before</b> any API or Worker host points at it. Hosts poll
    /// the database from background services (e.g. the preview relay waits for it to exist); a pooled connection of
    /// theirs that lands between EF's CREATE DATABASE and its follow-up ALTER DATABASE blocks that ALTER until it
    /// times out. Migrating without a host avoids the race.
    /// </summary>
    public async Task MigrateAsync()
    {
        var options = new DbContextOptionsBuilder<VersoDbContext>().UseSqlServer(ConnectionString).Options;
        await using var context = new VersoDbContext(options);
        await context.Database.MigrateAsync();
    }

    /// <summary>Drops the database if it exists. Only ever called for Verso_Test_* databases.</summary>
    public async Task DropAsync()
    {
        if (!Name.StartsWith("Verso_Test_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to drop non-test database '{Name}'.");
        }

        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(BuildConnectionString("master"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF DB_ID(N'{Name}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{Name}];
            END
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static string BuildConnectionString(string database) => new SqlConnectionStringBuilder
    {
        DataSource = Server,
        InitialCatalog = database,
        IntegratedSecurity = true,
        TrustServerCertificate = true,
        ConnectTimeout = 5,
        // Test databases appear and disappear while hosts run (e.g. the preview relay polls before a test creates
        // its database). Without this, one failed login makes the pool replay that error for seconds afterwards.
        PoolBlockingPeriod = PoolBlockingPeriod.NeverBlock,
    }.ConnectionString;
}
