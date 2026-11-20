using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
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

    public async Task<ApiProbeResult> ProbeAsync(ApiConnectionSettings settings, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        using var http = new HttpClient(handlerFactory?.Invoke() ?? ApiHttp.CreateHandler(settings.Timeout)) { Timeout = settings.Timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Verso-connection-test/1.0");
        try
        {
            string? bearer = null;
            if (settings.AuthType == ApiAuthTypes.OAuthClientCredentials)
            {
                bearer = await ApiHttp.RequestTokenAsync(http, settings, ct);
                if (bearer is null)
                {
                    return ApiProbeResult.Failure("api.token_failed", elapsedMs: (int)clock.ElapsedMilliseconds);
                }
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(settings));
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("*/*;q=0.5");
            ApiHttp.ApplyAuth(request, settings, bearer);

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var bytes = await CountAsync(response.Content, MaxBodyBytes, ct);
            var elapsed = (int)clock.ElapsedMilliseconds;
            var contentType = response.Content.Headers.ContentType?.MediaType;
            var status = (int)response.StatusCode;
            return status is >= 200 and < 300
                ? ApiProbeResult.Success(status, elapsed, contentType, bytes)
                : ApiProbeResult.Failure(ApiHttp.CodeForStatus(status), status, elapsed, contentType, bytes);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ApiProbeResult.Failure("api.timeout", elapsedMs: (int)clock.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or AuthenticationException or BlockedAddressException or InvalidOperationException or UriFormatException or IOException)
        {
            return ApiProbeResult.Failure(ApiHttp.CodeForException(ex), elapsedMs: (int)clock.ElapsedMilliseconds);
        }
    }

    /// <summary>The request address: base plus test path, with the API key appended for the "query" kind (URL-encoded).</summary>
    internal static Uri BuildUrl(ApiConnectionSettings s)
    {
        var url = ApiUrls.Combine(s.BaseUrl, s.TestPath);
        if (s.AuthType == ApiAuthTypes.ApiKeyQuery)
        {
            url = ApiHttp.AppendQuery(url, s.AuthName, s.Secret);
        }

        return new Uri(url, UriKind.Absolute);
    }

    internal static void ApplyAuth(HttpRequestMessage request, ApiConnectionSettings s, string? oauthToken) => ApiHttp.ApplyAuth(request, s, oauthToken);

    internal static string CodeForStatus(int status) => ApiHttp.CodeForStatus(status);

    internal static string CodeForException(Exception ex) => ApiHttp.CodeForException(ex);

    internal static SocketsHttpHandler CreateHandler(TimeSpan timeout) => ApiHttp.CreateHandler(timeout);

    private static async Task<long> CountAsync(HttpContent content, int max, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        return await ApiHttp.CopyLimitedAsync(stream, Stream.Null, max, ct);
    }
}
