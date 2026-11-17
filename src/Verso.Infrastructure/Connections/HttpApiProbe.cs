using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Verso.Application.Connections;

namespace Verso.Infrastructure.Connections;

/// <summary>Raised when an address resolves to somewhere Verso never connects to.</summary>
public sealed class BlockedAddressException : Exception
{
    public BlockedAddressException() : base("The address is not allowed.") { }
}

/// <summary>Which resolved addresses an API connection may reach (ADR 0017): never loopback, link-local (including cloud metadata), unspecified or multicast.</summary>
public static class IpGuard
{
    public static bool IsAllowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 169 && b[1] == 254) // link-local, includes the cloud metadata address 169.254.169.254
                && b[0] < 224;                    // multicast and reserved
        }

        return !address.IsIPv6LinkLocal && !address.IsIPv6Multicast && !address.IsIPv6SiteLocal;
    }
}

/// <summary>
/// Sends the one GET of "Test connection" (ADR 0017) with <c>HttpClient</c> (no new package). HTTPS only (the connection's address rules),
/// no redirects, connects only to allowed addresses (checked when connecting, so a name that later resolves to a blocked address is refused too),
/// the configured timeout. For OAuth it asks the token endpoint first. The response body is read only to count its size (at most 1 MB) and is
/// never kept; messages are Verso's own.
/// </summary>
public sealed class HttpApiProbe(Func<HttpMessageHandler>? handlerFactory = null) : IApiProbe
{
    private const int MaxBodyBytes = 1024 * 1024;
    private const int MaxTokenBytes = 64 * 1024;

    public async Task<ApiProbeResult> ProbeAsync(ApiConnectionSettings settings, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        using var http = new HttpClient(handlerFactory?.Invoke() ?? CreateHandler(settings.Timeout)) { Timeout = settings.Timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Verso-connection-test/1.0");
        try
        {
            string? bearer = null;
            if (settings.AuthType == ApiAuthTypes.OAuthClientCredentials)
            {
                bearer = await RequestTokenAsync(http, settings, ct);
                if (bearer is null)
                {
                    return ApiProbeResult.Failure("api.token_failed", elapsedMs: (int)clock.ElapsedMilliseconds);
                }
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(settings));
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("*/*;q=0.5");
            ApplyAuth(request, settings, bearer);

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var bytes = await CountAsync(response.Content, MaxBodyBytes, ct);
            var elapsed = (int)clock.ElapsedMilliseconds;
            var contentType = response.Content.Headers.ContentType?.MediaType;
            var status = (int)response.StatusCode;
            return status is >= 200 and < 300
                ? ApiProbeResult.Success(status, elapsed, contentType, bytes)
                : ApiProbeResult.Failure(CodeForStatus(status), status, elapsed, contentType, bytes);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ApiProbeResult.Failure("api.timeout", elapsedMs: (int)clock.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or AuthenticationException or BlockedAddressException or InvalidOperationException or UriFormatException or IOException)
        {
            return ApiProbeResult.Failure(CodeForException(ex), elapsedMs: (int)clock.ElapsedMilliseconds);
        }
    }

    /// <summary>The request address: base plus test path, with the API key appended for the "query" kind (URL-encoded).</summary>
    internal static Uri BuildUrl(ApiConnectionSettings s)
    {
        var url = ApiUrls.Combine(s.BaseUrl, s.TestPath);
        if (s.AuthType == ApiAuthTypes.ApiKeyQuery)
        {
            url += (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + Uri.EscapeDataString(s.AuthName) + "=" + Uri.EscapeDataString(s.Secret);
        }

        return new Uri(url, UriKind.Absolute);
    }

    internal static void ApplyAuth(HttpRequestMessage request, ApiConnectionSettings s, string? oauthToken)
    {
        switch (s.AuthType)
        {
            case ApiAuthTypes.ApiKeyHeader:
                request.Headers.TryAddWithoutValidation(s.AuthName, s.Secret);
                break;
            case ApiAuthTypes.Bearer:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Secret);
                break;
            case ApiAuthTypes.Basic:
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{s.AuthName}:{s.Secret}")));
                break;
            case ApiAuthTypes.OAuthClientCredentials when oauthToken is not null:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oauthToken);
                break;
        }
    }

    /// <summary>OAuth 2.0 client credentials: the client id and secret go as HTTP Basic to the token endpoint; the token is used once and never stored.</summary>
    private static async Task<string?> RequestTokenAsync(HttpClient http, ApiConnectionSettings s, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>> { new("grant_type", "client_credentials") };
        if (s.Scope.Length > 0)
        {
            form.Add(new("scope", s.Scope));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, s.TokenUrl) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(s.AuthName)}:{Uri.EscapeDataString(s.Secret)}")));
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var limited = new MemoryStream();
        await CopyLimitedAsync(stream, limited, MaxTokenBytes, ct);
        limited.Position = 0;
        try
        {
            using var doc = await JsonDocument.ParseAsync(limited, cancellationToken: ct);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("access_token", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } token
                ? token
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string CodeForStatus(int status) => status switch
    {
        401 or 403 => "api.unauthorized",
        404 => "api.not_found",
        429 => "api.rate_limited",
        >= 300 and < 400 => "api.redirect_not_followed",
        >= 400 and < 500 => "api.client_error",
        >= 500 => "api.server_error",
        _ => "api.client_error",
    };

    internal static string CodeForException(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            switch (e)
            {
                case BlockedAddressException:
                    return "api.blocked_address";
                case AuthenticationException:
                    return "api.certificate_untrusted";
                case TimeoutException:
                    return "api.timeout";
            }

            if (e.InnerException is null)
            {
                break;
            }
        }

        return "api.unreachable";
    }

    private static async Task<long> CountAsync(HttpContent content, int max, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        return await CopyLimitedAsync(stream, Stream.Null, max, ct);
    }

    private static async Task<long> CopyLimitedAsync(Stream from, Stream to, int max, CancellationToken ct)
    {
        var buffer = new byte[8192];
        long total = 0;
        int read;
        while (total < max && (read = await from.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, max - total)), ct)) > 0)
        {
            await to.WriteAsync(buffer.AsMemory(0, read), ct);
            total += read;
        }

        return total;
    }

    private static SocketsHttpHandler CreateHandler(TimeSpan timeout) => new()
    {
        AllowAutoRedirect = false, // a redirect could lead somewhere the administrator never named
        UseCookies = false,
        ConnectTimeout = timeout,
        PooledConnectionLifetime = TimeSpan.FromSeconds(30),
        SslOptions = new SslClientAuthenticationOptions { EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 },
        ConnectCallback = ConnectGuardedAsync,
    };

    /// <summary>Resolves the host and connects only to allowed addresses. Checked here, at connect time, so a name that resolves to a blocked address is refused.</summary>
    private static async ValueTask<Stream> ConnectGuardedAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        var allowed = addresses.Where(IpGuard.IsAllowed).ToArray();
        if (allowed.Length == 0)
        {
            throw new BlockedAddressException();
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
