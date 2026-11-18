using System.Text;
using System.Text.RegularExpressions;
using Verso.Domain.Connections;

namespace Verso.Application.Connections;

/// <summary>
/// Creates or updates an SMS connection. <see cref="Key"/> is used on create only. <see cref="Secret"/> is the provider's API key: required on
/// create; on update null keeps the stored one and a value replaces it. It is never returned.
/// </summary>
public sealed record SaveSmsConnectionRequest(
    string? Key, string? Name, string? Provider, string? BaseUrl, string? Sender, string? MessageType, bool? UnicodeEnabled,
    string? OrganisationPrefix, int? TimeoutSeconds, string? Description, string? Secret);

/// <summary>An SMS connection as the Configuration screen sees it. No API key: only whether one is stored.</summary>
public sealed record SmsConnectionDto(
    Guid Id, string Key, string Name, string Provider, string BaseUrl, string Sender, string MessageType, bool UnicodeEnabled,
    string OrganisationPrefix, int TimeoutSeconds, string Description, bool IsActive, bool HasSecret, DateTimeOffset UpdatedAt, string UpdatedBy);

/// <summary>The recipient of the one real test SMS, in international format. Never stored in clear, never audited.</summary>
public sealed record RequestSmsConnectionTest(string? Recipient);

/// <summary>The state of an SMS connection test. Never carries the phone number, the message text, the API's answer or the key.</summary>
public sealed record SmsConnectionTestDto(
    Guid Id, string ConnectionKey, string Status, string? Code, string? Message, int? HttpStatus, int? ElapsedMs, string? MessageId,
    int? SmsCount, decimal? RemainingCredits, DateTimeOffset RequestedAt, DateTimeOffset? CompletedAt);

/// <summary>What the Worker needs to send the test SMS, API key decrypted in memory only.</summary>
public sealed record SmsConnectionSettings(
    string BaseUrl, string Sender, string MessageType, bool UnicodeEnabled, string OrganisationPrefix, string ApiKey, TimeSpan Timeout);

/// <summary>The outcome of one test SMS. Only the status, the time, the provider's message id, the number of parts and the credits left are kept.</summary>
public sealed record SmsProbeResult(
    bool Ok, string? Code, string? Message, int? HttpStatus, int? ElapsedMs, string? MessageId, int? SmsCount, decimal? RemainingCredits)
{
    public static SmsProbeResult Success(int httpStatus, int elapsedMs, string? messageId, int? smsCount, decimal? remainingCredits) =>
        new(true, null, null, httpStatus, elapsedMs, messageId, smsCount, remainingCredits);

    /// <summary>A failure with Verso's own wording for <paramref name="code"/>.</summary>
    public static SmsProbeResult Failure(string code, int? httpStatus = null, int? elapsedMs = null) =>
        new(false, code, SmsProbeMessages.For(code), httpStatus, elapsedMs, null, null, null);
}

/// <summary>
/// Sends the one real SMS of "Test connection" (ADR 0018). Implemented in Infrastructure with <c>HttpClient</c>, registered in the Worker only.
/// HTTPS only, no redirects, no loopback or link-local address. <c>recipientDigits</c> is the number in international format
/// without "+" (for example 35799000000).
/// </summary>
public interface ISmsProbe
{
    Task<SmsProbeResult> SendTestAsync(SmsConnectionSettings settings, string recipientDigits, CancellationToken ct);
}

/// <summary>Queues the Worker job that runs an SMS connection test (Hangfire in Infrastructure).</summary>
public interface ISmsConnectionTestJobs
{
    void Enqueue(Guid testId);
}

