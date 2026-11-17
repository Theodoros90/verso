using System.Net.Http.Json;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Npgsql;
using Verso.Application;
using Verso.Application.Connections;
using Verso.Databases;
using Verso.Infrastructure.Connections;

namespace Verso.Api.Tests;

/// <summary>The database types (ADR 0016 addendum 1): the rules that differ per provider, the router, the connection strings and the error wording. No database server is contacted.</summary>
public sealed class DatabaseProviderTests
{
    private static DatabaseConnectionSettings Settings(string provider, string server = "db.example.test", int port = 0, string database = "app", string password = "pa;ss=\"w'ord", bool encrypt = true, bool trust = false) =>
        new(provider, server, port, database, "reader", password, encrypt, trust, TimeSpan.FromSeconds(10));

    [Theory]
    [InlineData("SqlServer", 1433)]
    [InlineData("PostgreSql", 5432)]
    [InlineData("MySql", 3306)]
    [InlineData("Oracle", 1521)]
    public void EachProvider_HasItsDefaultPort_AndAnExplicitPortWins(string provider, int port)
    {
        Assert.True(DatabaseProviders.IsValid(provider));
        Assert.Equal(port, DatabaseProviders.EffectivePort(provider, 0));
        Assert.Equal(9999, DatabaseProviders.EffectivePort(provider, 9999));
    }

