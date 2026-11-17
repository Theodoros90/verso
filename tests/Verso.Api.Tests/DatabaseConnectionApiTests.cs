using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Verso.Application;
using Verso.Application.Connections;
using Verso.Application.Mail;
using Verso.Infrastructure.Connections;
using Verso.Infrastructure.Security;

namespace Verso.Api.Tests;

/// <summary>Stands in for SQL Server: remembers the settings it was asked to try and answers as told.</summary>
internal sealed class RecordingDatabaseProbe : IDatabaseProbe
{
    public List<DatabaseConnectionSettings> Seen { get; } = [];
    public DatabaseProbeResult Result { get; set; } = new(true, null, null, "Microsoft SQL Server 2022 (test)");

    public Task<DatabaseProbeResult> ProbeAsync(DatabaseConnectionSettings settings, CancellationToken ct)
    {
        Seen.Add(settings);
        return Task.FromResult(Result);
    }
}

/// <summary>Database connections managed in the app (ADR 0016): administrators only, write-only encrypted password, audited without values, tested by the Worker with SELECT 1 only.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class DatabaseConnectionApiTests(MigratedDatabaseFixture fixture) : IDisposable
{
    private const string Secret = "db-s3cret-for-tests";
    private const string ServerName = "sql.private-host.example";
    private const string Login = "reader_private";
    private readonly HttpClient _client = fixture.Factory.CreateClient();

    public void Dispose() => _client.Dispose();

    private static string UniqueKey() => $"db-{Guid.NewGuid():N}"[..20];

    private static object Body(string key, string? password = Secret, string server = ServerName, string userName = Login, string database = "") => new
    {
        key, name = "DB " + key, server, database, userName, encrypt = true, trustServerCertificate = false, timeoutSeconds = 15, password,
    };

    private async Task<JsonElement> CreateAsync(string key, string? password = Secret)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/database-connections", Body(key, password));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private DatabaseConnectionTestRunner Runner(IServiceScope scope, IDatabaseProbe probe, DatabaseConnectionOptions? options = null, ISecretProtector? protector = null, TimeProvider? clock = null) =>
        new(scope.ServiceProvider.GetRequiredService<IVersoDbContext>(), protector ?? fixture.Factory.Services.GetRequiredService<ISecretProtector>(), probe, options ?? new DatabaseConnectionOptions(), clock ?? TimeProvider.System);

    private async Task<Guid> RequestTestAsync(Guid connectionId)
    {
        var response = await _client.PostAsync($"/api/v1/database-connections/{connectionId}/tests", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> GetTestAsync(Guid testId) =>
        await (await _client.GetAsync($"/api/v1/database-connections/tests/{testId}")).Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Create_StoresTheConnection_AndNeverReturnsThePassword()
    {
        var key = UniqueKey();

        var created = await CreateAsync(key);

        Assert.True(created.GetProperty("hasPassword").GetBoolean());
        Assert.Equal(ServerName, created.GetProperty("server").GetString());
        Assert.DoesNotContain(Secret, created.GetRawText(), StringComparison.Ordinal);
        var list = await _client.GetStringAsync("/api/v1/database-connections?includeInactive=true");
        Assert.Contains(key, list, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePassword_IsEncryptedInTheDatabase_AndOnlyTheWorkerSideRunnerReadsItBack()
    {
        var id = (await CreateAsync(UniqueKey())).GetProperty("id").GetGuid();
        var testId = await RequestTestAsync(id);
        var probe = new RecordingDatabaseProbe();

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await Runner(scope, probe).RunAsync(testId, CancellationToken.None);
        }

        var seen = Assert.Single(probe.Seen);
        Assert.Equal(Secret, seen.Password);
        Assert.Equal(ServerName, seen.Server);
        Assert.Equal(Login, seen.UserName);
        using var check = fixture.Factory.Services.CreateScope();
        var stored = check.ServiceProvider.GetRequiredService<IVersoDbContext>().DatabaseConnections.Single(c => c.Id == id).PasswordProtected;
        Assert.NotNull(stored);
        Assert.DoesNotContain(Secret, stored, StringComparison.Ordinal);
        Assert.StartsWith("v1.", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_WithoutAPassword_KeepsIt_WithAValue_ReplacesIt_AndClearRemovesIt()
    {
        var key = UniqueKey();
        var id = (await CreateAsync(key)).GetProperty("id").GetGuid();

        var keep = await _client.PutAsJsonAsync($"/api/v1/database-connections/{id}", Body(key, password: null, server: "sql2.private-host.example"));
        Assert.Equal(HttpStatusCode.OK, keep.StatusCode);
        Assert.True((await keep.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hasPassword").GetBoolean());

        var replaced = await _client.PutAsJsonAsync($"/api/v1/database-connections/{id}", Body(key, password: "another-db-pass"));
        Assert.True((await replaced.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hasPassword").GetBoolean());

        var cleared = await _client.PutAsJsonAsync($"/api/v1/database-connections/{id}", new { name = "DB", server = ServerName, userName = Login, clearPassword = true });
        Assert.False((await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hasPassword").GetBoolean());
    }

    [Fact]
    public async Task EveryChange_IsAudited_WithoutAnyValue()
    {
        var key = UniqueKey();
        var id = (await CreateAsync(key)).GetProperty("id").GetGuid();
        await _client.PutAsJsonAsync($"/api/v1/database-connections/{id}", Body(key, password: "second-db-pass", server: "sql3.private-host.example"));
        await _client.PostAsync($"/api/v1/database-connections/{id}/deactivate", null);

        var audit = await _client.GetStringAsync($"/api/v1/audit?entityType=DatabaseConnection&entityId={id}");

        foreach (var action in new[] { "DatabaseConnectionCreated", "DatabaseConnectionUpdated", "DatabaseConnectionPasswordChanged", "DatabaseConnectionDeactivated" })
        {
            Assert.Contains(action, audit, StringComparison.Ordinal);
        }

        foreach (var value in new[] { Secret, "second-db-pass", "sql3.private-host.example", ServerName, Login })
        {
            Assert.DoesNotContain(value, audit, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Bad Key", ServerName, Login, "key")]
    [InlineData("good-key-1", "host;Encrypt=false", Login, "server")]
    [InlineData("good-key-2", "host name", Login, "server")]
    [InlineData("good-key-3", "tcp:host", Login, "server")]
    [InlineData("good-key-4", ServerName, "", "userName")]
    [InlineData("good-key-5", ServerName, "us;er", "userName")]
    public async Task Validation_RefusesAKeyOrServerOrLoginThatCouldChangeTheConnectionString(string key, string server, string login, string field)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/database-connections", Body(key, server: server, userName: login));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sql.example.com")]
    [InlineData("sql.example.com\\INSTANCE1")]
    [InlineData("sql.example.com,1433")]
    [InlineData("10.0.0.5")]
    public async Task Validation_AcceptsHostNamesWithAnInstanceOrAPort(string server)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/database-connections", Body(UniqueKey(), server: server));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task ADuplicateKey_Conflicts_AndOnlyAdministratorsMayUseTheEndpoints()
    {
        var key = UniqueKey();
        await CreateAsync(key);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync("/api/v1/database-connections", Body(key))).StatusCode);

        foreach (var user in new[] { "dev.viewer", "dev.template-author", "dev.workflow-author", "dev.auditor", "dev.operator" })
        {
            using var other = fixture.Factory.CreateClientAs(user);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/v1/database-connections")).StatusCode);
        }
    }

    [Fact]
    public async Task WithoutAMasterKey_APasswordIsRefused_ButAConnectionWithoutOneIsFine()
    {
        using var noKey = fixture.Factory.WithWebHostBuilder(b => b.UseSetting("Secrets:MasterKey", ""));
        using var client = noKey.CreateClientAs("dev.admin");

        var refused = await client.PostAsJsonAsync("/api/v1/database-connections", Body(UniqueKey(), Secret));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Secrets:MasterKey", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/database-connections", Body(UniqueKey(), password: null))).StatusCode);
    }

    [Fact]
    public async Task Test_IsQueued_RunsOnTheWorker_AndTheResultNeverShowsServerLoginOrPassword()
    {
        var id = (await CreateAsync(UniqueKey())).GetProperty("id").GetGuid();
        var testId = await RequestTestAsync(id);
        Assert.Equal("Queued", (await GetTestAsync(testId)).GetProperty("status").GetString());
        var probe = new RecordingDatabaseProbe();

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await Runner(scope, probe).RunAsync(testId, CancellationToken.None);
            await Runner(scope, probe).RunAsync(testId, CancellationToken.None); // a duplicate delivery does nothing
        }

        Assert.Single(probe.Seen);
        var result = await GetTestAsync(testId);
        Assert.Equal("Succeeded", result.GetProperty("status").GetString());
        Assert.Equal("Microsoft SQL Server 2022 (test)", result.GetProperty("serverVersion").GetString());
        foreach (var value in new[] { Secret, ServerName, Login })
        {
            Assert.DoesNotContain(value, result.GetRawText(), StringComparison.Ordinal);
        }

        var audit = await _client.GetStringAsync($"/api/v1/audit?entityType=DatabaseConnectionTest&entityId={testId}");
        Assert.Contains("DatabaseConnectionTestRequested", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedProbe_GivesAFailedTest_WithTheCode()
    {
        var testId = await RequestTestAsync((await CreateAsync(UniqueKey())).GetProperty("id").GetGuid());
        var probe = new RecordingDatabaseProbe { Result = new(false, "db.login_failed", "The server rejected the login or the password.", null) };

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await Runner(scope, probe).RunAsync(testId, CancellationToken.None);
        }

        var result = await GetTestAsync(testId);
        Assert.Equal("Failed", result.GetProperty("status").GetString());
        Assert.Equal("db.login_failed", result.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AServerNotOnTheAllowedList_IsNeverConnectedTo()
    {
        var testId = await RequestTestAsync((await CreateAsync(UniqueKey())).GetProperty("id").GetGuid());
        var probe = new RecordingDatabaseProbe();

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await Runner(scope, probe, new DatabaseConnectionOptions { AllowedServers = ["only-this-host.example"] }).RunAsync(testId, CancellationToken.None);
        }

        Assert.Empty(probe.Seen);
        Assert.Equal("db.server_not_allowed", (await GetTestAsync(testId)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AnAllowedServer_IsMatchedByHostEvenWithAnInstanceOrPort()
    {
        var key = UniqueKey();
        var created = await _client.PostAsJsonAsync("/api/v1/database-connections", Body(key, server: "Allowed.Host.Example,1433"));
        var testId = await RequestTestAsync((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        var probe = new RecordingDatabaseProbe();

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await Runner(scope, probe, new DatabaseConnectionOptions { AllowedServers = ["allowed.host.example"] }).RunAsync(testId, CancellationToken.None);
        }

        Assert.Single(probe.Seen);
    }

    [Fact]
    public async Task APasswordThatCannotBeRead_FailsTheTest_WithoutConnecting()
    {
        var testId = await RequestTestAsync((await CreateAsync(UniqueKey())).GetProperty("id").GetGuid());
        var probe = new RecordingDatabaseProbe();

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await Runner(scope, probe, protector: new AesGcmSecretProtector(AesGcmSecretProtector.GenerateKey())).RunAsync(testId, CancellationToken.None);
        }

        Assert.Empty(probe.Seen);
        Assert.Equal("db.password_unreadable", (await GetTestAsync(testId)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ASwitchedOffConnection_Is409_AnUnknownOne404_AndOldTestsAreCleanedUp()
    {
        var id = (await CreateAsync(UniqueKey())).GetProperty("id").GetGuid();
        var testId = await RequestTestAsync(id);
        await _client.PostAsync($"/api/v1/database-connections/{id}/deactivate", null);
        var inactive = await _client.PostAsync($"/api/v1/database-connections/{id}/tests", null);
        Assert.Equal(HttpStatusCode.Conflict, inactive.StatusCode);
        Assert.Equal("database_connection.inactive", (await inactive.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/v1/database-connections/{Guid.NewGuid()}/tests", null)).StatusCode);

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            Assert.True(await Runner(scope, new RecordingDatabaseProbe(), clock: new Shifted(TimeSpan.FromDays(2))).CleanupAsync(CancellationToken.None) >= 1);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/database-connections/tests/{testId}")).StatusCode);
    }

    private sealed class Shifted(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
    }
}

/// <summary>Our own wording for SQL Server errors: never the server's text, which can name the server or the login.</summary>
public sealed class SqlServerProbeTests
{
    [Theory]
    [InlineData(18456, "db.login_failed")]
    [InlineData(18487, "db.password_expired")]
    [InlineData(4060, "db.cannot_open_database")]
    [InlineData(-2, "db.timeout")]
    [InlineData(53, "db.unreachable")]
    [InlineData(10060, "db.unreachable")]
    [InlineData(999999, "db.test_failed")]
    public void Describe_MapsErrorNumbersToOurCodes(int number, string code)
    {
        var (actual, message) = SqlServerProbe.Describe(number, "Login failed for user 'reader_private' on server 'sql.private-host.example'.");

        Assert.Equal(code, actual);
        Assert.DoesNotContain("reader_private", message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-host", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_RecognisesAnUntrustedCertificate_FromTheServerText_ButDoesNotRepeatIt()
    {
        var (code, message) = SqlServerProbe.Describe(0, "The certificate chain was issued by an authority that is not trusted (host sql.private-host.example).");

        Assert.Equal("db.certificate_untrusted", code);
        Assert.DoesNotContain("private-host", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sql.example.com", "sql.example.com")]
    [InlineData("sql.example.com\\INST", "sql.example.com")]
    [InlineData("sql.example.com,1433", "sql.example.com")]
    public void HostOf_StripsInstanceAndPort(string server, string host) => Assert.Equal(host, DatabaseServers.HostOf(server));
}