/// <summary>Verso's own wording for each failure code (never the provider's response text).</summary>
public static class SmsProbeMessages
{
    public static string For(string code) => code switch
    {
        "sms.unauthorized" => "The SMS provider refused the API key (HTTP 401 or 403). Check the key and that this server may use it.",
        "sms.invalid_recipient" => "The SMS provider did not accept the phone number. Use international format, such as +357 99 000000.",
        "sms.insufficient_credits" => "The SMS account has no credits left.",
        "sms.rate_limited" => "The SMS provider is limiting requests (HTTP 429). Try again later.",
        "sms.redirect_not_followed" => "The SMS provider answered with a redirect, which Verso does not follow. Use the address it redirects to.",
        "sms.client_error" => "The SMS provider rejected the message (an HTTP 4xx answer). Check the sender, the type and the base address.",
        "sms.server_error" => "The SMS provider reported an error on its side (an HTTP 5xx answer).",
        "sms.timeout" => "The SMS provider did not answer in time. The message may or may not have been sent.",
        "sms.unreachable" => "The SMS provider could not be reached. Check the address, the network, the firewall and any proxy.",
        "sms.certificate_untrusted" => "The SMS provider's certificate is not trusted. Install its certificate authority on the Worker server.",
        "sms.blocked_address" => "The address resolves to a loopback or link-local address, which Verso does not connect to.",
        "sms.host_not_allowed" => "The administrator has not allowed the Worker to call this host (SmsConnections:AllowedHosts).",
        "sms.secret_unreadable" => "The stored API key can't be read: the Worker's Secrets:MasterKey is missing or differs from the API's. Set it, or type the key again.",
        "sms.recipient_unreadable" => "The test number could not be read by the Worker (Secrets:MasterKey differs from the API's). Nothing was sent.",
        "sms.connection_unavailable" => "The connection no longer exists or is switched off.",
        _ => "The test could not be run.",
    };
}

/// <summary>Optional limit set by the administrator on the Worker: when <see cref="AllowedHosts"/> is not empty only these hosts may be called.</summary>
public sealed class SmsConnectionOptions
{
    public const string Section = "SmsConnections";
    public string[] AllowedHosts { get; set; } = [];
}

public static class SmsConnectionContexts
{
    public static string For(string key) => $"sms-connection:{key}";

    /// <summary>The context of the encrypted test recipient (one per test).</summary>
    public static string ForTest(Guid testId) => $"sms-test:{testId}";
}

/// <summary>Field rules for SMS connections.</summary>
public static partial class SmsRules
{
    public const string DefaultBaseUrl = "https://api.brevo.com/v3";
    public const int MaxDescriptionLength = 500;
    public const int MaxPrefixLength = 100;

    public static readonly IReadOnlyList<string> MessageTypes = [SmsConnection.Marketing, SmsConnection.Transactional];

    /// <summary>Up to 11 letters and digits, or up to 15 digits (a number).</summary>
    public static bool IsValidSender(string? sender) => sender is not null && SenderRegex().IsMatch(sender);

    /// <summary>
    /// Reads a phone number in international format: an optional "+" or "00", then 8 to 15 digits starting with 1-9; spaces, dots, dashes and
    /// brackets are ignored. Returns the digits only (no "+"), which is what the provider expects.
    /// </summary>
    public static bool TryNormaliseRecipient(string? input, out string digits)
    {
        digits = "";
        if (string.IsNullOrWhiteSpace(input) || input.Length > 40)
        {
            return false;
        }

        var text = input.Trim();
        var cleaned = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if ((c == '+' && i == 0) || c is ' ' or '.' or '-' or '(' or ')')
            {
                continue;
            }

            if (c is < '0' or > '9')
            {
                return false;
            }

            cleaned.Append(c);
        }

        var value = cleaned.ToString();
        if (text[0] != '+' && value.StartsWith("00", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        if (!RecipientRegex().IsMatch(value))
        {
            return false;
        }

        digits = value;
        return true;
    }

    [GeneratedRegex("^([A-Za-z0-9]{1,11}|[0-9]{1,15})$", RegexOptions.CultureInvariant)]
    private static partial Regex SenderRegex();

    [GeneratedRegex("^[1-9][0-9]{7,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex RecipientRegex();
}
