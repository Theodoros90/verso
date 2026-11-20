using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Verso.Application.Connections;

namespace Verso.Infrastructure.Connections;

/// <summary>
/// What every outbound API call of the Worker shares (ADR 0017): the guarded handler (no redirects, no cookies, TLS 1.2 / 1.3, connects only to allowed
/// addresses, checked when connecting), the authentication of the connection's kind, the OAuth client-credentials token request, the mapping of statuses
/// and exceptions to Verso's own codes, and a bounded stream copy.
/// </summary>
internal static class ApiHttp
{
    private const int MaxTokenBytes = 64 * 1024;

    internal static SocketsHttpHandler CreateHandler(TimeSpan timeout) => new()
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
    internal static async Task<string?> RequestTokenAsync(HttpClient http, ApiConnectionSettings s, CancellationToken ct)
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

    /// <summary>Appends one query pair, name and value each URL-encoded as one piece (so a "&amp;", "=", "#" or "?" in either stays inside it).</summary>
    internal static string AppendQuery(string url, string name, string value) =>
        url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value);

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

    internal static async Task<long> CopyLimitedAsync(Stream from, Stream to, int max, CancellationToken ct)
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
}
