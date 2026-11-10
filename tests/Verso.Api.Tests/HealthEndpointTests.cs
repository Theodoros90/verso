using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verso.Infrastructure.Persistence;

namespace Verso.Api.Tests;

public sealed class HealthEndpointTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Health_IsHealthy_WhenDatabaseIsMigrated()
    {
        var db = new TestDatabase("healthy");
        try
        {
            await db.MigrateAsync();
            await using var factory = new VersoApiFactory(db.ConnectionString);

            using var response = await factory.CreateClient().GetAsync(new Uri("/api/health", UriKind.Relative));
            var body = await ReadAsync(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("Healthy", body.Status);
            var check = Assert.Single(body.Checks);
            Assert.Equal("database", check.Name);
            Assert.Equal("Healthy", check.Status);
            Assert.Equal(0, check.Data!["pendingMigrations"].GetInt32());
            Assert.True(check.Data["appliedMigrations"].GetInt32() >= 1);
        }
        finally
        {
            await db.DropAsync();
        }
    }

    [Fact]
    public async Task Health_IsDegraded_WhenMigrationsArePending()
    {
        var db = new TestDatabase("pending");
        try
        {
            await db.CreateEmptyAsync();
            await using var factory = new VersoApiFactory(db.ConnectionString);

            using var response = await factory.CreateClient().GetAsync(new Uri("/api/health", UriKind.Relative));
            var body = await ReadAsync(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("Degraded", body.Status);
            var check = Assert.Single(body.Checks);
            Assert.Equal("Degraded", check.Status);
            Assert.True(check.Data!["pendingMigrations"].GetInt32() >= 1);
        }
        finally
        {
            await db.DropAsync();
        }
    }

    [Fact]
    public async Task Health_IsUnhealthy_AndLeaksNoConnectionDetails_WhenDatabaseIsMissing()
    {
        var db = new TestDatabase("missing"); // never created
        await using var factory = new VersoApiFactory(db.ConnectionString);

        using var response = await factory.CreateClient().GetAsync(new Uri("/api/health", UriKind.Relative));
        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<HealthBody>(raw, Json)!;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", body.Status);
        Assert.Equal("Unhealthy", Assert.Single(body.Checks).Status);
        Assert.DoesNotContain(db.Name, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localdb", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitialMigration_CreatesOneSchemaPerModule()
    {
        var db = new TestDatabase("schemas");
        try
        {
            await db.MigrateAsync();

            var schemas = new List<string>();
            await using var connection = new SqlConnection(db.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sys.schemas";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                schemas.Add(reader.GetString(0));
            }

            Assert.All(Schemas.All, s => Assert.Contains(s, schemas));
        }
        finally
        {
            await db.DropAsync();
        }
    }

    [Fact]
    public async Task Model_HasNoChangesMissingFromMigrations()
    {
        // Fails when an entity change is made without adding a migration. Needs no database connection.
        await using var factory = new VersoApiFactory(new TestDatabase("model").ConnectionString);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<VersoDbContext>();

        Assert.False(context.Database.HasPendingModelChanges());
    }

    private static async Task<HealthBody> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<HealthBody>(Json))!;

    private sealed record HealthBody(string Status, double TotalDurationMs, List<HealthCheck> Checks);

    private sealed record HealthCheck(string Name, string Status, string? Description, double DurationMs, Dictionary<string, JsonElement>? Data);
}
