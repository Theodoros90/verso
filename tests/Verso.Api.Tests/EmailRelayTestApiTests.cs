using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Verso.Application;
using Verso.Application.Executions;
using Verso.Application.Mail;

namespace Verso.Api.Tests;

/// <summary>"Test connection" for an email relay (ADR 0015): the API queues it, the Worker sends one test message and records the outcome. No address in any result.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class EmailRelayTestApiTests(MigratedDatabaseFixture fixture) : IDisposable
{
    private const string Recipient = "tester.private@bank.example";
    private readonly HttpClient _client = fixture.Factory.CreateClient();

    public void Dispose() => _client.Dispose();

    private async Task<Guid> CreateRelayAsync()
    {
        var key = $"t-{Guid.NewGuid():N}"[..20];
        var response = await _client.PostAsJsonAsync("/api/v1/email-relays", new { key, name = "Test relay", host = "smtp.relay.example", fromAddress = "verso@bank.example", userName = "svc", password = "pw-for-tests" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<(HttpResponseMessage Response, JsonElement Body)> RequestAsync(Guid relayId, string? to = Recipient)
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/email-relays/{relayId}/tests", new { toAddress = to });
        var text = await response.Content.ReadAsStringAsync();
        return (response, text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    private async Task RunAsync(Guid testId, FakeEmailSender email)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IVersoDbContext>();
        await new EmailRelayTestRunner(db, email, TimeProvider.System).RunAsync(testId, CancellationToken.None);
    }

    private async Task<JsonElement> GetAsync(Guid testId) =>
        await (await _client.GetAsync($"/api/v1/email-relays/tests/{testId}")).Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Request_IsAccepted_AndQueued_AndNothingShowsTheAddress()
    {
        var relayId = await CreateRelayAsync();

        var (response, body) = await RequestAsync(relayId);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal("Queued", body.GetProperty("status").GetString());
        Assert.DoesNotContain("tester.private", body.GetRawText(), StringComparison.Ordinal);
        var id = body.GetProperty("id").GetGuid();
        var audit = await _client.GetStringAsync($"/api/v1/audit?entityType=EmailRelayTest&entityId={id}");
        Assert.Contains("EmailRelayTestRequested", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("tester.private", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheWorker_SendsOneTestMessageThroughTheRelay_AndTheAddressIsClearedAfterwards()
    {
        var relayId = await CreateRelayAsync();
        var id = (await RequestAsync(relayId)).Body.GetProperty("id").GetGuid();
        var email = new FakeEmailSender();

        await RunAsync(id, email);
        await RunAsync(id, email); // a duplicate delivery does nothing

        var mail = Assert.Single(email.Sent);
        Assert.Equal([Recipient], mail.To);
        Assert.Equal("Verso test email", mail.Subject);
        Assert.StartsWith("t-", mail.Relay, StringComparison.Ordinal);
        var result = await GetAsync(id);
        Assert.Equal("Succeeded", result.GetProperty("status").GetString());
        Assert.DoesNotContain("tester.private", result.GetRawText(), StringComparison.Ordinal);
        using var scope = fixture.Factory.Services.CreateScope();
        var row = scope.ServiceProvider.GetRequiredService<IVersoDbContext>().EmailRelayTests.Single(t => t.Id == id);
        Assert.Null(row.ToAddress);
    }

    [Fact]
    public async Task ARelayThatFails_GivesAFailedTest_WithTheErrorCode_ButNotTheAddress()
    {
        var relayId = await CreateRelayAsync();
        var id = (await RequestAsync(relayId)).Body.GetProperty("id").GetGuid();

        await RunAsync(id, new FakeEmailSender { Failure = new EmailSendException("email.relay_unavailable", "The SMTP relay could not be reached or did not accept the message.", retryable: true) });

        var result = await GetAsync(id);
        Assert.Equal("Failed", result.GetProperty("status").GetString());
        Assert.Equal("email.relay_unavailable", result.GetProperty("code").GetString());
        Assert.DoesNotContain("tester.private", result.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("a@bank.example, b@bank.example")]
    public async Task ABadRecipient_IsRefused(string? to)
    {
        var relayId = await CreateRelayAsync();

        var (response, body) = await RequestAsync(relayId, to);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("toAddress", body.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownRelay_Is404_AndASwitchedOffRelay_Is409()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await RequestAsync(Guid.NewGuid())).Response.StatusCode);

        var relayId = await CreateRelayAsync();
        await _client.PostAsync($"/api/v1/email-relays/{relayId}/deactivate", null);
        var (response, body) = await RequestAsync(relayId);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("email_relay.inactive", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task OldTests_AreDeletedByTheCleanup_AndADeletedTestIs404()
    {
        var relayId = await CreateRelayAsync();
        var id = (await RequestAsync(relayId)).Body.GetProperty("id").GetGuid();
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IVersoDbContext>();
            var removed = await new EmailRelayTestRunner(db, new FakeEmailSender(), new Shifted(TimeSpan.FromDays(2))).CleanupAsync(CancellationToken.None);
            Assert.True(removed >= 1);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/email-relays/tests/{id}")).StatusCode);
    }

    [Fact]
    public async Task TheOptionsList_HoldsOnlyKeyAndName_AndAnyRoleMayReadIt()
    {
        await CreateRelayAsync();
        using var viewer = fixture.Factory.CreateClientAs("dev.viewer");

        var text = await viewer.GetStringAsync("/api/v1/email-relay-options");

        Assert.Contains("\"key\"", text, StringComparison.Ordinal);
        Assert.Contains("Test relay", text, StringComparison.Ordinal);
        Assert.DoesNotContain("smtp.relay.example", text, StringComparison.Ordinal);
        Assert.DoesNotContain("verso@bank.example", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"userName\"", text, StringComparison.Ordinal);
    }

    private sealed class Shifted(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
    }
}
