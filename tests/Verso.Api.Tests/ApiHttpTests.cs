using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Verso.Application.Connections;
using Verso.Infrastructure.Connections;

namespace Verso.Api.Tests;

/// <summary>The shared outbound HTTP helper (ADR 0017): used by the connection test and by the Fetch from API step.</summary>
public sealed class ApiHttpTests
{
    private static ApiConnectionSettings Settings(string authType, string name = "", string secret = "") =>
        new("https://api.example.test", "/v1/ping", authType, name, secret, "https://login.example.test/token", "", TimeSpan.FromSeconds(5));

    [Theory]
    [InlineData(401, "api.unauthorized")]
    [InlineData(403, "api.unauthorized")]
    [InlineData(404, "api.not_found")]
    [InlineData(429, "api.rate_limited")]
    [InlineData(301, "api.redirect_not_followed")]
    [InlineData(307, "api.redirect_not_followed")]
    [InlineData(400, "api.client_error")]
    [InlineData(418, "api.client_error")]
    [InlineData(500, "api.server_error")]
    [InlineData(503, "api.server_error")]
    [InlineData(100, "api.client_error")]
    [InlineData(0, "api.client_error")]
    public void CodeForStatus_MapsStatuses(int status, string code) => Assert.Equal(code, ApiHttp.CodeForStatus(status));

    [Fact]
    public void CodeForException_MapsKnownExceptions()
    {
        Assert.Equal("api.blocked_address", ApiHttp.CodeForException(new BlockedAddressException()));
        Assert.Equal("api.certificate_untrusted", ApiHttp.CodeForException(new AuthenticationException()));
        Assert.Equal("api.timeout", ApiHttp.CodeForException(new TimeoutException()));
        Assert.Equal("api.timeout", ApiHttp.CodeForException(new HttpRequestException("x", new TimeoutException())));
        Assert.Equal("api.blocked_address", ApiHttp.CodeForException(new HttpRequestException("x", new BlockedAddressException())));
        Assert.Equal("api.unreachable", ApiHttp.CodeForException(new InvalidOperationException()));
        Assert.Equal("api.unreachable", ApiHttp.CodeForException(new HttpRequestException("x", new IOException())));
    }

    [Fact]
    public async Task CopyLimitedAsync_StopsAtTheLimit()
    {
        using var from = new MemoryStream(new byte[20_000]);
        using var to = new MemoryStream();

        var copied = await ApiHttp.CopyLimitedAsync(from, to, 10_000, CancellationToken.None);

        Assert.Equal(10_000, copied);
        Assert.Equal(10_000, to.Length);
    }

    [Fact]
    public async Task CopyLimitedAsync_CopiesLessWhenTheSourceIsShorter()
    {
        using var from = new MemoryStream(new byte[300]);
        using var to = new MemoryStream();

        var copied = await ApiHttp.CopyLimitedAsync(from, to, 10_000, CancellationToken.None);

        Assert.Equal(300, copied);
        Assert.Equal(300, to.Length);
    }

    [Fact]
    public void ApplyAuth_ApiKeyHeader_AddsTheNamedHeader()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/");
        ApiHttp.ApplyAuth(request, Settings(ApiAuthTypes.ApiKeyHeader, "X-Api-Key", "s3cret"), null);

        Assert.Equal("s3cret", Assert.Single(request.Headers.GetValues("X-Api-Key")));
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public void ApplyAuth_Bearer_SetsTheAuthorizationHeader()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/");
        ApiHttp.ApplyAuth(request, Settings(ApiAuthTypes.Bearer, secret: "tok"), null);

        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("tok", request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public void ApplyAuth_Basic_EncodesUserAndPassword()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/");
        ApiHttp.ApplyAuth(request, Settings(ApiAuthTypes.Basic, "user", "pass"), null);

        Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pass")), request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public void ApplyAuth_OAuth_UsesTheTokenWhenGiven()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/");
        ApiHttp.ApplyAuth(request, Settings(ApiAuthTypes.OAuthClientCredentials, "id", "secret"), "access-1");

        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("access-1", request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public void ApplyAuth_OAuth_WithoutATokenAddsNothing()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/");
        ApiHttp.ApplyAuth(request, Settings(ApiAuthTypes.OAuthClientCredentials, "id", "secret"), null);

        Assert.Null(request.Headers.Authorization);
    }

    [Theory]
    [InlineData(ApiAuthTypes.ApiKeyQuery)]
    [InlineData(ApiAuthTypes.None)]
    public void ApplyAuth_QueryAndNone_AddNothingToTheHeaders(string authType)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/");
        ApiHttp.ApplyAuth(request, Settings(authType, "key", "s3cret"), null);

        Assert.Null(request.Headers.Authorization);
        Assert.Empty(request.Headers);
    }

    [Theory]
    [InlineData("https://api.example.test/v1/x", "a", "b", "https://api.example.test/v1/x?a=b")]
    [InlineData("https://api.example.test/v1/x?a=b", "c", "d", "https://api.example.test/v1/x?a=b&c=d")]
    [InlineData("https://api.example.test/x", "a b&c", "x?y=1&z#f", "https://api.example.test/x?a%20b%26c=x%3Fy%3D1%26z%23f")]
    [InlineData("https://api.example.test/x", "k", "../%2e/@evil.test", "https://api.example.test/x?k=..%2F%252e%2F%40evil.test")]
    public void AppendQuery_EncodesNameAndValueAsOnePieceEach(string url, string name, string value, string expected) =>
        Assert.Equal(expected, ApiHttp.AppendQuery(url, name, value));

    [Fact]
    public void CreateHandler_DoesNotFollowRedirectsOrKeepCookies()
    {
        using var handler = ApiHttp.CreateHandler(TimeSpan.FromSeconds(5));

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Equal(TimeSpan.FromSeconds(5), handler.ConnectTimeout);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public async Task CreateHandler_RefusesToConnectToALoopbackAddress()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var http = new HttpClient(ApiHttp.CreateHandler(TimeSpan.FromSeconds(5)));

            var ex = await Assert.ThrowsAnyAsync<Exception>(() => http.GetAsync($"https://127.0.0.1:{port}/"));

            Assert.Equal("api.blocked_address", ApiHttp.CodeForException(ex));
            Assert.Contains(Chain(ex), e => e is BlockedAddressException);
            Assert.False(listener.Pending(), "the guarded connect must not reach the listener");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static IEnumerable<Exception> Chain(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            yield return e;
            if (e.InnerException is null)
            {
                yield break;
            }
        }
    }
}
