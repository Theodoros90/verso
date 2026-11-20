using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Verso.Application;
using Verso.Application.Connections;
using Verso.Application.Mail;
using Verso.Infrastructure;
using Verso.Infrastructure.Connections;
using Verso.Infrastructure.Security;

namespace Verso.Api.Tests;

/// <summary>The connection test's request must stay byte for byte what it was before the Brevo helpers were shared with the Send SMS step.</summary>
public sealed class SmsProbeRequestUnchangedTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Uri { get; private set; }
        public byte[]? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri!.AbsoluteUri;
            Body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }
    }

    [Theory]
    [InlineData("TestOrg", "marketing", false, "{\"sender\":\"TestSender\",\"recipient\":\"35799000000\",\"content\":\"Verso test message\",\"type\":\"marketing\",\"unicodeEnabled\":false,\"organisationPrefix\":\"TestOrg\"}")]
    [InlineData("", "transactional", true, "{\"sender\":\"TestSender\",\"recipient\":\"35799000000\",\"content\":\"Verso test message\",\"type\":\"transactional\",\"unicodeEnabled\":true}")]
    [InlineData("Café & <Co>", "transactional", true, "{\"sender\":\"TestSender\",\"recipient\":\"35799000000\",\"content\":\"Verso test message\",\"type\":\"transactional\",\"unicodeEnabled\":true,\"organisationPrefix\":\"Caf\\u00E9 \\u0026 \\u003CCo\\u003E\"}")]
    public async Task The_probe_sends_exactly_the_same_address_and_body_bytes_as_before(string prefix, string type, bool unicode, string expectedBody)
    {
        var handler = new CapturingHandler();
        var settings = new SmsConnectionSettings("https://api.brevo.test/v3/", "TestSender", type, unicode, prefix, "test-key-not-real", TimeSpan.FromSeconds(10));

        var result = await new BrevoSmsProbe(() => handler).SendTestAsync(settings, "35799000000", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("https://api.brevo.test/v3/transactionalSMS/sms", handler.Uri);
        Assert.Equal(expectedBody, Encoding.UTF8.GetString(handler.Body!));
    }
}

