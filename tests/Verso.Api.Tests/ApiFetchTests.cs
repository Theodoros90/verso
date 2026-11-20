using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Verso.Application;
using Verso.Application.Connections;
using Verso.Application.Mail;
using Verso.Domain.Connections;
using Verso.Infrastructure;
using Verso.Infrastructure.Connections;
using Verso.Infrastructure.Security;

namespace Verso.Api.Tests;

/// <summary>
/// The Fetch from API service and its HTTP sender (Phase 3): one GET to the connection's host, the path and query values encoded, the host never moved,
/// the answer read up to the limit and parsed as JSON, Verso's own codes and messages, and the secret decrypted only after the host checks.
/// All HTTP is faked with a message handler: no test touches the network.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ApiFetchTests(MigratedDatabaseFixture fixture)
{
    private const string Host = "api.fetch-host.example";
    private const string BaseUrl = $"https://{Host}/v1";
    private const string Secret = "fetch-s3cret-marker";
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

    private sealed class RecordingSender(Func<ApiFetchResult>? answer = null) : IApiFetchSender
    {
        public List<(ApiConnectionSettings Settings, ApiFetchRequest Request)> Seen { get; } = [];

        public Task<ApiFetchResult> SendAsync(ApiConnectionSettings settings, ApiFetchRequest request, CancellationToken ct)
        {
            Seen.Add((settings, request));
            return Task.FromResult(answer?.Invoke() ?? ApiFetchResult.Success(204, null, 0, 1));
        }
    }

    private sealed class ThrowingSender(Exception ex) : IApiFetchSender
    {
        public Task<ApiFetchResult> SendAsync(ApiConnectionSettings settings, ApiFetchRequest request, CancellationToken ct) => throw ex;
    }

    /// <summary>Hands out bytes and throws if anyone reads past <c>limit</c> bytes: proves the answer is never read beyond the limit + 1.</summary>
    private sealed class TrapStream(long length, long limit) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _position);
            if (_position + n > limit)
            {
                throw new InvalidOperationException("read beyond the limit");
            }

            Array.Fill(buffer, (byte)'1', offset, n);
            _position += n;
            return n;
        }
    }

    private static string UniqueKey() => $"fetch-{Guid.NewGuid():N}"[..20];

    private static HttpResponseMessage Json(string body = "{\"rate\":1.08}", HttpStatusCode status = HttpStatusCode.OK, string type = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, type) };

    private async Task<string> CreateConnectionAsync(
        string authType = "None", string authName = "", string? secret = null, string baseUrl = BaseUrl, string tokenUrl = "", bool active = true)
    {
        var key = UniqueKey();
        var now = DateTimeOffset.UtcNow;
        var connection = ApiConnection.Create(key, "Fetch " + key, baseUrl, "/status", authType, authName, tokenUrl, "", 15, "tests", now);
        if (secret is not null)
        {
            connection.SetSecret(Protector.Protect(secret, ApiConnectionContexts.For(key)), "tests", now);
        }

        if (!active)
        {
            connection.Deactivate("tests", now);
        }

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IVersoDbContext>();
        db.ApiConnections.Add(connection);
        await db.SaveChangesAsync(CancellationToken.None);
        return key;
    }

    private static ApiFetchRequest Request(string key, string path = "/rates", IReadOnlyList<KeyValuePair<string, string>>? query = null, int max = 64 * 1024, int timeoutSeconds = 15) =>
        new(key, path, query ?? [], max, TimeSpan.FromSeconds(timeoutSeconds));

    private async Task<ApiFetchResult> RunAsync(ApiFetchRequest request, IApiFetchSender sender, ApiConnectionOptions? options = null, ISecretProtector? protector = null, CancellationToken ct = default)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var service = new ApiFetchService(scope.ServiceProvider.GetRequiredService<IVersoDbContext>(), protector ?? Protector, sender, options ?? new ApiConnectionOptions());
        return await service.RunAsync(request, ct);
    }

    private async Task<(ApiFetchResult Result, FakeHandler Handler)> FetchAsync(
        ApiFetchRequest request, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? respond = null, ApiConnectionOptions? options = null, ISecretProtector? protector = null, CancellationToken ct = default)
    {
        var handler = new FakeHandler(respond ?? ((_, _) => Task.FromResult(Json())));
        var result = await RunAsync(request, new HttpApiFetchSender(() => handler), options, protector, ct);
        return (result, handler);
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Answer(Func<HttpResponseMessage> make) => (_, _) => Task.FromResult(make());

    [Fact]
    public async Task Sends_one_GET_to_base_plus_path_with_encoded_query()
    {
        var key = await CreateConnectionAsync();

        var (result, handler) = await FetchAsync(Request(key, "/rates/EUR", [new("symbols", "USD,GBP"), new("as of", "2026-11-20")]));

        Assert.True(result.Ok);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"{BaseUrl}/rates/EUR?symbols=USD%2CGBP&as%20of=2026-11-20", handler.Uris[0]);
        Assert.Null(request.Content);
        Assert.Equal("application/json", Assert.Single(request.Headers.Accept).ToString());
        Assert.Equal("Verso-fetch/1.0", request.Headers.UserAgent.ToString());
        Assert.Null(request.Headers.Authorization);
        Assert.Equal("Accept,User-Agent", string.Join(",", request.Headers.Select(h => h.Key).Order(StringComparer.Ordinal)));
    }

    [Fact]
    public async Task Auth_api_key_in_a_header_is_sent_under_its_name()
    {
        var key = await CreateConnectionAsync("ApiKeyHeader", "X-API-Key", "key-123");

        var (result, handler) = await FetchAsync(Request(key));

        Assert.True(result.Ok);
        Assert.Equal("key-123", Assert.Single(handler.Requests[0].Headers.GetValues("X-API-Key")));
        Assert.Equal($"{BaseUrl}/rates", handler.Uris[0]);
    }

    [Fact]
    public async Task Auth_api_key_in_the_query_is_appended_last_and_encoded()
    {
        var key = await CreateConnectionAsync("ApiKeyQuery", "api_key", "k e&y=1");

        var (_, withQuery) = await FetchAsync(Request(key, query: [new("base", "EUR")]));
        var (_, noQuery) = await FetchAsync(Request(key));

        Assert.Equal($"{BaseUrl}/rates?base=EUR&api_key=k%20e%26y%3D1", withQuery.Uris[0]);
        Assert.Equal($"{BaseUrl}/rates?api_key=k%20e%26y%3D1", noQuery.Uris[0]);
    }

    [Fact]
    public async Task Auth_a_query_value_named_like_the_api_key_is_refused()
    {
        var key = await CreateConnectionAsync("ApiKeyQuery", "api_key", "k");

        var (result, handler) = await FetchAsync(Request(key, query: [new("API_KEY", "mine")]));

        Assert.Equal("api.bad_request", result.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Auth_bearer_and_basic_set_the_authorization_header()
    {
        var (_, bearer) = await FetchAsync(Request(await CreateConnectionAsync("Bearer", secret: "tok-1")));
        var (_, basic) = await FetchAsync(Request(await CreateConnectionAsync("Basic", "alice", "pa:ss")));

        Assert.Equal("Bearer tok-1", bearer.Requests[0].Headers.Authorization!.ToString());
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:pa:ss")), basic.Requests[0].Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Auth_oauth_asks_the_token_endpoint_first_then_sends_the_bearer_token()
    {
        var key = await CreateConnectionAsync("OAuthClientCredentials", "client-1", "client-secret", tokenUrl: "https://auth.fetch-host.example/token");

        var (result, handler) = await FetchAsync(Request(key), (request, _) => Task.FromResult(request.Method == HttpMethod.Post
            ? Json("{\"access_token\":\"the-token\",\"token_type\":\"Bearer\"}")
            : Json()));

        Assert.True(result.Found);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://auth.fetch-host.example/token", handler.Uris[0]);
        Assert.Contains("grant_type=client_credentials", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Equal("Bearer the-token", handler.Requests[1].Headers.Authorization!.ToString());
        Assert.DoesNotContain("the-token", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Auth_oauth_token_endpoint_failing_is_token_failed_and_the_api_is_never_called()
    {
        var key = await CreateConnectionAsync("OAuthClientCredentials", "c", "s", tokenUrl: "https://auth.fetch-host.example/token");

        var (result, handler) = await FetchAsync(Request(key), Answer(() => new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        Assert.Equal("api.token_failed", result.Code);
        Assert.False(result.Retryable);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OAuth_token_host_must_be_allowed_too()
    {
        var key = await CreateConnectionAsync("OAuthClientCredentials", "c", "s", tokenUrl: "https://auth.other-host.example/token");

        var (result, handler) = await FetchAsync(Request(key), options: new ApiConnectionOptions { AllowedHosts = [Host.ToUpperInvariant()] });

        Assert.Equal("api.host_not_allowed", result.Code);
        Assert.False(result.Retryable);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Hostile_values_stay_in_one_encoded_value()
    {
        var key = await CreateConnectionAsync();
        // The path pieces arrive already encoded (Task 4 encodes each resolved value with Uri.EscapeDataString).
        var path = "/accounts/" + Uri.EscapeDataString("x?y=1&z") + "/" + Uri.EscapeDataString("../admin") + "/" + Uri.EscapeDataString("%2e%2e") + "/" + Uri.EscapeDataString("a#b");

        var (result, handler) = await FetchAsync(Request(key, path, [new("q", "x?y=1&z"), new("p", "../"), new("e", "%2e%2e"), new("h", "a#b"), new("a&b=c", "d")]));

        Assert.True(result.Ok);
        Assert.Single(handler.Requests);
        var uri = handler.Requests[0].RequestUri!;
        Assert.Equal(Host, uri.Host);
        Assert.Equal("/v1/accounts/x%3Fy%3D1%26z/..%2Fadmin/%252e%252e/a%23b", uri.AbsolutePath);
        Assert.Equal("?q=x%3Fy%3D1%26z&p=..%2F&e=%252e%252e&h=a%23b&a%26b%3Dc=d", uri.Query);
        Assert.Equal("", uri.Fragment);
    }

    [Theory]
    [InlineData("/..%2f..")]
    [InlineData("/@evil.test/x")]
    [InlineData("/x@evil.test")]
    [InlineData("//evil.test")]
    [InlineData("/%2f%2fevil.test")]
    [InlineData("/:443@evil.test")]
    public async Task Host_cannot_move_through_a_hostile_path(string path)
    {
        var key = await CreateConnectionAsync();

        var (result, handler) = await FetchAsync(Request(key, path));

        if (result.Code == "api.bad_request")
        {
            Assert.Empty(handler.Requests);
        }
        else
        {
            var uri = Assert.Single(handler.Requests).RequestUri!;
            Assert.Equal(Host, uri.Host);
            Assert.Equal(443, uri.Port);
            Assert.Equal("", uri.UserInfo);
        }
    }

    [Theory]
    [InlineData("https://api.fetch-host.example", "//evil.test/x")]
    [InlineData("https://api.fetch-host.example", "/@evil.test/x")]
    [InlineData("https://api.fetch-host.example/v1/", "/x?y#z")]
    public async Task Host_cannot_move_even_when_the_sender_is_called_directly(string baseUrl, string path)
    {
        var handler = new FakeHandler(Answer(() => Json()));
        var settings = new ApiConnectionSettings(baseUrl, "/status", "None", "", "", "", "", TimeSpan.FromSeconds(5));

        var result = await new HttpApiFetchSender(() => handler).SendAsync(settings, Request("k", path), CancellationToken.None);

        foreach (var request in handler.Requests)
        {
            Assert.Equal(Host, request.RequestUri!.Host);
            Assert.Equal("", request.RequestUri.UserInfo);
            Assert.Equal("", request.RequestUri.Fragment);
        }

        Assert.True(result.Code == "api.bad_request" ? handler.Requests.Count == 0 : handler.Requests.Count == 1);
    }

    [Fact]
    public async Task Found_returns_json_body()
    {
        var key = await CreateConnectionAsync();

        var (result, _) = await FetchAsync(Request(key), Answer(() => Json("{\"rates\":{\"USD\":1.08},\"list\":[1,2]}")));
        var (array, _) = await FetchAsync(Request(key), Answer(() => Json("[1,2,3]")));
        var (scalar, _) = await FetchAsync(Request(key), Answer(() => Json("\"text\"")));

        Assert.True(result.Found);
        Assert.Equal(200, result.HttpStatus);
        Assert.Equal(1.08m, result.Body!.Value.GetProperty("rates").GetProperty("USD").GetDecimal());
        Assert.Equal("{\"rates\":{\"USD\":1.08},\"list\":[1,2]}".Length, result.ResponseBytes);
        Assert.True(result.DurationMs >= 0);
        Assert.Null(result.Message);
        Assert.Equal(JsonValueKind.Array, array.Body!.Value.ValueKind);
        Assert.Equal("text", scalar.Body!.Value.GetString());
    }

    [Fact]
    public async Task Not_found_is_a_success_without_body()
    {
        var key = await CreateConnectionAsync();

        var (notFound, _) = await FetchAsync(Request(key), Answer(() => Json("{\"error\":\"no such account\"}", HttpStatusCode.NotFound)));
        var (noContent, _) = await FetchAsync(Request(key), Answer(() => new HttpResponseMessage(HttpStatusCode.NoContent)));
        var (empty, _) = await FetchAsync(Request(key), Answer(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }));
        var (emptyUnknownLength, _) = await FetchAsync(Request(key), Answer(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new TrapStream(0, 10)) }));
        var (emptyHtml, _) = await FetchAsync(Request(key), Answer(() => Json("", type: "text/html")));

        foreach (var (result, status) in new[] { (notFound, 404), (noContent, 204), (empty, 200), (emptyUnknownLength, 200), (emptyHtml, 200) })
        {
            Assert.True(result.Ok);
            Assert.False(result.Found);
            Assert.Null(result.Body);
            Assert.Equal(status, result.HttpStatus);
        }
    }

    [Fact]
    public async Task Redirect_is_not_followed()
    {
        var key = await CreateConnectionAsync();

        var (result, handler) = await FetchAsync(Request(key), Answer(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://evil.test/steal");
            return response;
        }));

        Assert.Equal("api.redirect_not_followed", result.Code);
        Assert.False(result.Retryable);
        Assert.Equal(302, result.HttpStatus);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(401, "api.unauthorized", false)]
    [InlineData(403, "api.unauthorized", false)]
    [InlineData(400, "api.client_error", false)]
    [InlineData(422, "api.client_error", false)]
    [InlineData(429, "api.rate_limited", true)]
    [InlineData(500, "api.server_error", true)]
    [InlineData(503, "api.server_error", true)]
    public async Task Status_codes_map_and_retry_flags(int status, string code, bool retryable)
    {
        var key = await CreateConnectionAsync();

        var (result, handler) = await FetchAsync(Request(key), Answer(() => Json("{\"detail\":\"secret-error-detail\"}", (HttpStatusCode)status)));

        Assert.False(result.Ok);
        Assert.Equal(code, result.Code);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal(status, result.HttpStatus);
        Assert.Null(result.Body);
        Assert.Equal(ApiFetchMessages.For(code), result.Message);
        Assert.DoesNotContain("secret-error-detail", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Timeout_is_retryable_api_timeout()
    {
        var key = await CreateConnectionAsync();

        var (result, _) = await FetchAsync(Request(key, timeoutSeconds: 1), async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct); // HttpClient.Timeout (1 s) cancels this
            return Json();
        });

        Assert.Equal("api.timeout", result.Code);
        Assert.True(result.Retryable);
        Assert.Equal(ApiFetchMessages.For("api.timeout"), result.Message);
    }

    [Fact]
    public async Task Network_problems_map_to_codes_and_retry_flags()
    {
        var key = await CreateConnectionAsync();

        var (unreachable, _) = await FetchAsync(Request(key), (_, _) => throw new HttpRequestException("secret-network-text", new System.Net.Sockets.SocketException()));
        var (certificate, _) = await FetchAsync(Request(key), (_, _) => throw new HttpRequestException("x", new System.Security.Authentication.AuthenticationException("bad")));
        var (blocked, _) = await FetchAsync(Request(key), (_, _) => throw new HttpRequestException("x", new BlockedAddressException()));
        var (io, _) = await FetchAsync(Request(key), (_, _) => throw new IOException("reset"));

        Assert.Equal(("api.unreachable", true), (unreachable.Code, unreachable.Retryable));
        Assert.Equal(("api.certificate_untrusted", false), (certificate.Code, certificate.Retryable));
        Assert.Equal(("api.blocked_address", false), (blocked.Code, blocked.Retryable));
        Assert.Equal(("api.unreachable", true), (io.Code, io.Retryable));
        Assert.DoesNotContain("secret-network-text", JsonSerializer.Serialize(unreachable), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Not_json_content_type_and_malformed_json_fail_api_not_json()
    {
        var key = await CreateConnectionAsync();

        var (html, _) = await FetchAsync(Request(key), Answer(() => Json("<html>secret-html</html>", type: "text/html")));
        var (plain, _) = await FetchAsync(Request(key), Answer(() => Json("{\"a\":1}", type: "text/plain")));
        var (noType, _) = await FetchAsync(Request(key), Answer(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("{\"a\":1}"u8.ToArray()) }));
        var (malformed, _) = await FetchAsync(Request(key), Answer(() => Json("{\"a\":")));
        var (bigHtml, _) = await FetchAsync(Request(key, max: 1024), Answer(() => Json(new string('x', 5000), type: "text/html")));

        foreach (var result in new[] { html, plain, noType, malformed, bigHtml })
        {
            Assert.Equal("api.not_json", result.Code);
            Assert.False(result.Retryable);
            Assert.Null(result.Body);
            Assert.Equal("The API answered with something that is not JSON.", result.Message);
            Assert.DoesNotContain("secret-html", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("APPLICATION/JSON")]
    [InlineData("application/problem+json")]
    [InlineData("application/vnd.api+JSON; charset=UTF-8")]
    public async Task Content_type_parameters_and_plus_json_are_accepted(string contentType)
    {
        var key = await CreateConnectionAsync();

        var (result, _) = await FetchAsync(Request(key), Answer(() =>
        {
            var content = new ByteArrayContent("{\"ok\":true}"u8.ToArray());
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));

        Assert.True(result.Found, result.Code);
        Assert.True(result.Body!.Value.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Over_the_limit_fails_response_too_large()
    {
        var key = await CreateConnectionAsync();
        var exactly = "\"" + new string('a', 1022) + "\""; // 1024 bytes
        var oneMore = "\"" + new string('a', 1023) + "\""; // 1025 bytes

        var (atLimit, _) = await FetchAsync(Request(key, max: 1024), Answer(() => Json(exactly)));
        var (over, _) = await FetchAsync(Request(key, max: 1024), Answer(() => Json(oneMore)));
        var (declared, _) = await FetchAsync(Request(key, max: 1024), Answer(() =>
        {
            var content = new StringContent(oneMore, Encoding.UTF8, "application/json");
            content.Headers.ContentLength = 1025;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));

        Assert.True(atLimit.Found);
        Assert.Equal(1024, atLimit.ResponseBytes);
        foreach (var result in new[] { over, declared })
        {
            Assert.Equal("api.response_too_large", result.Code);
            Assert.False(result.Retryable);
            Assert.Null(result.Body);
            Assert.Equal("The API's answer is larger than the step allows. Raise the limit or ask the API for less.", result.Message);
        }
    }

    [Fact]
    public async Task Response_limit_is_applied_before_parsing_a_huge_body()
    {
        var key = await CreateConnectionAsync();

        var (result, _) = await FetchAsync(Request(key, max: 2048), Answer(() =>
        {
            var content = new StreamContent(new TrapStream(10_000_000, 2049)); // throws if read past limit + 1
            content.Headers.ContentType = new("application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));

        Assert.Equal("api.response_too_large", result.Code);
    }

    [Fact]
    public async Task Blocked_host_not_allowed_and_unreadable_secret_send_nothing()
    {
        // A blocked address: the real handler refuses it when connecting (nothing leaves the machine).
        foreach (var blockedBase in new[] { "https://localhost:9", "https://169.254.169.254/latest" })
        {
            var blockedKey = await CreateConnectionAsync(baseUrl: blockedBase);
            var blocked = await RunAsync(Request(blockedKey, timeoutSeconds: 5), new HttpApiFetchSender());
            Assert.Equal("api.blocked_address", blocked.Code);
            Assert.False(blocked.Retryable);
        }

        // A host not allowed, with a secret the Worker can't read: the host check comes first and nothing is decrypted or sent.
        var key = await CreateConnectionAsync("Bearer", secret: Secret);
        var wrongKey = new AesGcmSecretProtector(AesGcmSecretProtector.GenerateKey());
        var (notAllowed, handler1) = await FetchAsync(Request(key), options: new ApiConnectionOptions { AllowedHosts = ["only-this-host.example"] }, protector: wrongKey);
        Assert.Equal("api.host_not_allowed", notAllowed.Code);
        Assert.False(notAllowed.Retryable);
        Assert.Empty(handler1.Requests);

        // The host allowed (any case), the secret unreadable: nothing sent.
        var (unreadable, handler2) = await FetchAsync(Request(key), options: new ApiConnectionOptions { AllowedHosts = [Host.ToUpperInvariant()] }, protector: wrongKey);
        Assert.Equal("api.secret_unreadable", unreadable.Code);
        Assert.False(unreadable.Retryable);
        Assert.Empty(handler2.Requests);

        // And allowed with the right key: sent.
        var (allowed, handler3) = await FetchAsync(Request(key), options: new ApiConnectionOptions { AllowedHosts = [Host.ToUpperInvariant()] });
        Assert.True(allowed.Found);
        Assert.Single(handler3.Requests);
    }

    [Fact]
    public async Task Inactive_or_missing_connection()
    {
        var inactive = await CreateConnectionAsync(active: false);

        var (off, handler1) = await FetchAsync(Request(inactive));
        var (missing, handler2) = await FetchAsync(Request("no-such-connection"));

        foreach (var (result, handler) in new[] { (off, handler1), (missing, handler2) })
        {
            Assert.Equal("api.connection_unavailable", result.Code);
            Assert.False(result.Retryable);
            Assert.Empty(handler.Requests);
        }
    }

    [Theory]
    [InlineData("//evil")]
    [InlineData("/a b")]
    [InlineData("/a?b")]
    [InlineData("/a#b")]
    [InlineData("rates")]
    [InlineData("")]
    [InlineData("/a\\b")]
    [InlineData("/v1/../admin")]
    [InlineData("/./x")]
    [InlineData("/x/%2e%2E")]
    [InlineData("/x/.%2e/y")]
    public async Task Bad_path_is_refused_before_anything(string path)
    {
        // A connection that does not exist: refusing the path comes first, so the answer is bad_request, not connection_unavailable.
        var sender = new RecordingSender();

        var result = await RunAsync(Request("no-such-connection", path), sender);

        Assert.Equal("api.bad_request", result.Code);
        Assert.False(result.Retryable);
        Assert.Equal("The request path or query is not valid.", result.Message);
        Assert.Empty(sender.Seen);
    }

    [Fact]
    public async Task Too_many_or_unnamed_query_values_are_refused()
    {
        var sender = new RecordingSender();
        var many = Enumerable.Range(0, 21).Select(i => new KeyValuePair<string, string>("p" + i, "v")).ToList();

        var tooMany = await RunAsync(Request("no-such-connection", query: many), sender);
        var unnamed = await RunAsync(Request("no-such-connection", query: [new("", "v")]), sender);

        Assert.Equal("api.bad_request", tooMany.Code);
        Assert.Equal("api.bad_request", unnamed.Code);
        Assert.Empty(sender.Seen);
    }

    [Theory]
    [InlineData(0, 0, 1024, 1)]
    [InlineData(-5, -5, 1024, 1)]
    [InlineData(10_000_000, 600, 256 * 1024, 60)]
    [InlineData(64 * 1024, 15, 64 * 1024, 15)]
    public async Task Limits_are_clamped(int maxBytes, int timeoutSeconds, int expectedBytes, int expectedSeconds)
    {
        var key = await CreateConnectionAsync();
        var sender = new RecordingSender();

        await RunAsync(Request(key, max: maxBytes, timeoutSeconds: timeoutSeconds), sender);

        var (settings, request) = Assert.Single(sender.Seen);
        Assert.Equal(expectedBytes, request.MaxResponseBytes);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), request.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), settings.Timeout);
    }

    [Fact]
    public async Task Secret_and_address_never_appear_in_results()
    {
        const string valueMarker = "value-marker-4711";
        var key = await CreateConnectionAsync("ApiKeyQuery", "api_key", Secret);
        var request = Request(key, "/accounts/" + valueMarker, [new("holder", valueMarker)], max: 1024, timeoutSeconds: 1);
        var answers = new Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[]
        {
            Answer(() => Json("{\"rate\":1}")),
            Answer(() => Json("{}", HttpStatusCode.NotFound)),
            Answer(() => new HttpResponseMessage(HttpStatusCode.Unauthorized)),
            Answer(() => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri($"{BaseUrl}/{valueMarker}") } }),
            Answer(() => new HttpResponseMessage(HttpStatusCode.InternalServerError)),
            Answer(() => Json("<p/>", type: "text/html")),
            Answer(() => Json("\"" + new string('a', 2000) + "\"")),
            (_, _) => throw new HttpRequestException($"failed {BaseUrl}?api_key={Secret}"),
            async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json(); },
        };

        foreach (var answer in answers)
        {
            var (result, handler) = await FetchAsync(request, answer);
            Assert.Contains(Secret, handler.Uris[0], StringComparison.Ordinal); // the key did go on the wire...
            var text = JsonSerializer.Serialize(result with { Body = null }) + result.Message + result.Code;
            foreach (var marker in new[] { Secret, valueMarker, Host, "api_key", "/accounts" })
            {
                Assert.DoesNotContain(marker, text, StringComparison.OrdinalIgnoreCase); // ...but never into a result or message
            }
        }

        var thrown = await RunAsync(request, new ThrowingSender(new InvalidOperationException($"{BaseUrl}?api_key={Secret}")));
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(thrown), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var key = await CreateConnectionAsync();
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FetchAsync(Request(key), async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return Json();
        }, ct: cts.Token));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(Request(key), new ThrowingSender(new OperationCanceledException(cancelled.Token)), ct: cancelled.Token));
    }

    [Fact]
    public async Task Unexpected_sender_exception_becomes_retryable_unreachable_without_its_text()
    {
        var key = await CreateConnectionAsync();

        var result = await RunAsync(Request(key), new ThrowingSender(new ArgumentException("secret-exception-text")));

        Assert.Equal("api.unreachable", result.Code);
        Assert.True(result.Retryable);
        Assert.Equal(ApiFetchMessages.For("api.unreachable"), result.Message);
        Assert.DoesNotContain("secret-exception-text", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_service_hands_the_sender_the_connection_with_its_secret_in_memory()
    {
        var key = await CreateConnectionAsync("Bearer", secret: Secret);
        var sender = new RecordingSender();

        await RunAsync(Request(key, "/x", [new("a", "b")]), sender);

        var (settings, request) = Assert.Single(sender.Seen);
        Assert.Equal(BaseUrl, settings.BaseUrl);
        Assert.Equal("Bearer", settings.AuthType);
        Assert.Equal(Secret, settings.Secret);
        Assert.Equal("/x", request.Path);
        Assert.Equal(new KeyValuePair<string, string>("a", "b"), Assert.Single(request.Query));
    }

    [Fact]
    public void Messages_are_verso_own_and_the_probe_texts_are_unchanged()
    {
        Assert.Equal("The API answered with something that is not JSON.", ApiFetchMessages.For("api.not_json"));
        Assert.Equal("The API's answer is larger than the step allows. Raise the limit or ask the API for less.", ApiFetchMessages.For("api.response_too_large"));
        Assert.Equal("The request path or query is not valid.", ApiFetchMessages.For("api.bad_request"));
        foreach (var code in new[] { "api.unauthorized", "api.rate_limited", "api.redirect_not_followed", "api.server_error", "api.timeout", "api.unreachable", "api.certificate_untrusted", "api.blocked_address", "api.host_not_allowed", "api.token_failed", "api.secret_unreadable", "api.connection_unavailable" })
        {
            Assert.Equal(ApiProbeMessages.For(code), ApiFetchMessages.For(code));
        }

        Assert.Equal("The test could not be run.", ApiProbeMessages.For("api.not_json")); // the probe's own table did not grow
    }

    [Fact]
    public void Only_timeouts_unreachable_server_errors_and_rate_limits_are_retryable()
    {
        foreach (var code in new[] { "api.timeout", "api.unreachable", "api.server_error", "api.rate_limited" })
        {
            Assert.True(ApiFetchResult.IsRetryable(code), code);
        }

        foreach (var code in new[] { "api.unauthorized", "api.client_error", "api.redirect_not_followed", "api.certificate_untrusted", "api.blocked_address", "api.host_not_allowed", "api.token_failed", "api.secret_unreadable", "api.connection_unavailable", "api.not_json", "api.response_too_large", "api.bad_request" })
        {
            Assert.False(ApiFetchResult.IsRetryable(code), code);
        }
    }

    [Fact]
    public void TheApiHost_CannotResolve_IApiFetch_OrTheSender()
    {
        using var scope = fixture.Factory.Services.CreateScope();

        Assert.Null(scope.ServiceProvider.GetService<IApiFetch>());
        Assert.Null(scope.ServiceProvider.GetService<IApiFetchSender>());
        Assert.Null(scope.ServiceProvider.GetService<IApiProbe>()); // the same Worker-only registration
    }

    [Fact]
    public void TheWorkerRegistration_AddsTheSenderAsSingleton_AndTheServiceScoped()
    {
        var services = new ServiceCollection();
        services.AddVersoApiConnections(new ConfigurationBuilder().Build());

        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == typeof(IApiFetchSender)).Lifetime);
        var fetch = Assert.Single(services, d => d.ServiceType == typeof(IApiFetch));
        Assert.Equal(ServiceLifetime.Scoped, fetch.Lifetime);
        Assert.Equal(typeof(ApiFetchService), fetch.ImplementationType);
    }
}
