using System.Diagnostics;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Verso.Application.Connections;

namespace Verso.Infrastructure.Connections;

/// <summary>
/// Sends the one SMS of the Send SMS step through Brevo with <c>HttpClient</c> (no new package): <c>POST {base}{BrevoSms.SendPath}</c>, the body from
/// <see cref="BrevoSms.BuildBody"/> (shared with the connection test), the <c>api-key</c> header, through the guards every outbound call shares
/// (<see cref="ApiHttp.CreateHandler"/>: HTTPS only, no redirects, no cookies, only allowed addresses checked when connecting, TLS 1.2 / 1.3) and ONE
/// deadline over the POST and the answer's read. The answer is read at most 64 KB, only for the message id and the number of parts (and, on a 400, to
/// tell a bad number, an empty balance or a duplicate apart); it is never stored, logged or shown.
/// <para>
/// Known versus unknown: a message to a person can't be recalled, and Brevo documents no idempotency key, so Verso must never send twice. A failure is
/// KNOWN (nothing sent) only when the request certainly did not leave or Brevo certainly refused it; anything else is an UNKNOWN outcome. No failure is
/// ever retried.
/// </para>
/// </summary>
internal sealed class HttpSmsSender(Func<HttpMessageHandler>? handlerFactory = null) : ISmsSender
{
    private const int MaxAnswerBytes = 64 * 1024;

    /// <summary>The longest provider message id kept; a longer one is dropped (null).</summary>
    public const int MaxMessageIdLength = 100;

    public async Task<SmsSendResult> SendAsync(SmsConnectionSettings settings, SmsSendRequest request, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();

        // Building the request: a failure here is known (nothing has left). The address must stay plain https on the base's host and port.
        Uri uri;
        string body;
        try
        {
            uri = BrevoSms.BuildUrl(settings);
            body = BrevoSms.BuildBody(settings, request.RecipientDigits, request.Text, request.Tag);
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return Known("sms.bad_request", clock);
        }

        if (!IsSameHttpsHost(uri, settings.BaseUrl))
        {
            return Known("sms.bad_request", clock);
        }

        // ONE deadline for the POST and the body read (HttpClient.Timeout alone stops at the response headers). The caller's token is linked in.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(settings.Timeout);
        var token = deadline.Token;

        using var http = new HttpClient(handlerFactory?.Invoke() ?? ApiHttp.CreateHandler(settings.Timeout)) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Verso-sms/1.0");
        using var message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        message.Headers.Accept.ParseAdd("application/json");
        message.Headers.TryAddWithoutValidation("api-key", settings.ApiKey); // the value is never logged

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The deadline fired before the answer's headers. It may have fired while the request was on the wire or waiting for Brevo: unknown.
            return Unknown("sms.timeout", clock);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ForSendException(ex, clock);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300)
            {
                // Brevo accepted the message. Whatever happens while reading the answer it was sent, even if the caller cancels now (Worker
                // shutdown, run cancelled): "sent" must never be lost or turned into an exception. An unreadable answer only loses the id and parts.
                var accepted = await ReadAcceptedAsync(response, token);
                var (messageId, parts) = ParseSuccess(accepted);
                return SmsSendResult.Success(messageId, parts, clock.ElapsedMilliseconds);
            }

