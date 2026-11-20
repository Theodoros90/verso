using System.Text.Json;
using Verso.Application.Connections;

namespace Verso.Infrastructure.Connections;

/// <summary>
/// What the connection test (<see cref="BrevoSmsProbe"/>) and the Send SMS step (<c>HttpSmsSender</c>) share about Brevo's transactional SMS
/// call: ONE send path and ONE body builder, so the two can never drift apart.
/// </summary>
internal static class BrevoSms
{
    /// <summary>
    /// The send path. Brevo's current documentation names <c>/transactionalSMS/send</c>; the connection test has always posted to
    /// <c>/transactionalSMS/sms</c>. Unresolved (see the Send SMS ADR): kept unchanged here so both callers move together if it ever changes.
    /// </summary>
    public const string SendPath = "/transactionalSMS/sms";

    /// <summary>The send address: the base without a trailing slash plus <see cref="SendPath"/>.</summary>
    public static Uri BuildUrl(SmsConnectionSettings s) => new(s.BaseUrl.TrimEnd('/') + SendPath, UriKind.Absolute);

    /// <summary>
    /// The JSON body, fields in this fixed order: sender, recipient (digits, no "+"), content, type, unicodeEnabled, then organisationPrefix only when the
    /// connection has one and tag only when one is given (a JSON string). Serialised with the default options (non-ASCII and HTML-sensitive characters
    /// escaped as \uXXXX, which is still the same JSON text) exactly as the connection test always did.
    /// </summary>
    public static string BuildBody(SmsConnectionSettings settings, string recipientDigits, string text, string? tag)
    {
        var fields = new Dictionary<string, object>
        {
            ["sender"] = settings.Sender,
            ["recipient"] = recipientDigits,
            ["content"] = text,
            ["type"] = settings.MessageType,
            ["unicodeEnabled"] = settings.UnicodeEnabled,
        };
        if (settings.OrganisationPrefix.Length > 0)
        {
            fields["organisationPrefix"] = settings.OrganisationPrefix;
        }

        if (!string.IsNullOrEmpty(tag))
        {
            fields["tag"] = tag;
        }

        return JsonSerializer.Serialize(fields);
    }

    /// <summary>
    /// A 400 from Brevo carries a code and a text; only used to tell a bad number or an empty balance from other mistakes (the connection test's rules,
    /// unchanged). Never shown: the result is one of Verso's codes.
    /// </summary>
    public static string ClassifyBadRequest(string body)
    {
        var text = body.ToLowerInvariant();
        if (text.Contains("credit", StringComparison.Ordinal))
        {
            return "sms.insufficient_credits";
        }

        return text.Contains("recipient", StringComparison.Ordinal) || text.Contains("phone", StringComparison.Ordinal) || text.Contains("number", StringComparison.Ordinal)
            ? "sms.invalid_recipient"
            : "sms.client_error";
    }

    /// <summary>
    /// Whether a 400 says the request is a duplicate ("duplicate_request", "Request already processed"): Brevo may then already have accepted the same
    /// message, so the Send SMS step treats it as an unknown outcome. The connection test keeps its old classification.
    /// </summary>
    public static bool IsDuplicate(string body)
    {
        var text = body.ToLowerInvariant();
        return text.Contains("duplicate_request", StringComparison.Ordinal) || text.Contains("already processed", StringComparison.Ordinal);
    }
}
