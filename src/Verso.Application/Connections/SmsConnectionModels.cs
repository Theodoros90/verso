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
    string? OrganisationPrefix, int? TimeoutSeconds, string? Description, string? Secret,
    bool? AllowSending = null, string? AllowedCountryPrefixes = null);

/// <summary>An SMS connection as the Configuration screen sees it. No API key: only whether one is stored.</summary>
public sealed record SmsConnectionDto(
    Guid Id, string Key, string Name, string Provider, string BaseUrl, string Sender, string MessageType, bool UnicodeEnabled,
    string OrganisationPrefix, int TimeoutSeconds, string Description, bool IsActive, bool HasSecret, DateTimeOffset UpdatedAt, string UpdatedBy,
    bool AllowSending, string AllowedCountryPrefixes);

/// <summary>What a workflow author sees of an active SMS connection: key, name, type and whether sending is switched on. Nothing else.</summary>
public sealed record SmsConnectionOptionDto(string Key, string Name, string MessageType, bool AllowsSending);

/// <summary>The recipient of the one real test SMS, in international format. Never stored in clear, never audited.</summary>
public sealed record RequestSmsConnectionTest(string? Recipient);

/// <summary>The state of an SMS connection test. Never carries the phone number, the message text, the API's answer or the key.</summary>
public sealed record SmsConnectionTestDto(
    Guid Id, string ConnectionKey, string Status, string? Code, string? Message, int? HttpStatus, int? ElapsedMs, string? MessageId,
    int? SmsCount, decimal? RemainingCredits, DateTimeOffset RequestedAt, DateTimeOffset? CompletedAt);

