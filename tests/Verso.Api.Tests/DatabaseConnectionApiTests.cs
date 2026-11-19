using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verso.Application;
using Verso.Application.Connections;
using Verso.Application.Mail;
using Verso.Infrastructure.Connections;
using Verso.Infrastructure.Persistence;
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

    private async Task<JsonElement> AuditEntriesAsync(Guid id, string action)
    {
        var page = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/audit?entityType=DatabaseConnection&entityId={id}&pageSize=100");
        return JsonSerializer.SerializeToElement(page.GetProperty("items").EnumerateArray().Where(e => e.GetProperty("action").GetString() == action).ToList());
    }

    private Task<HttpResponseMessage> PutWritesAsync(Guid id, string key, bool? allowWrites) =>
        _client.PutAsJsonAsync($"/api/v1/database-connections/{id}", new
        {
            name = "DB " + key, server = ServerName, userName = Login, encrypt = true, trustServerCertificate = false, timeoutSeconds = 15, allowWrites,
        });

    [Fact]
    public async Task New_connection_does_not_allow_writes()
    {
        var created = await CreateAsync(UniqueKey());

        Assert.False(created.GetProperty("allowWrites").GetBoolean());
    }

    [Fact]
    public async Task Admin_can_switch_writes_on_and_off_and_it_is_audited()
    {
        var key = UniqueKey();
        var id = (await CreateAsync(key)).GetProperty("id").GetGuid();

        var on = await PutWritesAsync(id, key, true);
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.True((await on.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("allowWrites").GetBoolean());
        var same = await PutWritesAsync(id, key, true); // the same value again writes no audit
        Assert.True((await same.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("allowWrites").GetBoolean());
        var off = await PutWritesAsync(id, key, false);
        Assert.False((await off.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("allowWrites").GetBoolean());

        var changes = (await AuditEntriesAsync(id, "DatabaseConnectionWritesChanged")).EnumerateArray().ToList();
        Assert.Equal(2, changes.Count);
        var states = changes.Select(c => c.GetProperty("details").GetProperty("allowWrites").GetBoolean()).Order().ToList();
        Assert.Equal([false, true], states);
        Assert.All(changes, c => Assert.Equal(key, c.GetProperty("details").GetProperty("key").GetString()));
        var updated = (await AuditEntriesAsync(id, "DatabaseConnectionUpdated")).GetRawText();
        Assert.Contains("allowWrites", updated, StringComparison.Ordinal);
        var all = await _client.GetStringAsync($"/api/v1/audit?entityType=DatabaseConnection&entityId={id}");
        foreach (var value in new[] { Secret, ServerName, Login })
        {
            Assert.DoesNotContain(value, all, StringComparison.Ordinal);
        }

        using var other = fixture.Factory.CreateClientAs("dev.workflow-author");
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync($"/api/v1/database-connections/{id}", new { name = "x", server = ServerName, userName = Login, allowWrites = true })).StatusCode);
    }

    [Fact]
    public async Task Update_without_the_field_keeps_it()
    {
        var key = UniqueKey();
        var id = (await CreateAsync(key)).GetProperty("id").GetGuid();
        await PutWritesAsync(id, key, true);

        var kept = await _client.PutAsJsonAsync($"/api/v1/database-connections/{id}", Body(key, password: null, server: "sql9.private-host.example"));

        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.True((await kept.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("allowWrites").GetBoolean());
        Assert.Single((await AuditEntriesAsync(id, "DatabaseConnectionWritesChanged")).EnumerateArray());
    }

    [Fact]
    public async Task Create_with_allowWrites_true_is_audited()
    {
        var key = UniqueKey();
        var response = await _client.PostAsJsonAsync("/api/v1/database-connections", new
        {
            key, name = "DB " + key, server = ServerName, userName = Login, allowWrites = true, password = Secret,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(created.GetProperty("allowWrites").GetBoolean());
        var change = Assert.Single((await AuditEntriesAsync(created.GetProperty("id").GetGuid(), "DatabaseConnectionWritesChanged")).EnumerateArray());
        Assert.True(change.GetProperty("details").GetProperty("allowWrites").GetBoolean());
        Assert.Equal(key, change.GetProperty("details").GetProperty("key").GetString());
    }

    [Fact]
    public async Task Options_report_allowsWrites()
    {
        var on = "aaa-" + UniqueKey();
        var off = "aab-" + UniqueKey();
        var inactive = UniqueKey();
        var onId = (await CreateAsync(on)).GetProperty("id").GetGuid();
        await CreateAsync(off);
        var inactiveId = (await CreateAsync(inactive)).GetProperty("id").GetGuid();
        await PutWritesAsync(onId, on, true);
        await _client.PostAsync($"/api/v1/database-connections/{inactiveId}/deactivate", null);

        using var author = fixture.Factory.CreateClientAs("dev.workflow-author");
        var text = await author.GetStringAsync("/api/v1/database-connection-options");

        var options = JsonSerializer.Deserialize<JsonElement>(text).EnumerateArray().ToList();
        Assert.DoesNotContain(options, o => o.GetProperty("key").GetString() == inactive);
        var optionOn = options.Single(o => o.GetProperty("key").GetString() == on);
        var optionOff = options.Single(o => o.GetProperty("key").GetString() == off);
        Assert.True(optionOn.GetProperty("allowsWrites").GetBoolean());
        Assert.False(optionOff.GetProperty("allowsWrites").GetBoolean());
        Assert.Equal(["allowsWrites", "key", "name", "provider"], optionOn.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        foreach (var value in new[] { ServerName, Login, Secret, "userName", "server", "database", "port" })
        {
            Assert.DoesNotContain(value, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Existing_rows_default_to_off()
    {
        var key = UniqueKey();
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VersoDbContext>();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO [connections].[DatabaseConnections] ([Id], [Key], [Name], [Server], [Database], [UserName], [Encrypt], [TrustServerCertificate], [TimeoutSeconds], [IsActive], [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy]) VALUES ({id}, {key}, N'Old', N'old.example', N'', N'u', 1, 0, 15, 1, {now}, N'x', {now}, N'x')");

        var row = await scope.ServiceProvider.GetRequiredService<IVersoDbContext>().DatabaseConnections.AsNoTracking().SingleAsync(c => c.Id == id);
        Assert.False(row.AllowWrites);
    }

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
    public async Task Options_ListActiveConnectionsOnly_ByName_WithNothingButKeyNameAndProvider()
    {
        var active = "aaa-" + UniqueKey();
        var inactive = UniqueKey();
        var later = "zzz-" + UniqueKey();
        await CreateAsync(later);
        await CreateAsync(active);
        var inactiveId = (await CreateAsync(inactive)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"/api/v1/database-connections/{inactiveId}/deactivate", null)).StatusCode);

        using var author = fixture.Factory.CreateClientAs("dev.workflow-author");
        var response = await author.GetAsync("/api/v1/database-connection-options");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var options = JsonSerializer.Deserialize<JsonElement>(text).EnumerateArray().ToList();
        Assert.Contains(options, o => o.GetProperty("key").GetString() == active);
        Assert.DoesNotContain(options, o => o.GetProperty("key").GetString() == inactive);
        var option = options.Single(o => o.GetProperty("key").GetString() == active);
        Assert.Equal(["allowsWrites", "key", "name", "provider"], option.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("SqlServer", option.GetProperty("provider").GetString());
        Assert.Equal("DB " + active, option.GetProperty("name").GetString());
        Assert.DoesNotContain(ServerName, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Login, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        // Ordered by name: only the relative order of the two connections made here is asserted (other tests leave others behind, and the database collation decides the rest).
        var keys = options.Select(o => o.GetProperty("key").GetString()).ToList();
        Assert.True(keys.IndexOf(active) < keys.IndexOf(later));
    }

    [Fact]
    public async Task Options_NeedSignIn_AndAWorkflowEditorRole()
    {
        using var anonymous = fixture.Factory.CreateClientAs(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/database-connection-options")).StatusCode);

        foreach (var user in new[] { "dev.viewer", "dev.template-author", "dev.auditor", "dev.operator" })
        {
            using var other = fixture.Factory.CreateClientAs(user);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/v1/database-connection-options")).StatusCode);
        }

        foreach (var user in new[] { "dev.workflow-author", "dev.admin" })
        {
            using var editor = fixture.Factory.CreateClientAs(user);
            Assert.Equal(HttpStatusCode.OK, (await editor.GetAsync("/api/v1/database-connection-options")).StatusCode);
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