/// <summary>
/// The Send SMS service and its HTTP sender (Phase 4): one POST to the connection's Brevo address, the switch / marketing / country / host checks before
/// the key is decrypted, known versus unknown outcomes, and nothing personal (number, text, key, tag) in any result, message or ToString.
/// All HTTP is faked with a message handler: no test touches the network and no SMS is ever sent.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class SmsSendTests(MigratedDatabaseFixture fixture)
{
    private const string Host = "sms.send-host.example";
    private const string BaseUrl = $"https://{Host}/v3";
    private const string ApiKey = "sms-key-MARKER-not-real-123";
    private const string Digits = "35799000000";
    private const string Tag = "verso-0123456789ab-1";
    private const string UnknownWording = "The outcome is unknown: the message may or may not have been sent. Check the SMS provider's log before running this step again.";
    private static readonly AesGcmSecretProtector Protector = new(AesGcmSecretProtector.GenerateKey());

    internal sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Uris { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Uris.Add(request.RequestUri!.AbsoluteUri);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return await respond(request, cancellationToken);
        }
    }

    /// <summary>Counts decrypts; delegates to the inner protector (one with the WRONG master key in most tests).</summary>
    private sealed class CountingProtector(ISecretProtector inner) : ISecretProtector
    {
        public int Unprotects { get; private set; }
        public bool IsConfigured => inner.IsConfigured;
        public string Protect(string secret, string context) => inner.Protect(secret, context);

        public string? Unprotect(string protectedValue, string context)
        {
            Unprotects++;
            return inner.Unprotect(protectedValue, context);
        }
    }

    private sealed class ThrowingSender(Exception ex) : ISmsSender
    {
        public Task<SmsSendResult> SendAsync(SmsConnectionSettings settings, SmsSendRequest request, CancellationToken ct) => throw ex;
    }

    private abstract class ReadOnlyStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("synchronous read");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public abstract override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
    }

    /// <summary>A body that never arrives: every read waits until its token is cancelled.</summary>
    private sealed class StallingStream : ReadOnlyStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    /// <summary>A body that arrives after a delay.</summary>
    private sealed class DelayedStream(byte[] data, TimeSpan delay) : ReadOnlyStream
    {
        private int _position;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position == 0)
            {
                await Task.Delay(delay, cancellationToken);
            }

            var n = Math.Min(buffer.Length, data.Length - _position);
            data.AsMemory(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }
    }

    /// <summary>A body whose connection breaks off.</summary>
    private sealed class BreakingStream : ReadOnlyStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("connection reset"));
    }

    private static HttpResponseMessage Stalling(HttpStatusCode status)
    {
        var content = new StreamContent(new StallingStream());
        content.Headers.ContentType = new("application/json");
        return new HttpResponseMessage(status) { Content = content };
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Answer(Func<HttpResponseMessage> make) => (_, _) => Task.FromResult(make());

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Throw(Exception ex) => (_, _) => Task.FromException<HttpResponseMessage>(ex);

    /// <summary>Fails the test instead of hanging the suite if <paramref name="task"/> does not finish within <paramref name="seconds"/>.</summary>
    private static async Task<T> Bounded<T>(Task<T> task, int seconds = 20)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds))));
        return await task;
    }

    private static SmsConnectionSettings Settings(string prefix = "TestOrg", int timeoutSeconds = 10, string baseUrl = BaseUrl) =>
        new(baseUrl, "TestSender", "transactional", false, prefix, ApiKey, TimeSpan.FromSeconds(timeoutSeconds));

    private static SmsSendRequest Request(string key = "unused", string recipient = Digits, string text = "Hello", string tag = Tag) => new(key, recipient, text, tag);

    private static async Task<(SmsSendResult Result, FakeHandler Handler)> SendAsync(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? respond = null, SmsConnectionSettings? settings = null, SmsSendRequest? request = null, CancellationToken ct = default)
    {
        var handler = new FakeHandler(respond ?? Answer(() => Json(HttpStatusCode.Created, "{\"reference\":\"r\",\"messageId\":1511882900176220,\"smsCount\":2}")));
        var result = await new HttpSmsSender(() => handler).SendAsync(settings ?? Settings(), request ?? Request(), ct);
        return (result, handler);
    }

    private static string UniqueKey() => $"smss-{Guid.NewGuid():N}"[..20];

    private async Task<string> CreateConnectionAsync(
        bool allowSending = true, string prefixes = "357,30", string type = "transactional", bool unicode = false, string baseUrl = BaseUrl, bool active = true, bool withKey = true)
    {
        var key = UniqueKey();
        var now = DateTimeOffset.UtcNow;
        var connection = Verso.Domain.Connections.SmsConnection.Create(key, "SMS " + key, baseUrl, "TestSender", type, unicode, "", 10, "tests", "tests", now);
        connection.SetSending(allowSending, prefixes, "tests", now);
        if (withKey)
        {
            connection.SetSecret(Protector.Protect(ApiKey, SmsConnectionContexts.For(key)), "tests", now);
        }

        if (!active)
        {
            connection.Deactivate("tests", now);
        }

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IVersoDbContext>();
        db.SmsConnections.Add(connection);
        await db.SaveChangesAsync(CancellationToken.None);
        return key;
    }

    private async Task<SmsSendResult> RunAsync(SmsSendRequest request, ISmsSender sender, ISecretProtector? protector = null, SmsConnectionOptions? options = null, CancellationToken ct = default)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var service = new SmsSendService(scope.ServiceProvider.GetRequiredService<IVersoDbContext>(), protector ?? Protector, sender, options ?? new SmsConnectionOptions());
        return await service.RunAsync(request, ct);
    }

    private async Task<(SmsSendResult Result, FakeHandler Handler, CountingProtector Protector)> RunWithHandlerAsync(
        SmsSendRequest request, ISecretProtector? protector = null, SmsConnectionOptions? options = null, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? respond = null)
    {
        var handler = new FakeHandler(respond ?? Answer(() => Json(HttpStatusCode.Created, "{\"messageId\":77,\"smsCount\":1}")));
        var counting = new CountingProtector(protector ?? Protector);
        var result = await RunAsync(request, new HttpSmsSender(() => handler), counting, options);
        return (result, handler, counting);
    }

    private static AesGcmSecretProtector WrongMasterKey() => new(AesGcmSecretProtector.GenerateKey());

    private static void AssertKnown(SmsSendResult result, string code)
    {
        Assert.False(result.Ok);
        Assert.Equal(code, result.Code);
        Assert.False(result.OutcomeUnknown, code);
        Assert.Equal(SmsProbeMessages.For(code), result.Message);
        Assert.NotEqual("The test could not be run.", result.Message); // every code has its own wording
        Assert.Null(result.MessageId);
    }

    private static void AssertUnknown(SmsSendResult result, string code)
    {
        Assert.False(result.Ok);
        Assert.Equal(code, result.Code);
        Assert.True(result.OutcomeUnknown, code);
        Assert.Equal(UnknownWording, result.Message);
        Assert.Equal(UnknownWording, SmsSendMessages.OutcomeUnknown);
    }

    // ---------------------------------------------------------------- the sender

    [Fact]
    public async Task Sends_one_POST_to_base_plus_the_shared_send_path_with_the_documented_body_and_headers()
    {
        var (result, handler) = await SendAsync();

        Assert.True(result.Ok);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(BaseUrl + BrevoSms.SendPath, handler.Uris[0]);
        Assert.Equal("/transactionalSMS/sms", BrevoSms.SendPath);
        Assert.Equal(ApiKey, Assert.Single(request.Headers.GetValues("api-key")));
        Assert.Equal("application/json", Assert.Single(request.Headers.Accept).ToString());
        Assert.Equal("Verso-sms/1.0", request.Headers.UserAgent.ToString());
        Assert.Null(request.Headers.Authorization);
        Assert.Equal("Accept,User-Agent,api-key", string.Join(",", request.Headers.Select(h => h.Key).Order(StringComparer.Ordinal)));
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(
            "{\"sender\":\"TestSender\",\"recipient\":\"35799000000\",\"content\":\"Hello\",\"type\":\"transactional\",\"unicodeEnabled\":false,\"organisationPrefix\":\"TestOrg\",\"tag\":\"verso-0123456789ab-1\"}",
            handler.Bodies[0]);
    }

    [Fact]
    public async Task Without_an_organisation_prefix_the_field_is_left_out_and_the_tag_stays_last()
    {
        var (_, handler) = await SendAsync(settings: Settings(prefix: ""), request: Request(text: "Line 1\nCafé"));

        Assert.Equal(
            "{\"sender\":\"TestSender\",\"recipient\":\"35799000000\",\"content\":\"Line 1\\nCaf\\u00E9\",\"type\":\"transactional\",\"unicodeEnabled\":false,\"tag\":\"verso-0123456789ab-1\"}",
            handler.Bodies[0]);
        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("Line 1\nCafé", body.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Success_returns_message_id_and_parts_when_present()
    {
        var (number, _) = await SendAsync();
        var (text, _) = await SendAsync(Answer(() => Json(HttpStatusCode.Created, "{\"messageId\":\"abc-1\",\"smsCount\":3}")));

        Assert.True(number.Ok);
        Assert.Null(number.Code);
        Assert.Null(number.Message);
        Assert.False(number.OutcomeUnknown);
        Assert.Equal("1511882900176220", number.MessageId);
        Assert.Equal(2, number.SmsParts);
        Assert.True(number.DurationMs >= 0);
        Assert.Equal("abc-1", text.MessageId);
        Assert.Equal(3, text.SmsParts);
    }

    [Fact]
    public async Task A_message_id_longer_than_100_characters_is_dropped()
    {
        var at = new string('i', 100);
        var over = new string('i', 101);
        var (kept, _) = await SendAsync(Answer(() => Json(HttpStatusCode.Created, $"{{\"messageId\":\"{at}\",\"smsCount\":1}}")));
        var (dropped, _) = await SendAsync(Answer(() => Json(HttpStatusCode.Created, $"{{\"messageId\":\"{over}\",\"smsCount\":1}}")));
        var (longNumber, _) = await SendAsync(Answer(() => Json(HttpStatusCode.Created, $"{{\"messageId\":1{new string('0', 100)}}}")));

        Assert.Equal(at, kept.MessageId);
        Assert.True(dropped.Ok);
        Assert.Null(dropped.MessageId);
        Assert.Equal(1, dropped.SmsParts);
        Assert.True(longNumber.Ok);
        Assert.Null(longNumber.MessageId);
    }

    [Theory]
    [InlineData(201, "")]
    [InlineData(201, "not json")]
    [InlineData(200, "{}")]
    [InlineData(201, "[1,2]")]
    [InlineData(201, "{\"messageId\":{\"x\":1},\"smsCount\":\"two\"}")]
    [InlineData(202, "{\"messageId\":null}")]
    public async Task Success_without_body_fields_is_still_sent(int status, string body)
    {
        var (result, _) = await SendAsync(Answer(() => Json((HttpStatusCode)status, body)));

        Assert.True(result.Ok);
        Assert.False(result.OutcomeUnknown);
        Assert.Null(result.MessageId);
        Assert.Null(result.SmsParts);
    }

    [Fact]
    public async Task A_2xx_answer_larger_than_64_KB_is_still_sent()
    {
        var (result, _) = await SendAsync(Answer(() => Json(HttpStatusCode.Created, "{\"messageId\":5,\"pad\":\"" + new string('x', 200_000) + "\"}")));

        Assert.True(result.Ok);
        Assert.Null(result.MessageId); // only the first 64 KB are read: the cut JSON is not parsed, which is fine
    }

    [Fact]
    public async Task A_2xx_whose_body_stalls_or_breaks_off_is_still_sent()
    {
        var (stalled, _) = await Bounded(SendAsync(Answer(() => Stalling(HttpStatusCode.Created)), Settings(timeoutSeconds: 1)));
        var (broken, _) = await SendAsync(Answer(() => new HttpResponseMessage(HttpStatusCode.Created) { Content = new StreamContent(new BreakingStream()) }));

        Assert.True(stalled.Ok);
        Assert.Null(stalled.MessageId);
        Assert.True(broken.Ok);
    }

    [Theory]
    [InlineData(400, "{\"code\":\"invalid_parameter\",\"message\":\"recipient is invalid\"}", "sms.invalid_recipient")]
    [InlineData(400, "{\"code\":\"not_enough_credits\",\"message\":\"Not enough credits\"}", "sms.insufficient_credits")]
    [InlineData(400, "{\"code\":\"invalid_parameter\",\"message\":\"sender is too long\"}", "sms.client_error")]
    [InlineData(400, "", "sms.client_error")]
    [InlineData(401, "{}", "sms.unauthorized")]
    [InlineData(403, "{}", "sms.unauthorized")]
    [InlineData(402, "{}", "sms.insufficient_credits")]
    [InlineData(429, "{}", "sms.rate_limited")]
    [InlineData(404, "{}", "sms.client_error")]
    [InlineData(302, "{}", "sms.redirect_not_followed")]
    [InlineData(301, "{}", "sms.redirect_not_followed")]
    public async Task Rejections_by_the_provider_are_known_failures(int status, string body, string code)
    {
        var (result, handler) = await SendAsync(Answer(() => Json((HttpStatusCode)status, body)));

        AssertKnown(result, code);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(400, "{\"code\":\"duplicate_request\",\"message\":\"duplicate\"}", "sms.duplicate_request")]
    [InlineData(400, "{\"code\":\"invalid_parameter\",\"message\":\"Request already processed\"}", "sms.duplicate_request")]
    [InlineData(500, "{}", "sms.server_error")]
    [InlineData(502, "", "sms.server_error")]
    [InlineData(503, "{}", "sms.server_error")]
    public async Task Answers_that_may_follow_an_accepted_message_are_outcome_unknown(int status, string body, string code)
    {
        var (result, _) = await SendAsync(Answer(() => Json((HttpStatusCode)status, body)));

        AssertUnknown(result, code);
    }

    [Fact]
    public async Task Timeout_after_headers_is_outcome_unknown()
    {
        // A 400 whose body never arrives: it could say "duplicate_request", so it is unknown; ONE deadline covers the POST and the body read.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (afterHeaders, _) = await Bounded(SendAsync(Answer(() => Stalling(HttpStatusCode.BadRequest)), Settings(timeoutSeconds: 1)));
        Assert.InRange(clock.Elapsed.TotalSeconds, 0.9, 10);
        AssertUnknown(afterHeaders, "sms.timeout");

        // No headers at all within the deadline.
        var (noAnswer, _) = await Bounded(SendAsync(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json(HttpStatusCode.Created, "{}"); }, Settings(timeoutSeconds: 1)));
        AssertUnknown(noAnswer, "sms.timeout");
    }

    [Fact]
    public async Task The_post_and_the_body_read_share_one_deadline()
    {
        // Headers after 1.4 s, then a 400 body after another 1.4 s: each alone is under the 2-second deadline, together they are over it.
        var (result, _) = await Bounded(SendAsync(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1400), ct);
            var content = new StreamContent(new DelayedStream(Encoding.UTF8.GetBytes("{\"message\":\"recipient\"}"), TimeSpan.FromMilliseconds(1400)));
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = content };
        }, Settings(timeoutSeconds: 2)));

        AssertUnknown(result, "sms.timeout");
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, "sms.unreachable")]
    [InlineData(HttpRequestError.ConnectionError, "sms.unreachable")]
    [InlineData(HttpRequestError.ProxyTunnelError, "sms.unreachable")]
    [InlineData(HttpRequestError.SecureConnectionError, "sms.certificate_untrusted")]
    public async Task Connect_phase_failures_are_known(HttpRequestError error, string code)
    {
        var (result, _) = await SendAsync(Throw(new HttpRequestException(error, "connect failed MARKER", new System.Net.Sockets.SocketException())));

        AssertKnown(result, code);
    }

    [Fact]
    public async Task Blocked_addresses_and_tls_handshake_failures_are_known()
    {
        var (blocked, _) = await SendAsync(Throw(new HttpRequestException("x", new BlockedAddressException())));
        var (handshake, _) = await SendAsync(Throw(new HttpRequestException(HttpRequestError.SecureConnectionError, "x", new System.Security.Authentication.AuthenticationException("bad certificate"))));
        var (bare, _) = await SendAsync(Throw(new System.Security.Authentication.AuthenticationException("bad certificate")));

        AssertKnown(blocked, "sms.blocked_address");
        AssertKnown(handshake, "sms.certificate_untrusted");
        AssertKnown(bare, "sms.certificate_untrusted");
    }

    [Theory]
    [InlineData(HttpRequestError.Unknown)]
    [InlineData(HttpRequestError.ResponseEnded)]
    [InlineData(HttpRequestError.InvalidResponse)]
    public async Task A_tls_failure_outside_the_handshake_is_unknown(HttpRequestError error)
    {
        // For example a TLS failure while the answer is read: the request may already have been written.
        var (direct, _) = await SendAsync(Throw(new HttpRequestException(error, "x", new System.Security.Authentication.AuthenticationException("tls failed"))));
        var (nested, _) = await SendAsync(Throw(new HttpRequestException(error, "x", new IOException("read", new System.Security.Authentication.AuthenticationException("tls failed")))));

        AssertUnknown(direct, "sms.connection_lost");
        AssertUnknown(nested, "sms.connection_lost");
    }

    [Theory]
    [InlineData("https://localhost:9/v3")]
    [InlineData("https://169.254.169.254/latest")]
    public async Task The_real_handler_refuses_blocked_addresses_as_a_known_failure(string baseUrl)
    {
        var result = await Bounded(new HttpSmsSender().SendAsync(Settings(baseUrl: baseUrl, timeoutSeconds: 5), Request(), CancellationToken.None));

        AssertKnown(result, "sms.blocked_address");
    }

    [Theory]
    [InlineData("ftp://sms.send-host.example/v3")]
    [InlineData("http://sms.send-host.example/v3")]
    [InlineData("https://user:pw@sms.send-host.example/v3")]
    [InlineData("not a url")]
    public async Task An_address_that_is_not_plain_https_is_refused_before_anything_is_sent(string baseUrl)
    {
        var (result, handler) = await SendAsync(settings: Settings(baseUrl: baseUrl));

        AssertKnown(result, "sms.bad_request");
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpRequestError.Unknown)]
    [InlineData(HttpRequestError.ResponseEnded)]
    [InlineData(HttpRequestError.InvalidResponse)]
    [InlineData(HttpRequestError.HttpProtocolError)]
    public async Task Connection_lost_after_send_is_unknown(HttpRequestError error)
    {
        var (wrapped, _) = await SendAsync(Throw(new HttpRequestException(error, "lost MARKER", new IOException("reset"))));
        var (raw, _) = await SendAsync(Throw(new IOException("connection reset")));

        AssertUnknown(wrapped, "sms.connection_lost");
        AssertUnknown(raw, "sms.connection_lost");
    }

    [Fact]
    public async Task A_400_whose_body_breaks_off_is_unknown()
    {
        var (result, _) = await SendAsync(Answer(() => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StreamContent(new BreakingStream()) }));

        AssertUnknown(result, "sms.connection_lost");
    }

    [Fact]
    public async Task Caller_cancellation_propagates_from_the_sender()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        // No answer's headers yet: the caller's cancellation propagates.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bounded(SendAsync(StallBeforeHeaders, Settings(timeoutSeconds: 30), ct: cts.Token)));
    }

    [Fact]
    public async Task Caller_cancellation_after_a_2xx_still_returns_sent()
    {
        // Brevo accepted the message (201 headers); the body then blocks until the token is cancelled. "Sent" must not be lost.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var (result, handler) = await Bounded(SendAsync(Answer(() => Stalling(HttpStatusCode.Created)), Settings(timeoutSeconds: 30), ct: cts.Token));

        Assert.True(cts.IsCancellationRequested);
        Assert.True(result.Ok);
        Assert.False(result.OutcomeUnknown);
        Assert.Null(result.MessageId);
        Assert.Null(result.SmsParts);
        Assert.Single(handler.Requests);
    }

    private static readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> StallBeforeHeaders =
        async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json(HttpStatusCode.Created, "{}");
        };

    // ---------------------------------------------------------------- the service

    [Fact]
    public async Task A_valid_request_is_sent_once_with_the_decrypted_key_and_the_normalised_number()
    {
        var key = await CreateConnectionAsync();

        var (result, handler, protector) = await RunWithHandlerAsync(Request(key, recipient: "+357 99 000000", text: "Hi\r\nthere"));

        Assert.True(result.Ok);
        Assert.Equal("77", result.MessageId);
        Assert.Equal(1, result.SmsParts);
        Assert.Equal(1, protector.Unprotects);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(ApiKey, Assert.Single(request.Headers.GetValues("api-key")));
        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal(Digits, body.RootElement.GetProperty("recipient").GetString());
        Assert.Equal("Hi\nthere", body.RootElement.GetProperty("content").GetString());
        Assert.Equal(Tag, body.RootElement.GetProperty("tag").GetString());
        Assert.Equal("transactional", body.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Switch_off_never_decrypts_or_sends()
    {
        var key = await CreateConnectionAsync(allowSending: false);

        var (result, handler, protector) = await RunWithHandlerAsync(Request(key), WrongMasterKey());

        AssertKnown(result, "sms.sending_not_allowed");
        Assert.Empty(handler.Requests);
        Assert.Equal(0, protector.Unprotects);
    }

    [Fact]
    public async Task Marketing_connections_are_refused_before_decrypting()
    {
        var key = await CreateConnectionAsync(type: "marketing");

        var (result, handler, protector) = await RunWithHandlerAsync(Request(key), WrongMasterKey());

        AssertKnown(result, "sms.marketing_not_allowed");
        Assert.Empty(handler.Requests);
        Assert.Equal(0, protector.Unprotects);
    }

    [Theory]
    [InlineData("", "35799000000")]       // an empty list allows no country
    [InlineData("30,44", "35799000000")]  // no prefix matches
    [InlineData("3570", "35799000000")]   // the number must START with the whole prefix
    [InlineData("99", "35799000000")]     // a prefix inside the number is not a match
    [InlineData("1", "35799000000")]
    // Stored garbage (the save rules would refuse it) never allows a number: only 1..4 ASCII digits not starting with 0 count.
    [InlineData("35a,+357,0357,35700,, ,00357,٣٥٧", "35799000000")]
    [InlineData("35a,+357,0357,35700,, ,00357,٣٥٧", "35700123456")]
    [InlineData("+357", "35799000000")]
    [InlineData("35700", "35700123456")]
    [InlineData("3 5 7", "35799000000")]
    public async Task Countries_not_allowed_are_refused_before_decrypting(string prefixes, string recipient)
    {
        var key = await CreateConnectionAsync(prefixes: prefixes);

        var (result, handler, protector) = await RunWithHandlerAsync(Request(key, recipient: recipient), WrongMasterKey());

        AssertKnown(result, "sms.country_not_allowed");
        Assert.Empty(handler.Requests);
        Assert.Equal(0, protector.Unprotects);
    }

    [Theory]
    [InlineData("+357 99 000000")]
    [InlineData("0035799000000")]
    [InlineData("35799000000")]
    [InlineData("+(357) 99-000-000")]
    public async Task Allowed_prefixes_match_the_normalised_number(string recipient)
    {
        var key = await CreateConnectionAsync(prefixes: "44,357");

        var (result, handler, _) = await RunWithHandlerAsync(Request(key, recipient: recipient));

        Assert.True(result.Ok, result.Code);
        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(Digits, body.RootElement.GetProperty("recipient").GetString());
    }

    [Fact]
    public async Task A_host_not_allowed_is_refused_before_decrypting()
    {
        var key = await CreateConnectionAsync();

        var (result, handler, protector) = await RunWithHandlerAsync(Request(key), WrongMasterKey(), new SmsConnectionOptions { AllowedHosts = ["only-this-host.example"] });

        AssertKnown(result, "sms.host_not_allowed");
        Assert.Empty(handler.Requests);
        Assert.Equal(0, protector.Unprotects);

        var (allowed, allowedHandler, _) = await RunWithHandlerAsync(Request(key), options: new SmsConnectionOptions { AllowedHosts = [Host.ToUpperInvariant()] });
        Assert.True(allowed.Ok);
        Assert.Single(allowedHandler.Requests);
    }

    [Fact]
    public async Task An_invalid_stored_base_address_is_refused_before_decrypting()
    {
        var key = await CreateConnectionAsync(baseUrl: "http://sms.send-host.example/v3");

        var (result, handler, protector) = await RunWithHandlerAsync(Request(key), WrongMasterKey());

        AssertKnown(result, "sms.bad_request");
        Assert.Empty(handler.Requests);
        Assert.Equal(0, protector.Unprotects);
    }

    [Fact]
    public async Task Inactive_or_missing_connections_are_unavailable()
    {
        var inactive = await CreateConnectionAsync(active: false);

        var (off, handler1, protector1) = await RunWithHandlerAsync(Request(inactive));
        var (missing, handler2, _) = await RunWithHandlerAsync(Request("no-such-connection"));

        AssertKnown(off, "sms.connection_unavailable");
        AssertKnown(missing, "sms.connection_unavailable");
        Assert.Empty(handler1.Requests);
        Assert.Empty(handler2.Requests);
        Assert.Equal(0, protector1.Unprotects);
    }

    [Fact]
    public async Task An_unreadable_key_is_reported_only_after_every_other_check_passed()
    {
        var key = await CreateConnectionAsync();
        var noKey = await CreateConnectionAsync(withKey: false);

        var (wrong, handler1, protector1) = await RunWithHandlerAsync(Request(key), WrongMasterKey());
        var (none, handler2, _) = await RunWithHandlerAsync(Request(noKey));

        AssertKnown(wrong, "sms.secret_unreadable");
        AssertKnown(none, "sms.secret_unreadable");
        Assert.Equal(1, protector1.Unprotects);
        Assert.Empty(handler1.Requests);
        Assert.Empty(handler2.Requests);
    }

    [Fact]
    public async Task Unicode_needed_but_switched_off_is_refused_before_decrypting()
    {
        var off = await CreateConnectionAsync(unicode: false);
        var on = await CreateConnectionAsync(unicode: true);
        const string greek = "Γεια σου"; // lowercase Greek is not in the GSM set

        var (refused, handler1, protector1) = await RunWithHandlerAsync(Request(off, text: greek), WrongMasterKey());
        var (sent, handler2, _) = await RunWithHandlerAsync(Request(on, text: greek));
        var (gsm, handler3, _) = await RunWithHandlerAsync(Request(off, text: "ΓΔ é €"));

        AssertKnown(refused, "sms.text_needs_unicode");
        Assert.Empty(handler1.Requests);
        Assert.Equal(0, protector1.Unprotects);
        Assert.True(sent.Ok);
        Assert.Single(handler2.Requests);
        Assert.True(gsm.Ok);
        Assert.Single(handler3.Requests);
    }

    public static TheoryData<string> InvalidTexts =>
    [
        "", "   ", "a\u0000b", "a\tb", "a\rb", "a\ud800b", "a\u0085b", new string('a', 481),
        "Pay at bank.example‮moc.live", "a​b", "a﻿b", "a⁦b⁩", "a­b", // invisible / bidi characters can disguise a link
    ];

    [Theory]
    [MemberData(nameof(InvalidTexts), DisableDiscoveryEnumeration = true)] // a lone surrogate must not go through discovery serialisation
    public async Task Text_rules_are_checked_before_anything_else(string text)
    {
        var key = await CreateConnectionAsync(allowSending: false); // the request is checked even before the connection is read

        var (result, handler, protector) = await RunWithHandlerAsync(Request(key, text: text), WrongMasterKey());

        AssertKnown(result, "sms.text_invalid");
        Assert.Empty(handler.Requests);
        Assert.Equal(0, protector.Unprotects);
    }

    [Fact]
    public async Task A_480_character_text_is_sent()
    {
        var key = await CreateConnectionAsync();

        var (result, handler, _) = await RunWithHandlerAsync(Request(key, text: new string('a', 480)));

        Assert.True(result.Ok);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("verso_1")]
    [InlineData("abcdefghijklmnopqrstuvwxy")]
    [InlineData("verso-é")]
    public async Task Tag_rule_is_checked_before_anything_else(string tag)
    {
        var key = await CreateConnectionAsync(allowSending: false);

        var (result, handler, protector) = await RunWithHandlerAsync(Request(key, tag: tag), WrongMasterKey());

        AssertKnown(result, "sms.tag_invalid");
        Assert.Empty(handler.Requests);
        Assert.Equal(0, protector.Unprotects);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1234567")]
    [InlineData("+0357 99 000000")]
    [InlineData("35799000000x")]
    [InlineData("+3579900000012345")]
    [InlineData("357\n99000000")]
    public async Task Recipient_format_is_checked_before_anything_else(string recipient)
    {
        var key = await CreateConnectionAsync(allowSending: false);

        var (result, handler, protector) = await RunWithHandlerAsync(Request(key, recipient: recipient), WrongMasterKey());

        AssertKnown(result, "sms.recipient_invalid");
        Assert.Empty(handler.Requests);
        Assert.Equal(0, protector.Unprotects);
    }

    [Fact]
    public async Task Secret_number_text_never_appear_in_results_messages_or_ToString()
    {
        const string number = "35799123456";
        const string text = "SECRET-TEXT-MARKER balance 1234";
        var key = await CreateConnectionAsync();
        var markers = new[] { number, text, "SECRET-TEXT-MARKER", ApiKey, Tag, "MARKER" };
        var echo = $"{{\"message\":\"bad recipient {number} {text} {ApiKey} {Tag}\",\"code\":\"duplicate_request\"}}";

        var results = new List<SmsSendResult>();
        foreach (var respond in new[]
        {
            Answer(() => Json(HttpStatusCode.Created, $"{{\"messageId\":9,\"echo\":\"{number} {text}\"}}")),
            Answer(() => Json(HttpStatusCode.BadRequest, echo)),
            Answer(() => Json(HttpStatusCode.BadRequest, $"{{\"message\":\"recipient {number} MARKER\"}}")),
            Answer(() => Json(HttpStatusCode.InternalServerError, echo)),
            Answer(() => Json(HttpStatusCode.Unauthorized, echo)),
            Throw(new HttpRequestException(HttpRequestError.ConnectionError, $"MARKER {number} {text}")),
            Throw(new IOException($"MARKER {number} {text} {ApiKey}")),
        })
        {
            results.Add((await RunWithHandlerAsync(new SmsSendRequest(key, number, text, Tag), respond: respond)).Result);
        }

        results.Add(await RunAsync(new SmsSendRequest(key, number, text, Tag), new ThrowingSender(new InvalidOperationException($"MARKER {number} {text} {ApiKey}"))));
        results.Add(await RunAsync(new SmsSendRequest(key, number + "x", text, Tag), new ThrowingSender(new InvalidOperationException())));
        Assert.Contains(results, r => r.Ok);
        Assert.Contains(results, r => r.OutcomeUnknown);
        Assert.Contains(results, r => r is { Ok: false, OutcomeUnknown: false });

        foreach (var result in results)
        {
            var shown = JsonSerializer.Serialize(result) + result + result.Message;
            foreach (var marker in markers)
            {
                Assert.DoesNotContain(marker, shown, StringComparison.Ordinal);
            }
        }

        var request = new SmsSendRequest(key, number, text, Tag).ToString();
        var settings = new SmsConnectionSettings(BaseUrl, "TestSender", "transactional", false, "Org", ApiKey, TimeSpan.FromSeconds(5)).ToString();
        foreach (var marker in markers)
        {
            Assert.DoesNotContain(marker, request, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, settings, StringComparison.Ordinal);
        }

        Assert.Contains(key, request, StringComparison.Ordinal); // the connection key is not personal and helps support
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var key = await CreateConnectionAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var handler = new FakeHandler(StallBeforeHeaders);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bounded(RunAsync(Request(key), new HttpSmsSender(() => handler), ct: cts.Token)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(Request(key), new ThrowingSender(new OperationCanceledException()), ct: new CancellationToken(true)));
    }

    [Fact]
    public async Task Unexpected_sender_exception_is_unknown_unreachable_without_its_text()
    {
        var key = await CreateConnectionAsync();

        var result = await RunAsync(Request(key), new ThrowingSender(new InvalidOperationException("boom MARKER")));
        var timedOut = await RunAsync(Request(key), new ThrowingSender(new TaskCanceledException("inner timeout")));

        AssertUnknown(result, "sms.unreachable");
        Assert.DoesNotContain("boom", result.Message, StringComparison.Ordinal);
        AssertUnknown(timedOut, "sms.timeout");
    }

    [Fact]
    public void Every_send_code_has_its_own_known_wording()
    {
        foreach (var code in new[]
        {
            "sms.sending_not_allowed", "sms.marketing_not_allowed", "sms.country_not_allowed", "sms.recipient_invalid", "sms.text_invalid",
            "sms.text_needs_unicode", "sms.tag_invalid", "sms.bad_request", "sms.duplicate_request", "sms.connection_lost", "sms.unexpected_answer",
        })
        {
            Assert.NotEqual("The test could not be run.", SmsProbeMessages.For(code));
        }

        Assert.Equal("Sending is switched off for this connection. An administrator can switch it on in Configuration, Connections.", SmsProbeMessages.For("sms.sending_not_allowed"));
        Assert.Equal("Marketing connections can't be used in workflows.", SmsProbeMessages.For("sms.marketing_not_allowed"));
        Assert.Equal("The number's country is not one the administrator allowed for this connection.", SmsProbeMessages.For("sms.country_not_allowed"));
        Assert.Equal("The number is not a valid international phone number.", SmsProbeMessages.For("sms.recipient_invalid"));
        Assert.Equal("The message text is empty, too long or contains characters that can't be sent.", SmsProbeMessages.For("sms.text_invalid"));
        Assert.Equal("The text needs Unicode characters but this connection has Unicode switched off. An administrator can switch it on.", SmsProbeMessages.For("sms.text_needs_unicode"));
        Assert.Equal(UnknownWording, SmsProbeMessages.For("sms.duplicate_request"));
        Assert.Equal(UnknownWording, SmsSendResult.Failure("sms.server_error", outcomeUnknown: true).Message);
        Assert.Equal(SmsProbeMessages.For("sms.server_error"), SmsSendResult.Failure("sms.server_error", outcomeUnknown: false).Message);
    }

    // ---------------------------------------------------------------- registration

    [Fact]
    public void The_api_host_resolves_neither_ISmsSend_nor_ISmsSender()
    {
        using var scope = fixture.Factory.Services.CreateScope();

        Assert.Null(scope.ServiceProvider.GetService<ISmsSend>());
        Assert.Null(scope.ServiceProvider.GetService<ISmsSender>());
        Assert.Null(scope.ServiceProvider.GetService<ISmsProbe>());
    }

    [Fact]
    public void The_worker_registration_adds_the_sender_as_singleton_and_the_service_scoped()
    {
        var services = new ServiceCollection();
        services.AddVersoSmsConnections(new ConfigurationBuilder().Build());

        var sender = Assert.Single(services, d => d.ServiceType == typeof(ISmsSender));
        Assert.Equal(ServiceLifetime.Singleton, sender.Lifetime);
        Assert.IsType<HttpSmsSender>(sender.ImplementationInstance);
        var send = Assert.Single(services, d => d.ServiceType == typeof(ISmsSend));
        Assert.Equal(ServiceLifetime.Scoped, send.Lifetime);
        Assert.Equal(typeof(SmsSendService), send.ImplementationType);
    }

    [Fact]
    public void Only_ISmsSend_is_public_so_nothing_can_send_around_its_checks()
    {
        Assert.True(typeof(ISmsSend).IsPublic);
        Assert.False(typeof(ISmsSender).IsPublic);
        Assert.False(typeof(SmsSendService).IsPublic);
        Assert.False(typeof(HttpSmsSender).IsPublic);
    }
}