    [Theory]
    [InlineData("db.example.com", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("db.example.com\\INST", false)] // an instance is a SQL Server thing
    [InlineData("db.example.com,5432", false)]  // the port is its own field
    [InlineData("db;Host=evil", false)]
    [InlineData("db host", false)]
    [InlineData("", false)]
    public void ServersOtherThanSqlServer_AreAPlainHost(string server, bool valid)
    {
        foreach (var provider in new[] { DatabaseProviders.PostgreSql, DatabaseProviders.MySql, DatabaseProviders.Oracle })
        {
            Assert.Equal(valid, DatabaseProviders.IsValidServer(provider, server));
        }
    }

    [Theory]
    [InlineData("db.example.com\\INST", true)]
    [InlineData("db.example.com,1433", true)]
    [InlineData("db;Encrypt=false", false)]
    public void SqlServer_StillAllowsAnInstanceOrPort(string server, bool valid) => Assert.Equal(valid, DatabaseProviders.IsValidServer(DatabaseProviders.SqlServer, server));

    [Fact]
    public void Oracle_NeedsAServiceName_AndHasNoTrustCertificateSwitch_OthersDoNot()
    {
        Assert.False(DatabaseProviders.IsValidDatabase(DatabaseProviders.Oracle, ""));
        Assert.True(DatabaseProviders.IsValidDatabase(DatabaseProviders.Oracle, "ORCLPDB1.example.com"));
        Assert.False(DatabaseProviders.IsValidDatabase(DatabaseProviders.Oracle, "svc)(HOST=evil"));
        Assert.True(DatabaseProviders.IsValidDatabase(DatabaseProviders.PostgreSql, ""));
        Assert.False(DatabaseProviders.SupportsTrustServerCertificate(DatabaseProviders.Oracle));
        Assert.True(DatabaseProviders.SupportsTrustServerCertificate(DatabaseProviders.PostgreSql));
    }

    [Fact]
    public async Task TheRouter_UsesTheProbeOfTheConnectionsProvider_AndAnUnknownOneFailsSafely()
    {
        var sql = new FakeProviderProbe(DatabaseProviders.SqlServer);
        var pg = new FakeProviderProbe(DatabaseProviders.PostgreSql);
        var router = new DatabaseProbeRouter([sql, pg]);

        Assert.True((await router.ProbeAsync(Settings(DatabaseProviders.PostgreSql), CancellationToken.None)).Ok);
        Assert.Equal(1, pg.Calls);
        Assert.Equal(0, sql.Calls);

        var missing = await router.ProbeAsync(Settings(DatabaseProviders.Oracle), CancellationToken.None);
        Assert.False(missing.Ok);
        Assert.Equal("db.provider_unavailable", missing.Code);
    }

    [Fact]
    public void ThePostgreSqlConnectionString_VerifiesTlsByDefault_IsReadOnly_AndKeepsAHostilePasswordInsideItsValue()
    {
        var verify = PostgreSqlProbe.Build(Settings(DatabaseProviders.PostgreSql));
        Assert.Equal(SslMode.VerifyFull, verify.SslMode);
        Assert.Equal(5432, verify.Port);
        Assert.Contains("default_transaction_read_only=on", verify.Options, StringComparison.Ordinal);
        Assert.False(verify.Pooling);
        Assert.Equal("pa;ss=\"w'ord", new NpgsqlConnectionStringBuilder(verify.ConnectionString).Password); // not split into options

        Assert.Equal(SslMode.Require, PostgreSqlProbe.Build(Settings(DatabaseProviders.PostgreSql, trust: true)).SslMode);
        Assert.Equal(SslMode.Disable, PostgreSqlProbe.Build(Settings(DatabaseProviders.PostgreSql, encrypt: false)).SslMode);
        Assert.Equal(6543, PostgreSqlProbe.Build(Settings(DatabaseProviders.PostgreSql, port: 6543)).Port);
    }

    [Fact]
    public void TheMySqlConnectionString_VerifiesTlsByDefault_AndKeepsAHostilePasswordInsideItsValue()
    {
        var verify = MySqlProbe.Build(Settings(DatabaseProviders.MySql));
        Assert.Equal(MySqlSslMode.VerifyFull, verify.SslMode);
        Assert.Equal(3306u, verify.Port);
        Assert.False(verify.Pooling);
        Assert.Equal("pa;ss=\"w'ord", new MySqlConnectionStringBuilder(verify.ConnectionString).Password);

        Assert.Equal(MySqlSslMode.Required, MySqlProbe.Build(Settings(DatabaseProviders.MySql, trust: true)).SslMode);
        Assert.Equal(MySqlSslMode.None, MySqlProbe.Build(Settings(DatabaseProviders.MySql, encrypt: false)).SslMode);
    }

    [Fact]
    public void TheOracleDescriptor_UsesTcpsWhenEncrypted_AndRefusesAnythingThatCouldInjectAnOption()
    {
        Assert.Equal("(DESCRIPTION=(ADDRESS=(PROTOCOL=TCPS)(HOST=db.example.test)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=ORCLPDB1)))", OracleProbe.BuildDescriptor("db.example.test", 1521, "ORCLPDB1", tcps: true));
        Assert.Contains("PROTOCOL=TCP)", OracleProbe.BuildDescriptor("db.example.test", 1521, "ORCLPDB1", tcps: false), StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => OracleProbe.BuildDescriptor("db)(HOST=evil", 1521, "svc", true));
        Assert.Throws<ArgumentException>(() => OracleProbe.BuildDescriptor("db.example.test", 1521, "svc)(ADDRESS=x", true));
        Assert.Throws<ArgumentException>(() => OracleProbe.BuildDescriptor("db.example.test", 0, "svc", true));
        Assert.Throws<ArgumentException>(() => OracleProbe.BuildDescriptor("db.example.test", 1521, "", true));
        var connectionString = OracleProbe.Build(Settings(DatabaseProviders.Oracle, database: "ORCLPDB1")).ConnectionString;
        var parsed = new Oracle.ManagedDataAccess.Client.OracleConnectionStringBuilder(connectionString);
        Assert.Equal("pa;ss=\"w'ord", parsed.Password); // quoted by the builder: the hostile password stays one value, it adds no option
        Assert.Equal("(DESCRIPTION=(ADDRESS=(PROTOCOL=TCPS)(HOST=db.example.test)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=ORCLPDB1)))", parsed.DataSource);
    }

    [Fact]
    public void SqlServer_AddsTheSeparatePortToTheDataSource_UnlessTheServerAlreadyHasOne()
    {
        Assert.Equal("db.example.test,1444", SqlServerProbe.DataSource(Settings(DatabaseProviders.SqlServer, port: 1444)));
        Assert.Equal("db.example.test,1555", SqlServerProbe.DataSource(Settings(DatabaseProviders.SqlServer, server: "db.example.test,1555", port: 1444)));
        Assert.Equal("db.example.test", SqlServerProbe.DataSource(Settings(DatabaseProviders.SqlServer)));
    }

    [Fact]
    public void NetworkProblems_AreRecognisedTheSameWayForEveryDriver()
    {
        foreach (var describe in new Func<Exception, string>[] { PostgreSqlProbe.Describe, MySqlProbe.Describe, OracleProbe.Describe })
        {
            Assert.Equal("db.unreachable", describe(new InvalidOperationException("x", new SocketException())));
            Assert.Equal("db.certificate_untrusted", describe(new InvalidOperationException("x", new AuthenticationException("bad certificate"))));
            Assert.Equal("db.timeout", describe(new TimeoutException()));
            Assert.Equal("db.test_failed", describe(new InvalidOperationException("something else")));
        }
    }

    [Fact]
    public void PostgreSqlErrors_AreMappedBySqlState()
    {
        Assert.Equal("db.login_failed", PostgreSqlProbe.Describe(new PostgresException("password authentication failed for user \"reader\"", "FATAL", "FATAL", "28P01")));
        Assert.Equal("db.cannot_open_database", PostgreSqlProbe.Describe(new PostgresException("database \"x\" does not exist", "FATAL", "FATAL", "3D000")));
    }

    [Fact]
    public void EveryFailureCode_HasOurOwnWording_ThatNamesNoServerOrLogin()
    {
        foreach (var code in new[] { "db.login_failed", "db.password_expired", "db.cannot_open_database", "db.timeout", "db.unreachable", "db.certificate_untrusted", "db.provider_unavailable", "db.test_failed" })
        {
            var message = DatabaseProbeMessages.For(code);
            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.Equal(message, DatabaseProbeResult.Failure(code).Message);
        }
    }

    private sealed class FakeProviderProbe(string provider) : IProviderProbe
    {
        public string Provider { get; } = provider;
        public int Calls { get; private set; }

        public Task<DatabaseProbeResult> ProbeAsync(DatabaseConnectionSettings settings, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(DatabaseProbeResult.Success("fake"));
        }
    }
}

/// <summary>Provider-specific rules at the API, and that the Worker side gets the provider and port.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class DatabaseProviderApiTests(MigratedDatabaseFixture fixture) : IDisposable
{
    private readonly HttpClient _client = fixture.Factory.CreateClient();

    public void Dispose() => _client.Dispose();

    private static string UniqueKey() => $"pv-{Guid.NewGuid():N}"[..20];

    private Task<HttpResponseMessage> PostAsync(object body) => _client.PostAsJsonAsync("/api/v1/database-connections", body);

    [Theory]
    [InlineData("PostgreSql", "db.example.test", 5432, "")]
    [InlineData("MySql", "db.example.test", 0, "app")]
    [InlineData("Oracle", "db.example.test", 1521, "ORCLPDB1")]
    [InlineData("SqlServer", "db.example.test\\INST", 0, "")]
    public async Task EachProvider_CanBeCreated_AndTheDtoSaysWhichOneItIs(string provider, string server, int port, string database)
    {
        var response = await PostAsync(new { key = UniqueKey(), provider, name = "Provider test", server, port, database, userName = "reader", password = "pw-for-tests" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(provider, dto.GetProperty("provider").GetString());
        Assert.Equal(port, dto.GetProperty("port").GetInt32());
        Assert.DoesNotContain("pw-for-tests", dto.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAProvider_ItIsSqlServer_ForCallersOfTheEarlierContract()
    {
        var response = await PostAsync(new { key = UniqueKey(), name = "Default provider", server = "db.example.test", userName = "reader" });

        Assert.Equal("SqlServer", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("provider").GetString());
    }

    [Theory]
    [InlineData("Db2", "db.example.test", 0, "", false, "provider")]
    [InlineData("PostgreSql", "db.example.test\\INST", 0, "", false, "server")]
    [InlineData("PostgreSql", "db.example.test,5432", 0, "", false, "server")]
    [InlineData("MySql", "db.example.test", 70000, "", false, "port")]
    [InlineData("Oracle", "db.example.test", 0, "", false, "database")]
    [InlineData("Oracle", "db.example.test", 0, "svc)(HOST=evil", false, "database")]
    [InlineData("Oracle", "db.example.test", 0, "ORCLPDB1", true, "trustServerCertificate")]
    public async Task ProviderRules_AreEnforced(string provider, string server, int port, string database, bool trust, string field)
    {
        var response = await PostAsync(new { key = UniqueKey(), provider, name = "Bad", server, port, database, userName = "reader", trustServerCertificate = trust });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheProvider_CannotBeChangedByAnUpdate()
    {
        var key = UniqueKey();
        var created = await (await PostAsync(new { key, provider = "PostgreSql", name = "Fixed", server = "db.example.test", userName = "reader" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();

        var updated = await _client.PutAsJsonAsync($"/api/v1/database-connections/{id}", new { provider = "Oracle", name = "Fixed", server = "db2.example.test", port = 6000, userName = "reader" });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var dto = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PostgreSql", dto.GetProperty("provider").GetString());
        Assert.Equal(6000, dto.GetProperty("port").GetInt32());
    }

    [Fact]
    public async Task TheWorkerSide_GetsTheProviderAndPort_ToPickTheRightDriver()
    {
        var response = await PostAsync(new { key = UniqueKey(), provider = "MySql", name = "Worker side", server = "db.example.test", port = 3307, userName = "reader", password = "pw-for-tests" });
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var testId = (await (await _client.PostAsync($"/api/v1/database-connections/{id}/tests", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var probe = new RecordingDatabaseProbe();

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var runner = new DatabaseConnectionTestRunner(
                scope.ServiceProvider.GetRequiredService<IVersoDbContext>(),
                fixture.Factory.Services.GetRequiredService<Verso.Application.Mail.ISecretProtector>(), probe, new DatabaseConnectionOptions(), TimeProvider.System);
            await runner.RunAsync(testId, CancellationToken.None);
        }

        var seen = Assert.Single(probe.Seen);
        Assert.Equal("MySql", seen.Provider);
        Assert.Equal(3307, seen.Port);
        Assert.Equal("pw-for-tests", seen.Password);
    }
}
