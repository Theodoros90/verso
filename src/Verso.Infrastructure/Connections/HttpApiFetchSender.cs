using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using Verso.Application.Connections;

namespace Verso.Infrastructure.Connections;

/// <summary>
/// Sends the one GET of the Fetch from API step with <c>HttpClient</c> (no new package), through the guards the connection test uses (<see cref="ApiHttp"/>):
/// HTTPS only, no redirects, no cookies, connects only to allowed addresses (checked when connecting), TLS 1.2 / 1.3. The address is the connection's base
/// plus the already-encoded path plus the encoded query pairs (plus the API key for the "query" kind) and must keep the base's host. For OAuth it asks the
/// token endpoint first. The answer is read at most to the limit + 1 bytes and must be JSON. Results carry Verso's codes and words, never the address,
/// the answer's text (except the parsed <see cref="ApiFetchResult.Body"/>) or a secret.
/// </summary>
public sealed class HttpApiFetchSender(Func<HttpMessageHandler>? handlerFactory = null) : IApiFetchSender
{
    public async Task<ApiFetchResult> SendAsync(ApiConnectionSettings settings, ApiFetchRequest request, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        if (BuildUri(settings, request) is not { } uri)
        {
            return ApiFetchResult.Failure("api.bad_request", retryable: false);
        }

        using var http = new HttpClient(handlerFactory?.Invoke() ?? ApiHttp.CreateHandler(request.Timeout)) { Timeout = request.Timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Verso-fetch/1.0");
        try
        {
            string? bearer = null;
            if (settings.AuthType == ApiAuthTypes.OAuthClientCredentials)
            {
                bearer = await ApiHttp.RequestTokenAsync(http, settings, ct);
                if (bearer is null)
                {
                    return Fail("api.token_failed", null, clock);
                }
            }

            using var message = new HttpRequestMessage(HttpMethod.Get, uri);
            message.Headers.Accept.ParseAdd("application/json");
            ApiHttp.ApplyAuth(message, settings, bearer);

            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;
            if (status == 404)
            {
                return ApiFetchResult.Success(404, null, 0, clock.ElapsedMilliseconds); // "not found" is an outcome; its body is never read
            }

            if (status is < 200 or >= 300)
            {
                return Fail(ApiHttp.CodeForStatus(status), status, clock); // a 3xx is never followed: the handler does not redirect
            }

            return await ReadJsonAsync(response, status, request.MaxResponseBytes, clock, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail("api.timeout", null, clock);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or AuthenticationException or BlockedAddressException or InvalidOperationException or UriFormatException or IOException)
        {
            return Fail(ApiHttp.CodeForException(ex), null, clock);
        }
    }

    /// <summary>
    /// Base + path, then each query pair encoded, then the API key for the "query" kind. Null when the result is not an absolute https address on the base's
    /// host and port without user info or fragment (defence in depth: the service has checked the path already, but no path may ever move the host).
    /// </summary>
    internal static Uri? BuildUri(ApiConnectionSettings settings, ApiFetchRequest request)
    {
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps
            || request.Path is not { Length: > 0 } path || path[0] != '/' || path.Contains('?', StringComparison.Ordinal) || path.Contains('#', StringComparison.Ordinal))
        {
            return null;
        }

        var url = ApiUrls.Combine(settings.BaseUrl, path);
        foreach (var (name, value) in request.Query)
        {
            url = ApiHttp.AppendQuery(url, name, value);
        }

        if (settings.AuthType == ApiAuthTypes.ApiKeyQuery)
        {
            url = ApiHttp.AppendQuery(url, settings.AuthName, settings.Secret);
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == baseUri.Port
            && uri.UserInfo.Length == 0
            && uri.Fragment.Length == 0
                ? uri
                : null;
    }

    /// <summary>A 2xx: empty is a success without a body; otherwise JSON only, at most <paramref name="max"/> bytes, read no further than one byte past it.</summary>
    private static async Task<ApiFetchResult> ReadJsonAsync(HttpResponseMessage response, int status, int max, Stopwatch clock, CancellationToken ct)
    {
        var declared = response.Content.Headers.ContentLength;
        if (declared == 0)
        {
            return ApiFetchResult.Success(status, null, 0, clock.ElapsedMilliseconds);
        }

        var json = IsJson(response.Content.Headers.ContentType?.MediaType);
        if (declared > max)
        {
            return Fail(json ? "api.response_too_large" : "api.not_json", status, clock);
        }

        using var buffer = new MemoryStream();
        await using (var stream = await response.Content.ReadAsStreamAsync(ct))
        {
            await ApiHttp.CopyLimitedAsync(stream, buffer, max + 1, ct);
        }

        if (buffer.Length == 0)
        {
            return ApiFetchResult.Success(status, null, 0, clock.ElapsedMilliseconds);
        }

        if (!json)
        {
            return Fail("api.not_json", status, clock);
        }

        if (buffer.Length > max)
        {
            return Fail("api.response_too_large", status, clock);
        }

        try
        {
            using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
            return ApiFetchResult.Success(status, document.RootElement.Clone(), buffer.Length, clock.ElapsedMilliseconds);
        }
        catch (JsonException)
        {
            return Fail("api.not_json", status, clock);
        }
    }

    /// <summary><c>application/json</c> or any <c>+json</c> type, ignoring case (parameters such as charset are not part of the media type).</summary>
    internal static bool IsJson(string? mediaType) =>
        mediaType is not null
        && (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    private static ApiFetchResult Fail(string code, int? status, Stopwatch clock) =>
        ApiFetchResult.Failure(code, ApiFetchResult.IsRetryable(code), status, clock.ElapsedMilliseconds);
}