            switch (status)
            {
                case >= 300 and < 400:
                    return Known("sms.redirect_not_followed", clock); // never followed: the handler does not redirect, so nothing went further
                case 401 or 403:
                    return Known("sms.unauthorized", clock);
                case 402:
                    return Known("sms.insufficient_credits", clock);
                case 429:
                    return Known("sms.rate_limited", clock);
                case 400:
                    {
                        // A 400 is a refusal, except a duplicate ("duplicate_request", "Request already processed"): the same message may already have
                        // been accepted. When the body can't be read we can't rule that out: unknown.
                        var answer = await TryReadAsync(response, token, ct);
                        if (answer.Problem is { } problem)
                        {
                            return Unknown(problem, clock);
                        }

                        return BrevoSms.IsDuplicate(answer.Body)
                            ? Unknown("sms.duplicate_request", clock)
                            : Known(BrevoSms.ClassifyBadRequest(answer.Body), clock);
                    }

                case >= 400 and < 500:
                    return Known("sms.client_error", clock);
                case >= 500:
                    return Unknown("sms.server_error", clock); // Brevo may have queued the message before failing
                default:
                    return Unknown("sms.unexpected_answer", clock); // an informational status as the final answer: not a refusal we understand
            }
        }
    }

    /// <summary>
    /// What an exception from <c>SendAsync</c> means. KNOWN only when the request can't have left this machine:
    /// <list type="bullet">
    /// <item><see cref="BlockedAddressException"/> anywhere in the chain: only our connect guard throws it, before any socket is opened.</item>
    /// <item><see cref="HttpRequestException.HttpRequestError"/> <see cref="HttpRequestError.NameResolutionError"/> (no address),
    /// <see cref="HttpRequestError.ConnectionError"/> (the TCP connection could not be opened), <see cref="HttpRequestError.SecureConnectionError"/>
    /// (the TLS handshake failed) and <see cref="HttpRequestError.ProxyTunnelError"/> (the proxy tunnel was refused): all raised while connecting,
    /// before a byte of the request is written. Each send uses a new handler, so there is no pooled connection whose failure could come later.</item>
    /// <item>An <see cref="AuthenticationException"/> with no <see cref="HttpRequestException"/> around it (thrown straight from the connect phase).</item>
    /// </list>
    /// An <see cref="AuthenticationException"/> inside an <see cref="HttpRequestException"/> of any other kind is NOT known: a TLS failure after the
    /// handshake (for example while the answer is read) may come after the request was written. Everything else may have happened after the request
    /// was written (connection reset, <see cref="HttpRequestError.ResponseEnded"/>, <see cref="HttpRequestError.InvalidResponse"/>,
    /// <see cref="HttpRequestError.HttpProtocolError"/>, <see cref="HttpRequestError.Unknown"/>, an <see cref="IOException"/>, a
    /// <see cref="TimeoutException"/>, anything unexpected): UNKNOWN.
    /// </summary>
    internal static SmsSendResult ForSendException(Exception ex, Stopwatch clock)
    {
        var wrapped = false;
        var authentication = false;
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case BlockedAddressException:
                    return Known("sms.blocked_address", clock);
                case HttpRequestException:
                    wrapped = true;
                    break;
                case AuthenticationException:
                    authentication = true;
                    break;
            }
        }

        if (ex is HttpRequestException request)
        {
            switch (request.HttpRequestError)
            {
                case HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.ProxyTunnelError:
                    return Known("sms.unreachable", clock);
                case HttpRequestError.SecureConnectionError:
                    return Known("sms.certificate_untrusted", clock);
            }
        }

        if (authentication && !wrapped)
        {
            return Known("sms.certificate_untrusted", clock);
        }

        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is TimeoutException)
            {
                return Unknown("sms.timeout", clock);
            }
        }

        return Unknown("sms.connection_lost", clock);
    }

    /// <summary>
    /// Reads at most 64 KB of the answer within the deadline (more is not an error: the first 64 KB are enough). Never throws except for the caller's
    /// cancellation: a deadline gives <c>sms.timeout</c>, any other failure <c>sms.connection_lost</c>, in <c>Problem</c>.
    /// </summary>
    private static async Task<(string Body, string? Problem)> TryReadAsync(HttpResponseMessage response, CancellationToken token, CancellationToken ct)
    {
        try
        {
            using var buffer = new MemoryStream();
            await using (var stream = await response.Content.ReadAsStreamAsync(token))
            {
                await ApiHttp.CopyLimitedAsync(stream, buffer, MaxAnswerBytes, token);
            }

            return (Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return ("", ex is OperationCanceledException ? "sms.timeout" : "sms.connection_lost");
        }
    }

    /// <summary>The body of a 2xx, best effort: ANY failure (the deadline, a broken connection, the caller's cancellation) gives an empty body.</summary>
    private static async Task<string> ReadAcceptedAsync(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            using var buffer = new MemoryStream();
            await using (var stream = await response.Content.ReadAsStreamAsync(token))
            {
                await ApiHttp.CopyLimitedAsync(stream, buffer, MaxAnswerBytes, token);
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (Exception)
        {
            return ""; // deliberately everything: the message was accepted, and that fact must reach the caller
        }
    }

    /// <summary>The message id (string or number) and the number of parts (<c>smsCount</c>) when the answer has them; otherwise null.</summary>
    internal static (string? MessageId, int? Parts) ParseSuccess(string body)
    {
        if (body.Length == 0)
        {
            return (null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            string? messageId = null;
            if (root.TryGetProperty("messageId", out var id))
            {
                messageId = id.ValueKind switch { JsonValueKind.String => id.GetString(), JsonValueKind.Number => id.GetRawText(), _ => null };
                if (messageId is { Length: > MaxMessageIdLength })
                {
                    messageId = null; // not an id we expect (Brevo's is a number): dropped rather than cut, never stored at any length
                }
            }

            int? parts = root.TryGetProperty("smsCount", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) ? n : null;
            return (messageId, parts);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return (null, null);
        }
    }

    /// <summary>An absolute https address on the base's host and port, without user info or fragment.</summary>
    internal static bool IsSameHttpsHost(Uri uri, string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
        && baseUri.Scheme == Uri.UriSchemeHttps
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Length > 0
        && string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Port == baseUri.Port
        && uri.UserInfo.Length == 0
        && baseUri.UserInfo.Length == 0
        && uri.Fragment.Length == 0;

    private static SmsSendResult Known(string code, Stopwatch clock) => SmsSendResult.Failure(code, outcomeUnknown: false, clock.ElapsedMilliseconds);

    private static SmsSendResult Unknown(string code, Stopwatch clock) => SmsSendResult.Failure(code, outcomeUnknown: true, clock.ElapsedMilliseconds);
}
