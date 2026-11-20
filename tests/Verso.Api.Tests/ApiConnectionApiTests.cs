using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Verso.Application;
using Verso.Application.Connections;
using Verso.Application.Mail;
using Verso.Infrastructure.Connections;
using Verso.Infrastructure.Security;

namespace Verso.Api.Tests;

/// <summary>Stands in for the HTTP call: remembers the settings it was asked to use and answers as told.</summary>
internal sealed class RecordingApiProbe : IApiProbe
{
    public List<ApiConnectionSettings> Seen { get; } = [];
    public ApiProbeResult Result { get; set; } = ApiProbeResult.Success(200, 42, "application/json", 123);

    public Task<ApiProbeResult> ProbeAsync(ApiConnectionSettings settings, CancellationToken ct)
    {
        Seen.Add(settings);
        return Task.FromResult(Result);
    }
}

/// <summary>API connections managed in the app (ADR 0017): administrators only, write-only encrypted secret, audited without values, tested by the Worker with one GET.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ApiConnectionApiTests(MigratedDatabaseFixture fixture) : IDisposable
{
    private const string Secret = "api-s3cret-for-tests";
    private const string Host = "api.private-host.example";
    private readonly HttpClient _client = fixture.Factory.CreateClient();

    public void Dispose() => _client.Dispose();

    private static string UniqueKey() => $"api-{Guid.NewGuid():N}"[..20];

    private static object Body(string key, string authType = "Bearer", string? secret = Secret, string baseUrl = $"https://{Host}/v1", string testPath = "/status?probe=1", string authName = "") =>
        new { key, name = "API " + key, baseUrl, testPath, authType, authName, timeoutSeconds = 15, secret };

    private async Task<JsonElement> CreateAsync(string key, string authType = "Bearer", string? secret = Secret, string authName = "")
    {
        var response = await _client.PostAsJsonAsync("/api/v1/api-connections", Body(key, authType, secret, authName: authName));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private ApiConnectionTestRunner Runner(IServiceScope scope, IApiProbe probe, ApiConnectionOptions? options = null, ISecretProtector? protector = null, TimeProvider? clock = null) =>
        new(scope.ServiceProvider.GetRequiredService<IVersoDbContext>(), protector ?? fixture.Factory.Services.GetRequiredService<ISecretProtector>(), probe, options ?? new ApiConnectionOptions(), clock ?? TimeProvider.System);

    private async Task<Guid> RequestTestAsync(Guid connectionId)
    {
        var response = await _client.PostAsync($"/api/v1/api-connections/{connectionId}/tests", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> GetTestAsync(Guid testId) =>
        await (await _client.GetAsync($"/api/v1/api-connections/tests/{testId}")).Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Options_ListActiveConnectionsOnly_ByName_WithNothingButKeyNameAndHost()
    {
        var active = "aaa-" + UniqueKey();
        var inactive = UniqueKey();
        var later = "zzz-" + UniqueKey();
        await CreateAsync(later);
        await CreateAsync(active);
        var inactiveId = (await CreateAsync(inactive)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"/api/v1/api-connections/{inactiveId}/deactivate", null)).StatusCode);

        using var author = fixture.Factory.CreateClientAs("dev.workflow-author");
        var response = await author.GetAsync("/api/v1/api-connection-options");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var options = JsonSerializer.Deserialize<JsonElement>(text).EnumerateArray().ToList();
        Assert.Contains(options, o => o.GetProperty("key").GetString() == active);
        Assert.DoesNotContain(options, o => o.GetProperty("key").GetString() == inactive);
        var option = options.Single(o => o.GetProperty("key").GetString() == active);
        Assert.Equal(["host", "key", "name"], option.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(Host, option.GetProperty("host").GetString());
        Assert.Equal("API " + active, option.GetProperty("name").GetString());
        foreach (var leak in new[] { Secret, "https://", "/status", "/v1", "Bearer", "baseUrl", "testPath", "authType", "token", "secret" })
        {
            Assert.DoesNotContain(leak, text, StringComparison.OrdinalIgnoreCase);
        }

        var keys = options.Select(o => o.GetProperty("key").GetString()).ToList();
        Assert.True(keys.IndexOf(active) < keys.IndexOf(later));
    }

    [Fact]
    public async Task Options_ShowTheHostInLowerCase()
    {
        var key = UniqueKey();
        var response = await _client.PostAsJsonAsync("/api/v1/api-connections", Body(key, "None", null, baseUrl: "https://API.Mixed-Case.Example/v1"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var author = fixture.Factory.CreateClientAs("dev.workflow-author");
        var options = (await author.GetFromJsonAsync<JsonElement>("/api/v1/api-connection-options")).EnumerateArray().ToList();

        Assert.Equal("api.mixed-case.example", options.Single(o => o.GetProperty("key").GetString() == key).GetProperty("host").GetString());
    }

    [Fact]
    public async Task Options_NeedSignIn_AndAWorkflowEditorRole_WhileTheConnectionsStayAdministratorsOnly()
    {
        using var anonymous = fixture.Factory.CreateClientAs(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/api-connection-options")).StatusCode);

        foreach (var user in new[] { "dev.viewer", "dev.template-author", "dev.auditor", "dev.operator" })
        {
            using var other = fixture.Factory.CreateClientAs(user);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/v1/api-connection-options")).StatusCode);
        }

        foreach (var user in new[] { "dev.workflow-author", "dev.admin" })
        {
            using var editor = fixture.Factory.CreateClientAs(user);
            Assert.Equal(HttpStatusCode.OK, (await editor.GetAsync("/api/v1/api-connection-options")).StatusCode);
        }

        using var author = fixture.Factory.CreateClientAs("dev.workflow-author");
        Assert.Equal(HttpStatusCode.Forbidden, (await author.GetAsync("/api/v1/api-connections")).StatusCode);
    }

    [Fact]
    public async Task TheFrankfurterExample_NeedsNoKey_AndIsAccepted()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/api-connections", new
        {
            key = UniqueKey(), name = "Frankfurter", baseUrl = "https://api.frankfurter.dev/v1", testPath = "/latest?base=EUR&symbols=USD,GBP", authType = "None",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("None", dto.GetProperty("authType").GetString());
        Assert.False(dto.GetProperty("hasSecret").GetBoolean());
    }

    [Fact]
    public async Task Create_StoresTheConnection_AndNeverReturnsTheSecret()
    {
        var key = UniqueKey();

        var created = await CreateAsync(key);

        Assert.True(created.GetProperty("hasSecret").GetBoolean());
        Assert.DoesNotContain(Secret, created.GetRawText(), StringComparison.Ordinal);
        var list = await _client.GetStringAsync("/api/v1/api-connections?includeInactive=true");
        Assert.Contains(key, list, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSecret_IsEncryptedInTheDatabase_AndOnlyTheWorkerSideRunnerReadsItBack()
    {
        var id = (await CreateAsync(UniqueKey(), "ApiKeyHeader", authName: "X-API-Key")).GetProperty("id").GetGuid();
        var testId = await RequestTestAsync(id);
        var probe = new RecordingApiProbe();

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await Runner(scope, probe).RunAsync(testId, CancellationToken.None);
        }

        var seen = Assert.Single(probe.Seen);
        Assert.Equal(Secret, seen.Secret);
        Assert.Equal("ApiKeyHeader", seen.AuthType);
        Assert.Equal("X-API-Key", seen.AuthName);
        Assert.Equal($"https://{Host}/v1", seen.BaseUrl);
        using var check = fixture.Factory.Services.CreateScope();
        var stored = check.ServiceProvider.GetRequiredService<IVersoDbContext>().ApiConnections.Single(c => c.Id == id).SecretProtected;
        Assert.NotNull(stored);
        Assert.DoesNotContain(Secret, stored, StringComparison.Ordinal);
        Assert.StartsWith("v1.", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_WithoutASecret_KeepsIt_WithAValue_ReplacesIt_ClearRemovesIt_AndChoosingNoneDropsIt()
    {
        var key = UniqueKey();
        var id = (await CreateAsync(key)).GetProperty("id").GetGuid();

        var keep = await _client.PutAsJsonAsync($"/api/v1/api-connections/{id}", Body(key, secret: null, baseUrl: "https://api2.private-host.example/v1"));
        Assert.True((await keep.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hasSecret").GetBoolean());

        var replaced = await _client.PutAsJsonAsync($"/api/v1/api-connections/{id}", Body(key, secret: "another-api-secret"));
        Assert.True((await replaced.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hasSecret").GetBoolean());

        var cleared = await _client.PutAsJsonAsync($"/api/v1/api-connections/{id}", new { name = "API", baseUrl = $"https://{Host}/v1", testPath = "/status", authType = "Bearer", clearSecret = true });
        Assert.False((await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hasSecret").GetBoolean());

        await _client.PutAsJsonAsync($"/api/v1/api-connections/{id}", Body(key, secret: "third-api-secret"));
        var none = await _client.PutAsJsonAsync($"/api/v1/api-connections/{id}", Body(key, authType: "None", secret: null));
        Assert.False((await none.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hasSecret").GetBoolean()); // no authentication, so no secret is kept
    }

    [Fact]
    public async Task EveryChange_IsAudited_WithoutAnyValue()
    {
        var key = UniqueKey();
        var id = (await CreateAsync(key)).GetProperty("id").GetGuid();
        await _client.PutAsJsonAsync($"/api/v1/api-connections/{id}", Body(key, secret: "second-api-secret", baseUrl: "https://api3.private-host.example/v1"));
        await _client.PostAsync($"/api/v1/api-connections/{id}/deactivate", null);

        var audit = await _client.GetStringAsync($"/api/v1/audit?entityType=ApiConnection&entityId={id}");

        foreach (var action in new[] { "ApiConnectionCreated", "ApiConnectionUpdated", "ApiConnectionSecretChanged", "ApiConnectionDeactivated" })
        {
            Assert.Contains(action, audit, StringComparison.Ordinal);
        }

        foreach (var value in new[] { Secret, "second-api-secret", "api3.private-host.example", Host })
        {
            Assert.DoesNotContain(value, audit, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("http://api.example.test/v1", "/status", "None", "", "baseUrl")]                      // https only
    [InlineData("https://api.example.test/v1?x=1", "/status", "None", "", "baseUrl")]                // the query belongs to the test path
    [InlineData("https://user:pw@api.example.test", "/status", "None", "", "baseUrl")]               // no credentials in the address
    [InlineData("ftp://api.example.test", "/status", "None", "", "baseUrl")]
    [InlineData("https://api.example.test", "status", "None", "", "testPath")]                       // must start with /
    [InlineData("https://api.example.test", "//evil.example/x", "None", "", "testPath")]             // can't become another host
    [InlineData("https://api.example.test", "/a b", "None", "", "testPath")]
    [InlineData("https://api.example.test", "/status", "Nope", "", "authType")]
    [InlineData("https://api.example.test", "/status", "ApiKeyHeader", "Bad Header", "authName")]
    [InlineData("https://api.example.test", "/status", "ApiKeyHeader", "", "authName")]
    [InlineData("https://api.example.test", "/status", "ApiKeyQuery", "", "authName")]
    [InlineData("https://api.example.test", "/status", "Basic", "us:er", "authName")]                // a colon would break "user:password"
    [InlineData("https://api.example.test", "/status", "OAuthClientCredentials", "client", "tokenUrl")]
    public async Task Validation_RefusesAddressesAndNamesThatAreUnsafeOrMalformed(string baseUrl, string testPath, string authType, string authName, string field)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/api-connections", new { key = UniqueKey(), name = "Bad", baseUrl, testPath, authType, authName });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OAuth_NeedsAnHttpsTokenUrl_AndNoneCannotHaveASecret()
    {
        var http = await _client.PostAsJsonAsync("/api/v1/api-connections", new { key = UniqueKey(), name = "O", baseUrl = "https://api.example.test", testPath = "/s", authType = "OAuthClientCredentials", authName = "client", tokenUrl = "http://auth.example.test/token", secret = Secret });
        Assert.Equal(HttpStatusCode.BadRequest, http.StatusCode);
        Assert.Contains("\"tokenUrl\"", await http.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var ok = await _client.PostAsJsonAsync("/api/v1/api-connections", new { key = UniqueKey(), name = "O2", baseUrl = "https://api.example.test", testPath = "/s", authType = "OAuthClientCredentials", authName = "client", tokenUrl = "https://auth.example.test/token", scope = "read", secret = Secret });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        var noneWithSecret = await _client.PostAsJsonAsync("/api/v1/api-connections", Body(UniqueKey(), "None", Secret));
        Assert.Equal(HttpStatusCode.BadRequest, noneWithSecret.StatusCode);
        Assert.Contains("\"secret\"", await noneWithSecret.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADuplicateKey_Conflicts_AndOnlyAdministratorsMayUseTheEndpoints()
    {
        var key = UniqueKey();
        await CreateAsync(key);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync("/api/v1/api-connections", Body(key))).StatusCode);

        foreach (var user in new[] { "dev.viewer", "dev.template-author", "dev.workflow-author", "dev.auditor", "dev.operator" })
        {
            using var other = fixture.Factory.CreateClientAs(user);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/v1/api-connections")).StatusCode);
        }
    }

    [Fact]
    public async Task WithoutAMasterKey_ASecretIsRefused_ButAConnectionWithoutOneIsFine()
    {
        using var noKey = fixture.Factory.WithWebHostBuilder(b => b.UseSetting("Secrets:MasterKey", ""));
        using var client = noKey.CreateClientAs("dev.admin");

        var refused = await client.PostAsJsonAsync("/api/v1/api-connections", Body(UniqueKey()));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Secrets:MasterKey", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/api-connections", Body(UniqueKey(), "None", null))).StatusCode);
    }

    [Fact]
    public async Task Test_IsQueued_RunsOnTheWorker_AndTheResultHoldsOnlyStatusTimeTypeAndSize()
    {
        var id = (await CreateAsync(UniqueKey())).GetProperty("id").GetGuid();
        var testId = await RequestTestAsync(id);
        Assert.Equal("Queued", (await GetTestAsync(testId)).GetProperty("status").GetString());
        var probe = new RecordingApiProbe();

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await Runner(scope, probe).RunAsync(testId, CancellationToken.None);
            await Runner(scope, probe).RunAsync(testId, CancellationToken.None); // a duplicate delivery does nothing
        }

        Assert.Single(probe.Seen);
        var result = await GetTestAsync(testId);
        Assert.Equal("Succeeded", result.GetProperty("status").GetString());
        Assert.Equal(200, result.GetProperty("httpStatus").GetInt32());
        Assert.Equal(42, result.GetProperty("elapsedMs").GetInt32());
        Assert.Equal("application/json", result.GetProperty("contentType").GetString());
        Assert.Equal(123, result.GetProperty("responseBytes").GetInt64());
        foreach (var value in new[] { Secret, Host, "probe=1" })
        {
            Assert.DoesNotContain(value, result.GetRawText(), StringComparison.Ordinal);
        }

        var audit = await _client.GetStringAsync($"/api/v1/audit?entityType=ApiConnectionTest&entityId={testId}");
        Assert.Contains("ApiConnectionTestRequested", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedCall_GivesAFailedTest_WithTheCodeAndTheHttpStatus()
    {
        var testId = await RequestTestAsync((await CreateAsync(UniqueKey())).GetProperty("id").GetGuid());
        var probe = new RecordingApiProbe { Result = ApiProbeResult.Failure("api.unauthorized", 401, 30, "application/json", 20) };

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await Runner(scope, probe).RunAsync(testId, CancellationToken.None);
        }

        var result = await GetTestAsync(testId);
        Assert.Equal("Failed", result.GetProperty("status").GetString());
        Assert.Equal("api.unauthorized", result.GetProperty("code").GetString());
        Assert.Equal(401, result.GetProperty("httpStatus").GetInt32());
    }

    [Fact]
    public async Task AHostNotOnTheAllowedList_IsNeverCalled_AndTheTokenHostMustBeAllowedToo()
    {
        var testId = await RequestTestAsync((await CreateAsync(UniqueKey())).GetProperty("id").GetGuid());
        var probe = new RecordingApiProbe();
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await Runner(scope, probe, new ApiConnectionOptions { AllowedHosts = ["only-this-host.example"] }).RunAsync(testId, CancellationToken.None);
        }

        Assert.Empty(probe.Seen);
        Assert.Equal("api.host_not_allowed", (await GetTestAsync(testId)).GetProperty("code").GetString());

        var oauth = await _client.PostAsJsonAsync("/api/v1/api-connections", new { key = UniqueKey(), name = "O", baseUrl = $"https://{Host}", testPath = "/s", authType = "OAuthClientCredentials", authName = "client", tokenUrl = "https://auth.other-host.example/token", secret = Secret });
        var oauthTest = await RequestTestAsync((await oauth.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await Runner(scope, probe, new ApiConnectionOptions { AllowedHosts = [Host.ToUpperInvariant()] }).RunAsync(oauthTest, CancellationToken.None); // the API host is allowed (any case), the token host is not
        }

        Assert.Empty(probe.Seen);
        Assert.Equal("api.host_not_allowed", (await GetTestAsync(oauthTest)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AnAllowedHost_IsMatchedWithoutRegardToCase()
    {
        var testId = await RequestTestAsync((await CreateAsync(UniqueKey())).GetProperty("id").GetGuid());
        var probe = new RecordingApiProbe();

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await Runner(scope, probe, new ApiConnectionOptions { AllowedHosts = [Host.ToUpperInvariant()] }).RunAsync(testId, CancellationToken.None);
        }

        Assert.Single(probe.Seen);
    }

    [Fact]
    public async Task ASecretThatCannotBeRead_FailsTheTest_WithoutCalling()
    {
        var testId = await RequestTestAsync((await CreateAsync(UniqueKey())).GetProperty("id").GetGuid());
        var probe = new RecordingApiProbe();

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await Runner(scope, probe, protector: new AesGcmSecretProtector(AesGcmSecretProtector.GenerateKey())).RunAsync(testId, CancellationToken.None);
        }

        Assert.Empty(probe.Seen);
        Assert.Equal("api.secret_unreadable", (await GetTestAsync(testId)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ASwitchedOffConnection_Is409_AnUnknownOne404_AndOldTestsAreCleanedUp()
    {
        var id = (await CreateAsync(UniqueKey())).GetProperty("id").GetGuid();
        var testId = await RequestTestAsync(id);
        await _client.PostAsync($"/api/v1/api-connections/{id}/deactivate", null);
        var inactive = await _client.PostAsync($"/api/v1/api-connections/{id}/tests", null);
        Assert.Equal(HttpStatusCode.Conflict, inactive.StatusCode);
        Assert.Equal("api_connection.inactive", (await inactive.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/v1/api-connections/{Guid.NewGuid()}/tests", null)).StatusCode);

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            Assert.True(await Runner(scope, new RecordingApiProbe(), clock: new Shifted(TimeSpan.FromDays(2))).CleanupAsync(CancellationToken.None) >= 1);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/api-connections/tests/{testId}")).StatusCode);
    }

    private sealed class Shifted(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
    }
}

/// <summary>The HTTP probe with a fake handler (no network): what it sends, how it maps answers, and that the body is never kept.</summary>
public sealed class HttpApiProbeTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return await respond(request);
        }
    }

    private static HttpResponseMessage Ok(string body = "{\"rates\":{\"USD\":1.08}}", string type = "application/json") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };

    private static ApiConnectionSettings Settings(string authType = "None", string authName = "", string secret = "", string path = "/latest?base=EUR&symbols=USD,GBP", string tokenUrl = "", string scope = "") =>
        new("https://api.frankfurter.dev/v1", path, authType, authName, secret, tokenUrl, scope, TimeSpan.FromSeconds(10));

    private static async Task<(ApiProbeResult Result, FakeHandler Handler)> RunAsync(ApiConnectionSettings settings, Func<HttpRequestMessage, Task<HttpResponseMessage>>? respond = null)
    {
        var handler = new FakeHandler(respond ?? (_ => Task.FromResult(Ok())));
        return (await new HttpApiProbe(() => handler).ProbeAsync(settings, CancellationToken.None), handler);
    }

    [Fact]
    public async Task TheFrankfurterExample_IsASingleGetWithoutCredentials_AndSucceeds()
    {
        var (result, handler) = await RunAsync(Settings());

        Assert.True(result.Ok);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.frankfurter.dev/v1/latest?base=EUR&symbols=USD,GBP", request.RequestUri!.ToString());
        Assert.Null(request.Headers.Authorization);
        Assert.Equal(200, result.HttpStatus);
        Assert.Equal("application/json", result.ContentType);
        Assert.Equal("{\"rates\":{\"USD\":1.08}}".Length, result.ResponseBytes);
    }

    [Fact]
    public async Task TheBody_IsCountedButNeverKept()
    {
        var (result, _) = await RunAsync(Settings(), _ => Task.FromResult(Ok("customer-ssn-123-45-6789")));

        Assert.True(result.Ok);
        Assert.DoesNotContain("123-45", System.Text.Json.JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Equal(24, result.ResponseBytes);
    }

    [Fact]
    public async Task AnApiKeyInAHeader_IsSentUnderTheNamedHeader()
    {
        var (_, handler) = await RunAsync(Settings("ApiKeyHeader", "X-API-Key", "key-123"));

        Assert.Equal("key-123", Assert.Single(handler.Requests[0].Headers.GetValues("X-API-Key")));
    }

    [Fact]
    public async Task AnApiKeyInTheQuery_IsAppendedAndUrlEncoded_EvenWhenThePathHasNoQuery()
    {
        var (_, withQuery) = await RunAsync(Settings("ApiKeyQuery", "api_key", "k e&y=1"));
        Assert.EndsWith("&api_key=k%20e%26y%3D1", withQuery.Requests[0].RequestUri!.AbsoluteUri, StringComparison.Ordinal); // AbsoluteUri is what goes on the wire

        var (_, noQuery) = await RunAsync(Settings("ApiKeyQuery", "key", "abc", path: "/status"));
        Assert.Equal("https://api.frankfurter.dev/v1/status?key=abc", noQuery.Requests[0].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task BearerAndBasic_SetTheAuthorizationHeader()
    {
        var (_, bearer) = await RunAsync(Settings("Bearer", secret: "tok-1"));
        Assert.Equal("Bearer tok-1", bearer.Requests[0].Headers.Authorization!.ToString());

        var (_, basic) = await RunAsync(Settings("Basic", "alice", "pa:ss"));
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:pa:ss")), basic.Requests[0].Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task OAuthClientCredentials_AsksTheTokenEndpointFirst_ThenCallsTheApiWithTheBearerToken()
    {
        var (result, handler) = await RunAsync(Settings("OAuthClientCredentials", "client-1", "client-secret", tokenUrl: "https://auth.example.test/token", scope: "read write"), request =>
            Task.FromResult(request.Method == HttpMethod.Post
                ? Ok("{\"access_token\":\"the-token\",\"token_type\":\"Bearer\"}")
                : Ok()));

        Assert.True(result.Ok);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://auth.example.test/token", handler.Requests[0].RequestUri!.ToString());
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("client-1:client-secret")), handler.Requests[0].Headers.Authorization!.ToString());
        Assert.Contains("grant_type=client_credentials", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Contains("scope=read+write", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Equal("Bearer the-token", handler.Requests[1].Headers.Authorization!.ToString());
        Assert.DoesNotContain("the-token", System.Text.Json.JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(500)]
    public async Task OAuth_ATokenEndpointThatRefuses_FailsWithTokenFailed_AndTheApiIsNeverCalled(int status)
    {
        var (result, handler) = await RunAsync(Settings("OAuthClientCredentials", "c", "s", tokenUrl: "https://auth.example.test/token"), _ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));

        Assert.Equal("api.token_failed", result.Code);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("{\"token_type\":\"Bearer\"}")]
    [InlineData("not json")]
    [InlineData("{\"access_token\":\"\"}")]
    public async Task OAuth_AnAnswerWithoutAnAccessToken_IsATokenFailure(string body)
    {
        var (result, _) = await RunAsync(Settings("OAuthClientCredentials", "c", "s", tokenUrl: "https://auth.example.test/token"), _ => Task.FromResult(Ok(body)));

        Assert.Equal("api.token_failed", result.Code);
    }

    [Theory]
    [InlineData(401, "api.unauthorized")]
    [InlineData(403, "api.unauthorized")]
    [InlineData(404, "api.not_found")]
    [InlineData(429, "api.rate_limited")]
    [InlineData(302, "api.redirect_not_followed")]
    [InlineData(400, "api.client_error")]
    [InlineData(500, "api.server_error")]
    [InlineData(503, "api.server_error")]
    public async Task HttpStatuses_AreMappedToOurCodes_AndKeepTheStatusNumber(int status, string code)
    {
        var (result, _) = await RunAsync(Settings(), _ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("secret-error-detail") }));

        Assert.False(result.Ok);
        Assert.Equal(code, result.Code);
        Assert.Equal(status, result.HttpStatus);
        Assert.Equal(ApiProbeMessages.For(code), result.Message);
        Assert.DoesNotContain("secret-error-detail", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NetworkProblems_AreMappedToOurCodes()
    {
        var (unreachable, _) = await RunAsync(Settings(), _ => throw new HttpRequestException("x", new SocketException()));
        var (certificate, _) = await RunAsync(Settings(), _ => throw new HttpRequestException("x", new AuthenticationException("bad certificate")));
        var (blocked, _) = await RunAsync(Settings(), _ => throw new HttpRequestException("x", new BlockedAddressException()));
        var (timeout, _) = await RunAsync(Settings(), _ => throw new TaskCanceledException("timed out"));

        Assert.Equal("api.unreachable", unreachable.Code);
        Assert.Equal("api.certificate_untrusted", certificate.Code);
        Assert.Equal("api.blocked_address", blocked.Code);
        Assert.Equal("api.timeout", timeout.Code);
    }

    [Fact]
    public async Task TheRealHandler_RefusesALoopbackAddress_WithoutConnecting()
    {
        var result = await new HttpApiProbe().ProbeAsync(new ApiConnectionSettings("https://localhost:9", "/x", "None", "", "", "", "", TimeSpan.FromSeconds(5)), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("api.blocked_address", result.Code);
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("10.1.2.3", true)]        // internal APIs are allowed (the administrator names them; set AllowedHosts in production)
    [InlineData("192.168.1.10", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("169.254.169.254", false)] // cloud metadata
    [InlineData("fe80::1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:169.254.169.254", false)]
    public void IpGuard_RefusesLoopbackLinkLocalAndMulticast(string address, bool allowed) => Assert.Equal(allowed, IpGuard.IsAllowed(IPAddress.Parse(address)));

    [Theory]
    [InlineData("https://api.frankfurter.dev/v1", true)]
    [InlineData("https://api.example.com", true)]
    [InlineData("http://api.example.com", false)]
    [InlineData("https://api.example.com?x=1", false)]
    [InlineData("https://api.example.com#frag", false)]
    [InlineData("https://user@api.example.com", false)]
    [InlineData("https://api.example.com/a b", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    public void BaseUrls_MustBeHttpsWithoutCredentialsQueryOrFragment(string url, bool valid) => Assert.Equal(valid, ApiUrls.IsValidBaseUrl(url));

    [Theory]
    [InlineData("/latest?base=EUR&symbols=USD,GBP", true)]
    [InlineData("/", true)]
    [InlineData("latest", false)]
    [InlineData("//evil.example", false)]
    [InlineData("/a b", false)]
    [InlineData("/a\\b", false)]
    [InlineData("", false)]
    public void TestPaths_StartWithASingleSlashAndHaveNoSpaces(string path, bool valid) => Assert.Equal(valid, ApiUrls.IsValidTestPath(path));
}