/// <summary>What the Worker needs to send the test SMS, API key decrypted in memory only.</summary>
public sealed record SmsConnectionSettings(
    string BaseUrl, string Sender, string MessageType, bool UnicodeEnabled, string OrganisationPrefix, string ApiKey, TimeSpan Timeout)
{
    /// <summary>Holds the API key: ToString shows only the type, the Unicode switch and the timeout.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"MessageType = {MessageType}, UnicodeEnabled = {UnicodeEnabled}, Timeout = {Timeout}, address, sender, prefix and API key redacted");
        return true;
    }
}

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
        // The Send SMS step (Phase 4). A failure whose outcome is unknown always shows SmsSendMessages.OutcomeUnknown instead.
        "sms.sending_not_allowed" => "Sending is switched off for this connection. An administrator can switch it on in Configuration, Connections.",
        "sms.marketing_not_allowed" => "Marketing connections can't be used in workflows.",
        "sms.country_not_allowed" => "The number's country is not one the administrator allowed for this connection.",
        "sms.recipient_invalid" => "The number is not a valid international phone number.",
        "sms.text_invalid" => "The message text is empty, too long or contains characters that can't be sent.",
        "sms.text_needs_unicode" => "The text needs Unicode characters but this connection has Unicode switched off. An administrator can switch it on.",
        "sms.tag_invalid" => "The message tag Verso built is not valid. Nothing was sent.",
        "sms.bad_request" => "The connection's base address is not a valid https address. Nothing was sent.",
        "sms.duplicate_request" or "sms.connection_lost" or "sms.unexpected_answer" => SmsSendMessages.OutcomeUnknown,
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
    public const int MaxCountryPrefixes = 20;
    public const int MaxCountryPrefixesLength = 200;

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

    /// <summary>
    /// Reads the country dialling codes an administrator allows: separated by comma, space or semicolon, a leading "+" or "00" ignored, each 1 to 4
    /// digits not starting with 0, repeats dropped, at most 20. <paramref name="normalised"/> is the codes joined with commas in the order typed.
    /// Empty input is an empty list, not a problem.
    /// </summary>
    public static bool TryNormalisePrefixes(string? input, out string normalised, out string? problem)
    {
        normalised = "";
        problem = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return true;
        }

        var list = new List<string>();
        foreach (var token in input.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var digits = token.StartsWith('+') ? token[1..] : token.StartsWith("00", StringComparison.Ordinal) ? token[2..] : token;
            if (digits.Length is < 1 or > 4 || digits[0] == '0' || !digits.All(char.IsAsciiDigit))
            {
                var shown = token.Length > 10 ? token[..10] : token;
                problem = $"Each country prefix must be 1 to 4 digits and not start with 0, such as 357 or +49 (not \"{shown}\").";
                return false;
            }

            if (!list.Contains(digits, StringComparer.Ordinal))
            {
                list.Add(digits);
            }
        }

        if (list.Count > MaxCountryPrefixes)
        {
            problem = $"Enter at most {MaxCountryPrefixes} country prefixes.";
            return false;
        }

        normalised = string.Join(',', list);
        return true;
    }

    /// <summary>The longest text a Send SMS step may send, in UTF-16 characters (Brevo splits a long text into several messages).</summary>
    public const int MaxTextLength = 480;

    /// <summary>
    /// The GSM 03.38 default alphabet (basic set) plus its extension table (form feed, ^ { } \ [ ~ ] | €, sent as two septets each). Any other character
    /// makes the provider switch to Unicode (UCS-2). Line feed and carriage return are in the basic set.
    /// </summary>
    private const string GsmCharacters =
        "@£$¥èéùìòÇ\nØø\rÅå" + // @ £ $ ¥ è é ù ì ò Ç LF Ø ø CR Å å
        "Δ_ΦΓΛΩΠΨΣΘΞÆæßÉ" +   // Δ _ Φ Γ Λ Ω Π Ψ Σ Θ Ξ Æ æ ß É (0x1B is the escape)
        " !\"#¤%&'()*+,-./0123456789:;<=>?" +                                                     // ¤ in place of the currency sign
        "¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§" +                              // ¡ ... Ä Ö Ñ Ü §
        "¿abcdefghijklmnopqrstuvwxyzäöñüà" +                              // ¿ ... ä ö ñ ü à
        "\f^{}\\[~]|€";                                                                            // the extension table

    private static readonly System.Buffers.SearchValues<char> Gsm = System.Buffers.SearchValues.Create(GsmCharacters);

    /// <summary>True when <paramref name="text"/> has a character outside the GSM 7-bit basic set and its extension table (so it can only go as Unicode).</summary>
    public static bool NeedsUnicode(string text) => text.AsSpan().IndexOfAnyExcept(Gsm) >= 0;

    /// <summary>
    /// The text rules of a message: not empty or only white space, at most <see cref="MaxTextLength"/> characters, no control characters except a line
    /// feed (a carriage return is accepted only right before a line feed, and the pair becomes one line feed), no lone surrogate (a half of a UTF-16 pair,
    /// which can't be encoded). <paramref name="normalised"/> is the text with CR LF turned into LF.
    /// </summary>
    public static bool TryNormaliseText(string? text, out string normalised)
    {
        normalised = "";
        if (text is null)
        {
            return false;
        }

        var value = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (value.Length > MaxTextLength || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return false;
                }

                i++; // a complete pair
            }
            else if (char.IsLowSurrogate(c) || (char.IsControl(c) && c != '\n'))
            {
                return false;
            }
        }

        normalised = value;
        return true;
    }

    /// <summary>The tag Verso sends with a message: 1 to 24 ASCII letters, digits and dashes (Brevo documents a tag but not its limits).</summary>
    public static bool IsValidTag(string? tag) => tag is not null && TagRegex().IsMatch(tag);

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,24}\z", RegexOptions.CultureInvariant)] // \z, not $: "$" would also match before a final line feed
    private static partial Regex TagRegex();

    [GeneratedRegex("^([A-Za-z0-9]{1,11}|[0-9]{1,15})$", RegexOptions.CultureInvariant)]
    private static partial Regex SenderRegex();

    [GeneratedRegex("^[1-9][0-9]{7,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex RecipientRegex();
}
