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

/// <summary>Stands in for the SMS provider: remembers what it was asked to send and answers as told. Never sends anything.</summary>
internal sealed class RecordingSmsProbe : ISmsProbe
{
    public List<(SmsConnectionSettings Settings, string Recipient)> Seen { get; } = [];
    public SmsProbeResult Result { get; set; } = SmsProbeResult.Success(201, 88, "9001", 1, 41.5m);

    public Task<SmsProbeResult> SendTestAsync(SmsConnectionSettings settings, string recipientDigits, CancellationToken ct)
    {
        Seen.Add((settings, recipientDigits));
        return Task.FromResult(Result);
    }
}

/// <summary>
/// SMS connections managed in the app (ADR 0018): administrators only, write-only encrypted API key, audited without values, tested by the Worker
/// with ONE real SMS to a number typed in the test (here always a fake probe and a fake number: nothing is ever sent).
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class SmsConnectionApiTests(MigratedDatabaseFixture fixture) : IDisposable
{
    private const string ApiKey = "test-key-not-real-0123456789";
    private const string FakeNumber = "+357 99 000000";
    private const string FakeDigits = "35799000000";
    private const string Host = "sms.private-host.example";
    private readonly HttpClient _client = fixture.Factory.CreateClient();

    public void Dispose() => _client.Dispose();

    private static string UniqueKey() => $"sms-{Guid.NewGuid():N}"[..20];

    private static object Body(string key, string? secret = ApiKey, string baseUrl = $"https://{Host}/v3", string sender = "TestSender", string messageType = "transactional") =>
        new { key, name = "SMS " + key, provider = "brevo", baseUrl, sender, messageType, unicodeEnabled = true, organisationPrefix = "TestOrg", timeoutSeconds = 15, description = "for tests", secret };

    private async Task<JsonElement> CreateAsync(string key)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/sms-connections", Body(key));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private SmsConnectionTestRunner Runner(IServiceScope scope, ISmsProbe probe, SmsConnectionOptions? options = null, ISecretProtector? protector = null, TimeProvider? clock = null) =>
        new(scope.ServiceProvider.GetRequiredService<IVersoDbContext>(), protector ?? fixture.Factory.Services.GetRequiredService<ISecretProtector>(), probe, options ?? new SmsConnectionOptions(), clock ?? TimeProvider.System);

    /// <summary>A job queue that queues nothing: the tests run the Worker's runner themselves, and never depend on Hangfire's global state.</summary>
    private sealed class NoJobs : ISmsConnectionTestJobs
    {
        public void Enqueue(Guid testId) { }
    }

    private sealed class Admin : ICurrentUser
    {
        public string Id => "admin";
        public string Name => "Admin";
        public IReadOnlyCollection<string> Roles => [];
    }

    /// <summary>Requests a test through the API-side service (the same one the endpoint calls) with a no-op queue.</summary>
    private async Task<Guid> RequestTestAsync(Guid connectionId, string recipient = FakeNumber)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var service = new SmsConnectionTests(scope.ServiceProvider.GetRequiredService<IVersoDbContext>(), new Admin(), TimeProvider.System, fixture.Factory.Services.GetRequiredService<ISecretProtector>(), new NoJobs());
        var result = await service.RequestAsync(connectionId, new RequestSmsConnectionTest(recipient), CancellationToken.None);
        Assert.True(result.IsSuccess);
        return result.Value!.Id;
    }

    private async Task<JsonElement> GetTestAsync(Guid testId) =>
        await (await _client.GetAsync($"/api/v1/sms-connections/tests/{testId}")).Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Create_StoresTheConnection_AndNeverReturnsTheApiKey()
    {
        var key = UniqueKey();

        var created = await CreateAsync(key);

        Assert.True(created.GetProperty("hasSecret").GetBoolean());
        Assert.Equal("brevo", created.GetProperty("provider").GetString());
        Assert.Equal("TestSender", created.GetProperty("sender").GetString());
        Assert.DoesNotContain(ApiKey, created.GetRawText(), StringComparison.Ordinal);
        var list = await _client.GetStringAsync("/api/v1/sms-connections?includeInactive=true");
        Assert.Contains(key, list, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBaseAddress_DefaultsToBrevo_WhenLeftOut()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/sms-connections", new { key = UniqueKey(), name = "Default", sender = "Verso", secret = ApiKey });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://api.brevo.com/v3", dto.GetProperty("baseUrl").GetString());
        Assert.Equal("transactional", dto.GetProperty("messageType").GetString());
        Assert.True(dto.GetProperty("unicodeEnabled").GetBoolean());
    }

    [Fact]
    public async Task TheApiKey_IsEncryptedInTheDatabase_AndOnlyTheWorkerSideRunnerReadsItBack()
    {
        var id = (await CreateAsync(UniqueKey())).GetProperty("id").GetGuid();
        var testId = await RequestTestAsync(id);
        var probe = new RecordingSmsProbe();

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await Runner(scope, probe).RunAsync(testId, CancellationToken.None);
        }

        var (seen, recipient) = Assert.Single(probe.Seen);
        Assert.Equal(ApiKey, seen.ApiKey);
        Assert.Equal(FakeDigits, recipient);
        Assert.Equal("TestSender", seen.Sender);
        Assert.Equal("transactional", seen.MessageType);
        Assert.Equal("TestOrg", seen.OrganisationPrefix);
        Assert.Equal($"https://{Host}/v3", seen.BaseUrl);
        using var check = fixture.Factory.Services.CreateScope();
        var stored = check.ServiceProvider.GetRequiredService<IVersoDbContext>().SmsConnections.Single(c => c.Id == id).SecretProtected;
        Assert.NotNull(stored);
        Assert.DoesNotContain(ApiKey, stored, StringComparison.Ordinal);
        Assert.StartsWith("v1.", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_WithoutAKey_KeepsIt_AndWithAValue_ReplacesIt()
    {
        var key = UniqueKey();
        var id = (await CreateAsync(key)).GetProperty("id").GetGuid();

        var keep = await _client.PutAsJsonAsync($"/api/v1/sms-connections/{id}", Body(key, secret: null, sender: "NewSender"));
        var kept = await keep.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(kept.GetProperty("hasSecret").GetBoolean());
        Assert.Equal("NewSender", kept.GetProperty("sender").GetString());

        var replaced = await _client.PutAsJsonAsync($"/api/v1/sms-connections/{id}", Body(key, secret: "another-test-key-not-real"));
        Assert.True((await replaced.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hasSecret").GetBoolean());
        var testId = await RequestTestAsync(id);
        var probe = new RecordingSmsProbe();
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await Runner(scope, probe).RunAsync(testId, CancellationToken.None);
        }

        Assert.Equal("another-test-key-not-real", Assert.Single(probe.Seen).Settings.ApiKey);
    }

    [Fact]
    public async Task EveryChange_IsAudited_WithoutAnyValue()
    {
        var key = UniqueKey();
        var id = (await CreateAsync(key)).GetProperty("id").GetGuid();
        await _client.PutAsJsonAsync($"/api/v1/sms-connections/{id}", Body(key, secret: "second-test-key-not-real", baseUrl: "https://sms3.private-host.example/v3", sender: "OtherName"));
        await _client.PostAsync($"/api/v1/sms-connections/{id}/deactivate", null);

        var audit = await _client.GetStringAsync($"/api/v1/audit?entityType=SmsConnection&entityId={id}");

        foreach (var action in new[] { "SmsConnectionCreated", "SmsConnectionUpdated", "SmsConnectionSecretChanged", "SmsConnectionDeactivated" })
        {
            Assert.Contains(action, audit, StringComparison.Ordinal);
        }

        foreach (var value in new[] { ApiKey, "second-test-key-not-real", "sms3.private-host.example", Host, "OtherName", "TestSender", "TestOrg" })
        {
            Assert.DoesNotContain(value, audit, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("http://api.example.test/v3", "Sender", "transactional", "baseUrl")]            // https only
    [InlineData("https://api.example.test/v3?x=1", "Sender", "transactional", "baseUrl")]       // no query on the base
    [InlineData("https://user:pw@api.example.test", "Sender", "transactional", "baseUrl")]      // no credentials in the address
    [InlineData("https://api.example.test", "", "transactional", "sender")]
    [InlineData("https://api.example.test", "Has Space", "transactional", "sender")]
    [InlineData("https://api.example.test", "TooLongSender1", "transactional", "sender")]        // more than 11 letters and digits
    [InlineData("https://api.example.test", "1234567890123456", "transactional", "sender")]      // more than 15 digits
    [InlineData("https://api.example.test", "Sender", "promo", "messageType")]
    public async Task Validation_RefusesUnsafeOrMalformedFields(string baseUrl, string sender, string messageType, string field)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/sms-connections", new { key = UniqueKey(), name = "Bad", baseUrl, sender, messageType, secret = ApiKey });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASenderThatIsANumber_AndAnElevenLetterName_AreAccepted()
    {
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync("/api/v1/sms-connections", Body(UniqueKey(), sender: "35799000000"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync("/api/v1/sms-connections", Body(UniqueKey(), sender: "ABCDEFGHIJK"))).StatusCode);
    }

    [Fact]
    public async Task TheApiKey_IsRequiredOnCreate_AndANotBrevoProviderIsRefused()
    {
        var noKey = await _client.PostAsJsonAsync("/api/v1/sms-connections", Body(UniqueKey(), secret: null));
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Contains("\"secret\"", await noKey.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var other = await _client.PostAsJsonAsync("/api/v1/sms-connections", new { key = UniqueKey(), name = "X", provider = "twilio", sender = "Verso", secret = ApiKey });
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
        Assert.Contains("\"provider\"", await other.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADuplicateKey_Conflicts_AndOnlyAdministratorsMayUseTheEndpoints()
    {
        var key = UniqueKey();
        await CreateAsync(key);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync("/api/v1/sms-connections", Body(key))).StatusCode);

        foreach (var user in new[] { "dev.viewer", "dev.template-author", "dev.workflow-author", "dev.auditor", "dev.operator" })
        {
            using var other = fixture.Factory.CreateClientAs(user);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/v1/sms-connections")).StatusCode);
        }
    }

    private sealed class NoMasterKey : ISecretProtector
    {
        public bool IsConfigured => false;
        public string Protect(string secret, string context) => throw new InvalidOperationException("no key");
        public string? Unprotect(string protectedValue, string context) => null;
    }

    [Fact]
    public async Task WithoutAMasterKey_AnApiKeyIsRefused_BeforeAnythingIsStored()
    {
        // A service with no database behind it: the refusal happens in validation, so nothing is ever read or written.
        var service = new SmsConnectionService(null!, new Admin(), TimeProvider.System, new NoMasterKey());

        var result = await service.CreateAsync(new SaveSmsConnectionRequest(UniqueKey(), "No key", null, null, "Verso", null, null, null, null, null, ApiKey), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("Secrets:MasterKey", string.Join(' ', result.Error!.Fields!["secret"]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_IsQueued_RunsOnTheWorker_AndTheResultNeverHoldsTheNumberOrTheKey()
    {
        var id = (await CreateAsync(UniqueKey())).GetProperty("id").GetGuid();
        var testId = await RequestTestAsync(id);
        Assert.Equal("Queued", (await GetTestAsync(testId)).GetProperty("status").GetString());
        var probe = new RecordingSmsProbe();

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await Runner(scope, probe).RunAsync(testId, CancellationToken.None);
            await Runner(scope, probe).RunAsync(testId, CancellationToken.None); // a duplicate delivery must never send a second SMS
        }

        Assert.Single(probe.Seen);
        var result = await GetTestAsync(testId);
        Assert.Equal("Succeeded", result.GetProperty("status").GetString());
        Assert.Equal(201, result.GetProperty("httpStatus").GetInt32());
        Assert.Equal(88, result.GetProperty("elapsedMs").GetInt32());
        Assert.Equal("9001", result.GetProperty("messageId").GetString());
        Assert.Equal(1, result.GetProperty("smsCount").GetInt32());
        Assert.Equal(41.5m, result.GetProperty("remainingCredits").GetDecimal());
        foreach (var value in new[] { ApiKey, Host, FakeDigits, "99 000000", "Verso test message" })
        {
            Assert.DoesNotContain(value, result.GetRawText(), StringComparison.Ordinal);
        }

        var audit = await _client.GetStringAsync($"/api/v1/audit?entityType=SmsConnectionTest&entityId={testId}");
        Assert.Contains("SmsConnectionTestRequested", audit, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeDigits, audit, StringComparison.Ordinal);
        Assert.DoesNotContain("99 000000", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTestNumber_IsEncryptedWhileWaiting_AndRemovedOnceTheTestHasRun()
    {
        var id = (await CreateAsync(UniqueKey())).GetProperty("id").GetGuid();
        var testId = await RequestTestAsync(id);

        using (var before = fixture.Factory.Services.CreateScope())
        {
            var stored = before.ServiceProvider.GetRequiredService<IVersoDbContext>().SmsConnectionTests.Single(t => t.Id == testId).RecipientProtected;
            Assert.NotNull(stored);
            Assert.DoesNotContain(FakeDigits, stored, StringComparison.Ordinal);
        }

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await Runner(scope, new RecordingSmsProbe()).RunAsync(testId, CancellationToken.None);
        }

        using var after = fixture.Factory.Services.CreateScope();
        Assert.Null(after.ServiceProvider.GetRequiredService<IVersoDbContext>().SmsConnectionTests.Single(t => t.Id == testId).RecipientProtected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12345")]
    [InlineData("+0099000000")]
    [InlineData("99 000 000 000 000 000")]
    [InlineData("+357-99-0000; DROP")]
    public async Task ANumberThatIsNotInternationalFormat_IsRefused_AndNothingIsQueued(string recipient)
    {
        var id = (await CreateAsync(UniqueKey())).GetProperty("id").GetGuid();

        var response = await _client.PostAsJsonAsync($"/api/v1/sms-connections/{id}/tests", new { recipient });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"recipient\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecondTestWithinThirtySeconds_IsRefused()
    {
        var id = (await CreateAsync(UniqueKey())).GetProperty("id").GetGuid();
        await RequestTestAsync(id);

        var second = await _client.PostAsJsonAsync($"/api/v1/sms-connections/{id}/tests", new { recipient = FakeNumber });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("sms_connection.test_too_soon", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AFailedSend_GivesAFailedTest_WithTheCodeAndTheHttpStatus()
    {
        var testId = await RequestTestAsync((await CreateAsync(UniqueKey())).GetProperty("id").GetGuid());
        var probe = new RecordingSmsProbe { Result = SmsProbeResult.Failure("sms.unauthorized", 401, 30) };

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await Runner(scope, probe).RunAsync(testId, CancellationToken.None);
        }

        var result = await GetTestAsync(testId);
        Assert.Equal("Failed", result.GetProperty("status").GetString());
        Assert.Equal("sms.unauthorized", result.GetProperty("code").GetString());
        Assert.Equal(401, result.GetProperty("httpStatus").GetInt32());
    }

    [Fact]
    public async Task AHostNotOnTheAllowedList_IsNeverCalled_AndTheMatchIgnoresCase()
    {
        var blockedTest = await RequestTestAsync((await CreateAsync(UniqueKey())).GetProperty("id").GetGuid());
        var probe = new RecordingSmsProbe();
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await Runner(scope, probe, new SmsConnectionOptions { AllowedHosts = ["only-this-host.example"] }).RunAsync(blockedTest, CancellationToken.None);
        }

        Assert.Empty(probe.Seen);
        Assert.Equal("sms.host_not_allowed", (await GetTestAsync(blockedTest)).GetProperty("code").GetString());

        var allowedTest = await RequestTestAsync((await CreateAsync(UniqueKey())).GetProperty("id").GetGuid());
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await Runner(scope, probe, new SmsConnectionOptions { AllowedHosts = [Host.ToUpperInvariant()] }).RunAsync(allowedTest, CancellationToken.None);
        }

        Assert.Single(probe.Seen);
    }

    [Fact]
    public async Task AnApiKeyThatCannotBeRead_FailsTheTest_WithoutSending()
    {
        var testId = await RequestTestAsync((await CreateAsync(UniqueKey())).GetProperty("id").GetGuid());
        var probe = new RecordingSmsProbe();

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await Runner(scope, probe, protector: new AesGcmSecretProtector(AesGcmSecretProtector.GenerateKey())).RunAsync(testId, CancellationToken.None);
        }

        Assert.Empty(probe.Seen);
        Assert.Equal("sms.secret_unreadable", (await GetTestAsync(testId)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ASwitchedOffConnection_Is409_AnUnknownOne404_AndOldTestsAreCleanedUp()
    {
        var id = (await CreateAsync(UniqueKey())).GetProperty("id").GetGuid();
        var testId = await RequestTestAsync(id);
        await _client.PostAsync($"/api/v1/sms-connections/{id}/deactivate", null);
        var inactive = await _client.PostAsJsonAsync($"/api/v1/sms-connections/{id}/tests", new { recipient = FakeNumber });
        Assert.Equal(HttpStatusCode.Conflict, inactive.StatusCode);
        Assert.Equal("sms_connection.inactive", (await inactive.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync($"/api/v1/sms-connections/{Guid.NewGuid()}/tests", new { recipient = FakeNumber })).StatusCode);

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            Assert.True(await Runner(scope, new RecordingSmsProbe(), clock: new Shifted(TimeSpan.FromDays(2))).CleanupAsync(CancellationToken.None) >= 1);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/sms-connections/tests/{testId}")).StatusCode);
    }

    private sealed class Shifted(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
    }
}

/// <summary>The Brevo probe with a fake handler (no network, no SMS): what it sends, how it maps answers, and that nothing sensitive is kept.</summary>
public sealed class BrevoSmsProbeTests
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

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static SmsConnectionSettings Settings(string prefix = "", string type = "transactional", bool unicode = true) =>
        new("https://api.brevo.test/v3/", "TestSender", type, unicode, prefix, "test-key-not-real", TimeSpan.FromSeconds(10));

    private static async Task<(SmsProbeResult Result, FakeHandler Handler)> RunAsync(SmsConnectionSettings settings, Func<HttpRequestMessage, Task<HttpResponseMessage>>? respond = null)
    {
        var handler = new FakeHandler(respond ?? (_ => Task.FromResult(Json(HttpStatusCode.Created, "{\"reference\":\"r\",\"messageId\":21,\"smsCount\":2,\"usedCredits\":2,\"remainingCredits\":7.5}"))));
        return (await new BrevoSmsProbe(() => handler).SendTestAsync(settings, "35799000000", CancellationToken.None), handler);
    }

    [Fact]
    public async Task ItSendsOnePost_ToTheTransactionalSmsAddress_WithTheApiKeyHeader_AndTheDocumentedFields()
    {
        var (result, handler) = await RunAsync(Settings(prefix: "TestOrg", type: "marketing", unicode: false));

        Assert.True(result.Ok);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.brevo.test/v3/transactionalSMS/sms", request.RequestUri!.ToString());
        Assert.Equal("test-key-not-real", Assert.Single(request.Headers.GetValues("api-key")));
        Assert.Null(request.Headers.Authorization);
        using var body = JsonDocument.Parse(handler.Bodies[0]);
        var root = body.RootElement;
        Assert.Equal("TestSender", root.GetProperty("sender").GetString());
        Assert.Equal("35799000000", root.GetProperty("recipient").GetString());
        Assert.Equal("Verso test message", root.GetProperty("content").GetString());
        Assert.Equal("marketing", root.GetProperty("type").GetString());
        Assert.False(root.GetProperty("unicodeEnabled").GetBoolean());
        Assert.Equal("TestOrg", root.GetProperty("organisationPrefix").GetString());
    }

    [Fact]
    public async Task WithoutAPrefix_TheFieldIsLeftOut()
    {
        var (_, handler) = await RunAsync(Settings());

        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.False(body.RootElement.TryGetProperty("organisationPrefix", out _));
    }

    [Fact]
    public async Task ASuccess_KeepsOnlyStatusTimeMessageIdPartsAndCredits()
    {
        var (result, _) = await RunAsync(Settings());

        Assert.Equal(201, result.HttpStatus);
        Assert.Equal("21", result.MessageId);
        Assert.Equal(2, result.SmsCount);
        Assert.Equal(7.5m, result.RemainingCredits);
        var kept = JsonSerializer.Serialize(result);
        foreach (var value in new[] { "35799000000", "test-key-not-real", "Verso test message" })
        {
            Assert.DoesNotContain(value, kept, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ASuccessWithAnUnreadableBody_IsStillASuccess()
    {
        var (result, _) = await RunAsync(Settings(), _ => Task.FromResult(Json(HttpStatusCode.Created, "not json")));

        Assert.True(result.Ok);
        Assert.Null(result.MessageId);
    }

    [Theory]
    [InlineData(401, "{}", "sms.unauthorized")]
    [InlineData(403, "{}", "sms.unauthorized")]
    [InlineData(402, "{}", "sms.insufficient_credits")]
    [InlineData(429, "{}", "sms.rate_limited")]
    [InlineData(302, "{}", "sms.redirect_not_followed")]
    [InlineData(404, "{}", "sms.client_error")]
    [InlineData(500, "{}", "sms.server_error")]
    [InlineData(503, "{}", "sms.server_error")]
    [InlineData(400, "{\"code\":\"invalid_parameter\",\"message\":\"recipient is invalid\"}", "sms.invalid_recipient")]
    [InlineData(400, "{\"code\":\"invalid_parameter\",\"message\":\"Not enough credits\"}", "sms.insufficient_credits")]
    [InlineData(400, "{\"code\":\"invalid_parameter\",\"message\":\"sender is too long\"}", "sms.client_error")]
    public async Task HttpAnswers_AreMappedToOurCodes_AndTheProvidersTextIsNeverKept(int status, string body, string code)
    {
        var (result, _) = await RunAsync(Settings(), _ => Task.FromResult(Json((HttpStatusCode)status, body)));

        Assert.False(result.Ok);
        Assert.Equal(code, result.Code);
        Assert.Equal(status, result.HttpStatus);
        Assert.Equal(SmsProbeMessages.For(code), result.Message);
        Assert.DoesNotContain("invalid", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NetworkProblems_AreMappedToOurCodes()
    {
        var (unreachable, _) = await RunAsync(Settings(), _ => throw new HttpRequestException("x", new SocketException()));
        var (certificate, _) = await RunAsync(Settings(), _ => throw new HttpRequestException("x", new AuthenticationException("bad certificate")));
        var (blocked, _) = await RunAsync(Settings(), _ => throw new HttpRequestException("x", new BlockedAddressException()));
        var (timeout, _) = await RunAsync(Settings(), _ => throw new TaskCanceledException("timed out"));

        Assert.Equal("sms.unreachable", unreachable.Code);
        Assert.Equal("sms.certificate_untrusted", certificate.Code);
        Assert.Equal("sms.blocked_address", blocked.Code);
        Assert.Equal("sms.timeout", timeout.Code);
    }

    [Fact]
    public async Task TheRealHandler_RefusesALoopbackAddress_WithoutConnecting()
    {
        var settings = new SmsConnectionSettings("https://localhost:9/v3", "TestSender", "transactional", true, "", "test-key-not-real", TimeSpan.FromSeconds(5));

        var result = await new BrevoSmsProbe().SendTestAsync(settings, "35799000000", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("sms.blocked_address", result.Code);
    }

    [Theory]
    [InlineData("+357 99 000000", "35799000000")]
    [InlineData("35799000000", "35799000000")]
    [InlineData("0035799000000", "35799000000")]
    [InlineData("+357-99-000-000", "35799000000")]
    [InlineData("+(357) 99.000.000", "35799000000")]
    [InlineData(" +44 7700 900123 ", "447700900123")]
    [InlineData("99000000", "99000000")] // 8 digits is the shortest accepted
    public void Recipients_AreNormalisedToDigitsWithoutPlus(string input, string digits)
    {
        Assert.True(SmsRules.TryNormaliseRecipient(input, out var result));
        Assert.Equal(digits, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1234567")]             // too short
    [InlineData("+0357 99 000000")]     // a country code never starts with 0
    [InlineData("+357 99 00000a")]
    [InlineData("++35799000000")]
    [InlineData("+3579900000012345")]   // too long
    public void Recipients_ThatAreNotInternationalFormat_AreRefused(string? input)
    {
        Assert.False(SmsRules.TryNormaliseRecipient(input, out _));
    }

    [Theory]
    [InlineData("Verso", true)]
    [InlineData("ABCDEFGHIJK", true)]
    [InlineData("ABCDEFGHIJKL", false)]
    [InlineData("35799000000", true)]
    [InlineData("123456789012345", true)]
    [InlineData("1234567890123456", false)]
    [InlineData("Has Space", false)]
    [InlineData("Név", false)]
    [InlineData("", false)]
    public void Senders_AreUpToElevenLettersAndDigitsOrUpToFifteenDigits(string sender, bool valid) => Assert.Equal(valid, SmsRules.IsValidSender(sender));
}
