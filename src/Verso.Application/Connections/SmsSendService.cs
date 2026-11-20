using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Verso.Application.Mail;
using Verso.Domain.Connections;

namespace Verso.Application.Connections;

/// <summary>
/// Worker side of the Send SMS step. In this order: the request (number, text, tag), the connection (exists and active), transactional only, the
/// administrator's "Allow sending" switch, the number's country among the allowed prefixes (an empty list allows none), the host
/// (<c>SmsConnections:AllowedHosts</c>), the stored base address, Unicode; ONLY THEN is the API key decrypted (in memory only) and the one SMS handed
/// to the sender. Every check before the sender is a known failure (nothing sent). Failures are results with a code, never exceptions (except
/// cancellation); no message carries the number, the text, the provider's answer or the key. Nothing is ever retried.
/// </summary>
public sealed class SmsSendService(IVersoDbContext db, ISecretProtector secrets, ISmsSender sender, SmsConnectionOptions options) : ISmsSend
{
    public const int MaxTimeoutSeconds = 60;

    public async Task<SmsSendResult> RunAsync(SmsSendRequest request, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();

        // The request first: a caller can't bypass the number format, the text rules or the tag rule.
        if (!SmsRules.TryNormaliseRecipient(request.RecipientDigits, out var digits))
        {
            return Known("sms.recipient_invalid");
        }

        if (!SmsRules.TryNormaliseText(request.Text, out var text))
        {
            return Known("sms.text_invalid");
        }

        if (!SmsRules.IsValidTag(request.Tag))
        {
            return Known("sms.tag_invalid");
        }

        var key = request.ConnectionKey;
        var connection = await db.SmsConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Key == key, ct);
        if (connection is null || !connection.IsActive)
        {
            return Known("sms.connection_unavailable");
        }

        // The administrator's decisions, all before the key is decrypted (a wrong master key still gives these answers).
        if (!string.Equals(connection.MessageType, SmsConnection.Transactional, StringComparison.Ordinal))
        {
            return Known("sms.marketing_not_allowed");
        }

        if (!connection.AllowSending)
        {
            return Known("sms.sending_not_allowed");
        }

        if (!CountryAllowed(connection.AllowedCountryPrefixes, digits))
        {
            return Known("sms.country_not_allowed");
        }

        if (!HostsAllowed(connection))
        {
            return Known("sms.host_not_allowed");
        }

        // A stored address the save rules would refuse is never used, whatever AllowedHosts says (it receives the API key).
        if (!ApiUrls.IsValidBaseUrl(connection.BaseUrl))
        {
            return Known("sms.bad_request");
        }

        if (!connection.UnicodeEnabled && SmsRules.NeedsUnicode(text))
        {
            return Known("sms.text_needs_unicode");
        }

        // Only now is the key decrypted (in memory only).
        var apiKey = connection.SecretProtected is null ? null : secrets.Unprotect(connection.SecretProtected, SmsConnectionContexts.For(connection.Key));
        if (string.IsNullOrEmpty(apiKey))
        {
            return Known("sms.secret_unreadable");
        }

        var settings = new SmsConnectionSettings(
            connection.BaseUrl, connection.Sender, connection.MessageType, connection.UnicodeEnabled, connection.OrganisationPrefix, apiKey,
            TimeSpan.FromSeconds(Math.Clamp(connection.TimeoutSeconds, 1, MaxTimeoutSeconds)));
        try
        {
            return await sender.SendAsync(settings, request with { RecipientDigits = digits, Text = text }, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Not the caller's cancellation: a deadline inside the sender. The request may have left: unknown, never retried.
            return SmsSendResult.Failure("sms.timeout", outcomeUnknown: true, clock.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A sender should not throw; if it does, the request may have left. Say nothing about why (its text could carry the number or the text).
            return SmsSendResult.Failure("sms.unreachable", outcomeUnknown: true, clock.ElapsedMilliseconds);
        }

        SmsSendResult Known(string code) => SmsSendResult.Failure(code, outcomeUnknown: false, clock.ElapsedMilliseconds);
    }

    /// <summary>A prefix matches when the normalised digits START with it. An empty list allows no country.</summary>
    internal static bool CountryAllowed(string? allowedPrefixes, string digits)
    {
        if (string.IsNullOrWhiteSpace(allowedPrefixes))
        {
            return false;
        }

        foreach (var prefix in allowedPrefixes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (prefix.All(char.IsAsciiDigit) && digits.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private bool HostsAllowed(SmsConnection connection)
    {
        if (options.AllowedHosts.Length == 0)
        {
            return true;
        }

        return ApiUrls.HostOf(connection.BaseUrl) is { } host && options.AllowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
    }
}
