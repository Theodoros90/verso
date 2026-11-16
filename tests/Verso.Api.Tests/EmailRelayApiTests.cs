using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Verso.Application.Executions;
using Verso.Application.Mail;
using Verso.Infrastructure.Email;
using Verso.Infrastructure.Security;

namespace Verso.Api.Tests;

/// <summary>Email relays managed in the app (ADR 0015): administrators only, the password is write-only and stored encrypted, every change is audited without values.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class EmailRelayApiTests(MigratedDatabaseFixture fixture) : IDisposable
{
    private const string Secret = "s3cret-app-password-for-tests";
    private static readonly string[] BadDomains = ["nodot"];
    private readonly HttpClient _client = fixture.Factory.CreateClient();

    public void Dispose() => _client.Dispose();

    private static string UniqueKey(string prefix = "relay") => $"{prefix}-{Guid.NewGuid():N}"[..30];

    private static object Body(string key, string? password = Secret, string host = "smtp.relay.example", string from = "verso@bank.example", string[]? domains = null) => new
    {
        key, name = "Relay " + key, host, port = 587, enableSsl = true, userName = "svc-mail", fromAddress = from, fromName = "Verso",
        timeoutSeconds = 30, allowedRecipientDomains = domains ?? ["customers.example"], password,
    };

    private async Task<JsonElement> CreateAsync(string key, string? password = Secret)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/email-relays", Body(key, password));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Create_StoresTheRelay_AndNeverReturnsThePassword()
    {
        var key = UniqueKey();

        var created = await CreateAsync(key);

        Assert.Equal(key, created.GetProperty("key").GetString());
        Assert.True(created.GetProperty("hasPassword").GetBoolean());
        Assert.Equal(["customers.example"], created.GetProperty("allowedRecipientDomains").EnumerateArray().Select(d => d.GetString()));
        Assert.DoesNotContain(Secret, created.GetRawText(), StringComparison.Ordinal);

        var list = await _client.GetStringAsync("/api/v1/email-relays?includeInactive=true");
        Assert.Contains(key, list, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, list, StringComparison.Ordinal);
        Assert.DoesNotContain("password\"", list.Replace("hasPassword", ""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ThePassword_IsEncryptedInTheDatabase_AndTheWorkerSideStoreReadsItBack()
    {
        var key = UniqueKey();
        await CreateAsync(key);

        var store = new EmailRelayStore(Options.Create(new EmailOptions()), fixture.Factory.Services.GetRequiredService<ISecretProtector>(), fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>());
        var settings = await store.FindAsync(key, CancellationToken.None);

        Assert.NotNull(settings);
        Assert.Equal(Secret, settings.Password);
        Assert.Equal("smtp.relay.example", settings.Host);
        Assert.Equal("verso@bank.example", settings.From);
        Assert.Equal(["customers.example"], settings.AllowedRecipientDomains);

        // What is stored is not the password.
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Verso.Application.IVersoDbContext>();
        var stored = db.EmailRelays.Single(r => r.Key == key).PasswordProtected;
        Assert.NotNull(stored);
        Assert.DoesNotContain(Secret, stored, StringComparison.Ordinal);
        Assert.StartsWith("v1.", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_WithoutAPassword_KeepsTheStoredOne_AndWithAValue_ReplacesIt_AndClearRemovesIt()
    {
        var key = UniqueKey();
        var id = (await CreateAsync(key)).GetProperty("id").GetGuid();
        var store = new EmailRelayStore(Options.Create(new EmailOptions()), fixture.Factory.Services.GetRequiredService<ISecretProtector>(), fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>());

        var keep = await _client.PutAsJsonAsync($"/api/v1/email-relays/{id}", Body(key, password: null, host: "smtp2.relay.example"));
        Assert.Equal(HttpStatusCode.OK, keep.StatusCode);
        var kept = await store.FindAsync(key, CancellationToken.None);
        Assert.Equal(Secret, kept!.Password);
        Assert.Equal("smtp2.relay.example", kept.Host);

        await _client.PutAsJsonAsync($"/api/v1/email-relays/{id}", Body(key, password: "another-password"));
        Assert.Equal("another-password", (await store.FindAsync(key, CancellationToken.None))!.Password);

        var cleared = await _client.PutAsJsonAsync($"/api/v1/email-relays/{id}", new { name = "Relay", host = "smtp.relay.example", fromAddress = "verso@bank.example", clearPassword = true });
        var dto = await cleared.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(dto.GetProperty("hasPassword").GetBoolean());
        Assert.Equal("", (await store.FindAsync(key, CancellationToken.None))!.Password);
    }

    [Fact]
    public async Task EveryChange_IsAudited_WithoutAnyValue()
    {
        var key = UniqueKey();
        var created = await CreateAsync(key);
        var id = created.GetProperty("id").GetGuid();
        await _client.PutAsJsonAsync($"/api/v1/email-relays/{id}", Body(key, password: "second-password", host: "smtp3.relay.example"));
        await _client.PostAsync($"/api/v1/email-relays/{id}/deactivate", null);

        var audit = await _client.GetStringAsync($"/api/v1/audit?entityType=EmailRelay&entityId={id}");

        foreach (var action in new[] { "EmailRelayCreated", "EmailRelayUpdated", "EmailRelayPasswordChanged", "EmailRelayDeactivated" })
        {
            Assert.Contains(action, audit, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(Secret, audit, StringComparison.Ordinal);
        Assert.DoesNotContain("second-password", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("smtp3.relay.example", audit, StringComparison.Ordinal); // field names only, not values
    }

    [Fact]
    public async Task ADeactivatedRelay_IsUnknownToTheWorker_AndAnActivatedOneWorksAgain()
    {
        var key = UniqueKey();
        var id = (await CreateAsync(key)).GetProperty("id").GetGuid();
        var store = new EmailRelayStore(Options.Create(new EmailOptions()), fixture.Factory.Services.GetRequiredService<ISecretProtector>(), fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>());

        await _client.PostAsync($"/api/v1/email-relays/{id}/deactivate", null);
        Assert.Null(await store.FindAsync(key, CancellationToken.None));

        await _client.PostAsync($"/api/v1/email-relays/{id}/activate", null);
        Assert.NotNull(await store.FindAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task ARelayInTheDatabase_WinsOverOneInConfiguration_AndConfigurationStillWorksWithout()
    {
        var key = UniqueKey();
        await CreateAsync(key);
        var config = new EmailOptions { Host = "default.invalid", From = "default@bank.example", Relays = { [key] = new EmailOptions { Host = "config.invalid", From = "c@bank.example" }, ["only-config"] = new EmailOptions { Host = "cfg.invalid", From = "x@bank.example" } } };
        var store = new EmailRelayStore(Options.Create(config), fixture.Factory.Services.GetRequiredService<ISecretProtector>(), fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>());

        Assert.Equal("smtp.relay.example", (await store.FindAsync(key, CancellationToken.None))!.Host);
        Assert.Equal("cfg.invalid", (await store.FindAsync("only-config", CancellationToken.None))!.Host);
        Assert.Equal("default.invalid", (await store.FindAsync(EmailRelays.DefaultKey, CancellationToken.None))!.Host);
        Assert.Null(await store.FindAsync("nobody", CancellationToken.None));
    }

    [Fact]
    public async Task APasswordThatCannotBeRead_FailsWithAClearNonRetryableError()
    {
        var key = UniqueKey();
        await CreateAsync(key);
        var otherKey = new AesGcmSecretProtector(AesGcmSecretProtector.GenerateKey());
        var store = new EmailRelayStore(Options.Create(new EmailOptions()), otherKey, fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>());

        var ex = await Assert.ThrowsAsync<EmailSendException>(() => store.FindAsync(key, CancellationToken.None));

        Assert.Equal("email.password_unreadable", ex.Code);
        Assert.False(ex.Retryable);
        Assert.DoesNotContain(Secret, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validation_RejectsABadKeyServerPortSenderAndDomain_AndDuplicateKeysConflict()
    {
        var bad = await _client.PostAsJsonAsync("/api/v1/email-relays", new { key = "Bad Key", name = "", host = "smtp host/x", port = 70000, fromAddress = "not-an-address", allowedRecipientDomains = BadDomains });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var problem = await bad.Content.ReadAsStringAsync();
        foreach (var field in new[] { "key", "name", "host", "port", "fromAddress", "allowedRecipientDomains" })
        {
            Assert.Contains($"\"{field}\"", problem, StringComparison.Ordinal);
        }

        var key = UniqueKey();
        await CreateAsync(key);
        var duplicate = await _client.PostAsJsonAsync("/api/v1/email-relays", Body(key.ToUpperInvariant()[..2] + key[2..]));
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode); // upper-case is not a valid key
        var again = await _client.PostAsJsonAsync("/api/v1/email-relays", Body(key));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task OnlyAdministrators_CanUseTheEndpoints()
    {
        foreach (var user in new[] { "dev.viewer", "dev.template-author", "dev.workflow-author", "dev.auditor", "dev.operator" })
        {
            using var other = fixture.Factory.CreateClientAs(user);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/v1/email-relays")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync("/api/v1/email-relays", Body(UniqueKey()))).StatusCode);
        }
    }

    [Fact]
    public async Task WithoutAMasterKey_APasswordIsRefused_ButARelayWithoutOneIsFine()
    {
        using var noKey = fixture.Factory.WithWebHostBuilder(b => b.UseSetting("Secrets:MasterKey", ""));
        using var client = noKey.CreateClientAs("dev.admin");

        var refused = await client.PostAsJsonAsync("/api/v1/email-relays", Body(UniqueKey(), Secret));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Secrets:MasterKey", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var fine = await client.PostAsJsonAsync("/api/v1/email-relays", Body(UniqueKey(), password: null));
        Assert.Equal(HttpStatusCode.Created, fine.StatusCode);
    }
}

public sealed class SecretProtectorTests
{
    private static AesGcmSecretProtector Protector() => new(AesGcmSecretProtector.GenerateKey());

    [Fact]
    public void RoundTrips_AndEveryValueIsDifferent()
    {
        var p = Protector();
        var a = p.Protect("hunter2", "email-relay:a");
        var b = p.Protect("hunter2", "email-relay:a");

        Assert.NotEqual(a, b); // a fresh nonce each time
        Assert.Equal("hunter2", p.Unprotect(a, "email-relay:a"));
        Assert.DoesNotContain("hunter2", a, StringComparison.Ordinal);
    }

    [Fact]
    public void AValueMovedToAnotherContext_OrTamperedWith_OrOpenedWithAnotherKey_IsRefused()
    {
        var p = Protector();
        var value = p.Protect("hunter2", "email-relay:a");

        Assert.Null(p.Unprotect(value, "email-relay:b"));
        Assert.Null(p.Unprotect(value[..^4] + "AAAA", "email-relay:a"));
        Assert.Null(Protector().Unprotect(value, "email-relay:a"));
        Assert.Null(p.Unprotect("garbage", "email-relay:a"));
        Assert.Null(p.Unprotect("v1.AAAA", "email-relay:a"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!!")]
    [InlineData("AAECAw==")] // too short
    public void WithoutAUsableMasterKey_NothingCanBeProtected(string? key)
    {
        var p = new AesGcmSecretProtector(key);

        Assert.False(p.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => p.Protect("x", "c"));
        Assert.Null(p.Unprotect("v1.AAAA", "c"));
    }
}
