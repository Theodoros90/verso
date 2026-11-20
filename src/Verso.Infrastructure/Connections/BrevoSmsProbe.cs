using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Verso.Application.Connections;

namespace Verso.Infrastructure.Connections;

/// <summary>
/// Sends the one real SMS of "Test connection" through Brevo (ADR 0018): <c>POST {base}/transactionalSMS/sms</c> (<see cref="BrevoSms.SendPath"/>) with the <c>api-key</c> header.
/// Same network rules as the HTTP API probe: HTTPS only, no redirects, only allowed addresses (checked when connecting), the configured timeout,
/// no new package. The provider's answer is read (at most 64 KB) only for the message id, the number of parts, the credits left and, on a
/// refusal, to tell a bad number from a missing balance; it is never stored, logged or shown. The test text is fixed and contains no data.
/// </summary>
public sealed class BrevoSmsProbe(Func<HttpMessageHandler>? handlerFactory = null) : ISmsProbe
{
    public const string TestText = "Verso test message";
    private const int MaxBodyBytes = 64 * 1024;

    public async Task<SmsProbeResult> SendTestAsync(SmsConnectionSettings settings, string recipientDigits, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        using var http = new HttpClient(handlerFactory?.Invoke() ?? HttpApiProbe.CreateHandler(settings.Timeout)) { Timeout = settings.Timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Verso-connection-test/1.0");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(settings))
            {
                Content = new StringContent(BuildBody(settings, recipientDigits), Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.TryAddWithoutValidation("api-key", settings.ApiKey);

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await ReadLimitedAsync(response.Content, ct);
            var elapsed = (int)clock.ElapsedMilliseconds;
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300)
            {
                var (messageId, smsCount, remaining) = ParseSuccess(body);
                return SmsProbeResult.Success(status, elapsed, messageId, smsCount, remaining);
            }

            return SmsProbeResult.Failure(CodeForStatus(status, body), status, elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return SmsProbeResult.Failure("sms.timeout", elapsedMs: (int)clock.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or AuthenticationException or BlockedAddressException or InvalidOperationException or UriFormatException or IOException)
        {
            return SmsProbeResult.Failure(CodeForException(ex), elapsedMs: (int)clock.ElapsedMilliseconds);
        }
    }

    /// <summary>The send address: the base without a trailing slash plus <see cref="BrevoSms.SendPath"/> (shared with the Send SMS step).</summary>
    internal static Uri BuildUrl(SmsConnectionSettings s) => BrevoSms.BuildUrl(s);

    /// <summary>The fixed test text, no tag (shared body builder, same bytes as before it was shared).</summary>
    internal static string BuildBody(SmsConnectionSettings s, string recipientDigits) => BrevoSms.BuildBody(s, recipientDigits, TestText, tag: null);

    internal static (string? MessageId, int? SmsCount, decimal? RemainingCredits) ParseSuccess(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null, null);
            }

            string? messageId = null;
            if (root.TryGetProperty("messageId", out var id))
            {
                messageId = id.ValueKind switch { JsonValueKind.String => id.GetString(), JsonValueKind.Number => id.GetRawText(), _ => null };
            }

            int? count = root.TryGetProperty("smsCount", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) ? n : null;
            decimal? remaining = root.TryGetProperty("remainingCredits", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetDecimal(out var d) ? d : null;
            return (messageId, count, remaining);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    internal static string CodeForStatus(int status, string body) => status switch
    {
        401 or 403 => "sms.unauthorized",
        402 => "sms.insufficient_credits",
        429 => "sms.rate_limited",
        >= 300 and < 400 => "sms.redirect_not_followed",
        400 => ClassifyBadRequest(body),
        >= 400 and < 500 => "sms.client_error",
        >= 500 => "sms.server_error",
        _ => "sms.client_error",
    };

    /// <summary>A 400 from Brevo carries a code and a text; only used here to tell a bad number or an empty balance from other mistakes.</summary>
    internal static string ClassifyBadRequest(string body) => BrevoSms.ClassifyBadRequest(body);

    internal static string CodeForException(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            switch (e)
            {
                case BlockedAddressException:
                    return "sms.blocked_address";
                case AuthenticationException:
                    return "sms.certificate_untrusted";
                case TimeoutException:
                    return "sms.timeout";
            }

            if (e.InnerException is null)
            {
                break;
            }
        }

        return "sms.unreachable";
    }

    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[8192];
        using var memory = new MemoryStream();
        int read;
        while (memory.Length < MaxBodyBytes && (read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, MaxBodyBytes - memory.Length)), ct)) > 0)
        {
            memory.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }
}
