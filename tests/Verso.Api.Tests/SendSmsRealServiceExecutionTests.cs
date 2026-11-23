using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verso.Application;
using Verso.Application.Connections;
using Verso.Application.Mail;
using Verso.Domain.Connections;
using Verso.Domain.Executions;
using Verso.Infrastructure.Connections;
using Verso.Infrastructure.Security;

namespace Verso.Api.Tests;

/// <summary>
/// The Send SMS step in a real run with the REAL <see cref="SmsSendService"/> and <see cref="HttpSmsSender"/> (wired as the Worker wires them: a service built from a
/// scoped database context, the master-key secret protector and the connection options), talking to a fake HTTP message handler: no network. SendSmsExecutionTests
/// uses a FakeSmsSend for the engine's own behaviour; these tests cover the seam between the two (the administrator's switches refused before any request, the data
/// refusals, the status codes and the engine's never-retry rule) and that the number, the text and the tag appear ONLY in the request body.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class SendSmsRealServiceExecutionTests(MigratedDatabaseFixture fixture) : IDisposable
{
    private const string Digits = "35799000006";
    private const string TextMarker = "REALTXT-7731";

    private static readonly AesGcmSecretProtector Protector = new(AesGcmSecretProtector.GenerateKey());

    private readonly HttpClient _client = fixture.Factory.CreateClient();
    private readonly TestDocumentStore _store = new();
    private readonly FakePdfPrinter _printer = new();
    private readonly RecordingExecutionJobs _jobs = new();
    private ExecutionTestKit Kit => new(fixture.Factory, _client);

    public void Dispose()
    {
        _store.Dispose();
        _client.Dispose();
    }

    /// <summary>The real service, built per call from a fresh scope (the engine's own context is scoped to its run, the service's to the call).</summary>
    private sealed class ScopedRealSms(IServiceProvider services, SmsSendTests.FakeHandler handler, ISecretProtector protector) : ISmsSend
    {
        public async Task<SmsSendResult> RunAsync(SmsSendRequest request, CancellationToken ct)
        {
            using var scope = services.CreateScope();
            var service = new SmsSendService(scope.ServiceProvider.GetRequiredService<IVersoDbContext>(), protector, new HttpSmsSender(() => handler), new SmsConnectionOptions());
            return await service.RunAsync(request, ct);
        }
    }

    private static SmsSendTests.FakeHandler Handler(HttpStatusCode status, string body = "{}") =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));

    private async Task<string> CreateConnectionAsync(bool allowSending = true, string prefixes = "357", string type = SmsConnection.Transactional)
    {
        var key = $"rsms-{Guid.NewGuid():N}"[..20];
        var now = DateTimeOffset.UtcNow;
        var connection = SmsConnection.Create(key, "SMS " + key, "https://api.sms-real.example/v3", "Verso", type, false, "", 10, "tests", "tests", now);
        connection.SetSending(allowSending, prefixes, "tests", now);
        connection.SetSecret(Protector.Protect("real-sms-key-not-real", SmsConnectionContexts.For(key)), "tests", now);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IVersoDbContext>();
        db.SmsConnections.Add(connection);
        await db.SaveChangesAsync(CancellationToken.None);
        return key;
    }

    private static JsonNode Data(string mobile = "+" + Digits)
    {
        var data = ExecutionTestKit.Sample.DeepClone();
        data["customer"]!["mobile"] = mobile;
        data["invoice"]!["poNumber"] = TextMarker;
        return data;
    }

    private async Task<(Guid Id, JsonElement Done)> RunAsync(string connection, SmsSendTests.FakeHandler handler, JsonNode? data = null, ISecretProtector? protector = null)
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(smsConfig: new JsonObject
        {
            ["connection"] = connection,
            ["to"] = "{{customer.mobile}}",
            ["text"] = "Your statement {{invoice.poNumber}} is ready.",
        });
        var id = await Kit.SubmitNewAsync(key, data ?? Data());
        await Kit.RunEngineAsync(id, _printer, _store, _jobs, sms: new ScopedRealSms(fixture.Factory.Services, handler, protector ?? Protector));
        return (id, await Kit.GetExecutionAsync(id));
    }

    private Task<List<string>> EventTypesAsync(Guid id) =>
        Kit.WithDbAsync(db => db.ExecutionEvents.Where(e => e.ExecutionId == id).OrderBy(e => e.Id).Select(e => e.Type).ToListAsync());

    private async Task AssertFailedWithoutRetryAsync(Guid id, JsonElement done, string code)
    {
        Assert.Equal("Failed", done.GetProperty("status").GetString());
        Assert.Equal(code, done.GetProperty("failureCode").GetString());
        Assert.Empty(_jobs.Scheduled);
        Assert.DoesNotContain(ExecutionEventTypes.RetryScheduled, await EventTypesAsync(id));
        Assert.Equal(0, _printer.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Switch_off_fails_sending_not_allowed_without_a_request(bool wrongMasterKey)
    {
        var handler = Handler(HttpStatusCode.Created, """{"messageId":1}""");
        var (id, done) = await RunAsync(await CreateConnectionAsync(allowSending: false), handler,
            protector: wrongMasterKey ? new AesGcmSecretProtector(AesGcmSecretProtector.GenerateKey()) : null);

        await AssertFailedWithoutRetryAsync(id, done, "sms.sending_not_allowed");
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Marketing_connection_fails_marketing_not_allowed_without_a_request()
    {
        var handler = Handler(HttpStatusCode.Created, """{"messageId":1}""");
        var (id, done) = await RunAsync(await CreateConnectionAsync(type: SmsConnection.Marketing), handler);

        await AssertFailedWithoutRetryAsync(id, done, "sms.marketing_not_allowed");
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Invalid_number_in_data_rejects_without_a_request()
    {
        var handler = Handler(HttpStatusCode.Created, """{"messageId":1}""");
        var (_, done) = await RunAsync(await CreateConnectionAsync(), handler, Data(mobile: "12"));

        Assert.Equal("Rejected", done.GetProperty("status").GetString());
        Assert.Equal("data.sms_invalid", done.GetProperty("failureCode").GetString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Prefix_outside_the_allowed_list_rejects_without_a_request()
    {
        var handler = Handler(HttpStatusCode.Created, """{"messageId":1}""");
        var (_, done) = await RunAsync(await CreateConnectionAsync(prefixes: "30,44"), handler);

        Assert.Equal("Rejected", done.GetProperty("status").GetString());
        Assert.Equal("data.sms_invalid", done.GetProperty("failureCode").GetString());
        var issue = Assert.Single(done.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("nodeId").GetString() == "sms")
            .GetProperty("result").GetProperty("issues").EnumerateArray());
        Assert.Equal("sms.country_not_allowed", issue.GetProperty("code").GetString());
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain(Digits, done.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_201_is_sent_audited_and_the_run_continues_to_the_document()
    {
        var handler = Handler(HttpStatusCode.Created, """{"messageId":1511882900176220,"smsCount":1}""");
        var connection = await CreateConnectionAsync();
        var (id, done) = await RunAsync(connection, handler);

        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Single(handler.Requests);
        var step = done.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("nodeId").GetString() == "sms");
        Assert.Equal("sent", step.GetProperty("outputPort").GetString());
        Assert.Equal("1511882900176220", step.GetProperty("result").GetProperty("messageId").GetString());
        Assert.Equal(1, _printer.Calls);
        var audit = await Kit.WithDbAsync(db => db.AuditEntries.Where(a => a.EntityId == id.ToString() && a.Action == "SmsSent").Select(a => a.Details).SingleAsync());
        using var details = JsonDocument.Parse(audit!);
        Assert.Equal(connection, details.RootElement.GetProperty("connection").GetString());
        Assert.Equal("1511882900176220", details.RootElement.GetProperty("messageId").GetString());
    }

    [Fact]
    public async Task A_503_fails_unknown_without_retry_and_the_handler_is_called_once()
    {
        var handler = Handler(HttpStatusCode.ServiceUnavailable);
        var (id, done) = await RunAsync(await CreateConnectionAsync(), handler);

        await AssertFailedWithoutRetryAsync(id, done, "sms.server_error");
        Assert.Contains("outcome is unknown", done.GetProperty("failureMessage").GetString(), StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_429_fails_known_without_retry()
    {
        var handler = Handler(HttpStatusCode.TooManyRequests);
        var (id, done) = await RunAsync(await CreateConnectionAsync(), handler);

        await AssertFailedWithoutRetryAsync(id, done, "sms.rate_limited");
        Assert.DoesNotContain("outcome is unknown", done.GetProperty("failureMessage").GetString(), StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task The_number_text_and_tag_appear_only_in_the_request_body()
    {
        var handler = Handler(HttpStatusCode.Created, """{"messageId":42}""");
        var (id, done) = await RunAsync(await CreateConnectionAsync(), handler);

        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        var tag = $"verso-{id.ToString("N")[..12]}-0";
        using (var body = JsonDocument.Parse(Assert.Single(handler.Bodies)))
        {
            Assert.Equal(Digits, body.RootElement.GetProperty("recipient").GetString());
            Assert.Equal($"Your statement {TextMarker} is ready.", body.RootElement.GetProperty("content").GetString());
            Assert.Equal(tag, body.RootElement.GetProperty("tag").GetString());
        }

        using var events = await _client.GetAsync(new Uri($"/api/v1/executions/{id}/events", UriKind.Relative));
        var (rows, audit) = await Kit.WithDbAsync(async db =>
        {
            var eventData = await db.ExecutionEvents.Where(e => e.ExecutionId == id).Select(e => e.Type + " " + e.Data).ToListAsync();
            var auditDetails = await db.AuditEntries.Where(a => a.EntityId == id.ToString()).Select(a => a.Action + " " + a.Details).ToListAsync();
            return (string.Join('\n', eventData), string.Join('\n', auditDetails));
        });
        foreach (var text in new[] { done.GetRawText(), await events.Content.ReadAsStringAsync(), rows, audit })
        {
            Assert.DoesNotContain(Digits, text, StringComparison.Ordinal);
            Assert.DoesNotContain(TextMarker, text, StringComparison.Ordinal);
            Assert.DoesNotContain(tag, text, StringComparison.Ordinal);
        }
    }
}
