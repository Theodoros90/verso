using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;

namespace Verso.Application.Executions;

/// <summary>One file attached to an outgoing email.</summary>
public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

/// <summary>
/// An outgoing email. Subject and addresses are already validated (single line, plain addresses). <see cref="Html"/> is
/// optional: when set the message is multipart with <see cref="Text"/> as the plain alternative. <see cref="Relay"/> is the key of the
/// relay the administrator named in the Worker's configuration (ADR 0006 addendum 1); null or empty means <see cref="EmailRelays.DefaultKey"/>.
/// </summary>
public sealed record EmailMessage(
    IReadOnlyList<string> To, string Subject, string Text, string? Html, IReadOnlyList<EmailAttachment> Attachments, string? Relay = null);

/// <summary>Rules for the relay key of a Send email step (ADR 0006 addendum 1).</summary>
public static partial class EmailRelays
{
    /// <summary>The relay used when a step names none: the top-level <c>Email</c> settings.</summary>
    public const string DefaultKey = "default";

    /// <summary>A relay key as typed in the step and as named under <c>Email:Relays</c>: lower-case letters, digits and hyphens.</summary>
    public const string KeyPattern = "^[a-z0-9][a-z0-9-]{1,63}$";

    public static bool IsValidKey(string? key) => key is not null && KeyRegex().IsMatch(key);

    /// <summary>The key a step uses: its own, trimmed, or <see cref="DefaultKey"/> when empty.</summary>
    public static string Resolve(string? key) => string.IsNullOrWhiteSpace(key) ? DefaultKey : key.Trim();

    [GeneratedRegex(KeyPattern, RegexOptions.CultureInvariant)]
    private static partial Regex KeyRegex();
}

/// <summary>An email that can't be sent. <see cref="Retryable"/>: a later attempt may work (server busy, network); otherwise it won't (not configured, refused address).</summary>
public sealed class EmailSendException(string code, string message, bool retryable, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

/// <summary>
/// Delivers email through the bank's SMTP relay (ADR 0006). Implemented in Infrastructure and registered in the Worker only;
/// the API never sends mail. Credentials come from configuration, never from a workflow.
/// </summary>
public interface IEmailSender
{
    /// <returns>The message id the relay was given.</returns>
    Task<string> SendAsync(EmailMessage message, CancellationToken ct);
}

/// <summary>Reads the recipients of a Send email step: addresses separated by commas or semicolons.</summary>
public static class EmailAddresses
{
    public const int MaxRecipients = 10;

    /// <summary>Plain addresses only (no display names, no line breaks), at most <see cref="MaxRecipients"/>. Empty text gives no addresses and no problem.</summary>
    public static (IReadOnlyList<string> Addresses, string? Problem) Parse(string? text)
    {
        var result = new List<string>();
        foreach (var raw in (text ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Any(char.IsControl) || raw.Any(char.IsWhiteSpace) || !MailAddress.TryCreate(raw, out var parsed) || parsed.Address != raw || !parsed.Host.Contains('.', StringComparison.Ordinal))
            {
                return ([], "A recipient is not a valid email address.");
            }

            if (!result.Contains(raw, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(raw);
            }
        }

        return result.Count > MaxRecipients ? ([], $"At most {MaxRecipients} recipients are allowed.") : (result, null);
    }

    /// <summary>One line, so data can't start another mail header.</summary>
    public static string OneLine(string text)
    {
        var line = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            line.Append(char.IsControl(c) ? ' ' : c);
        }

        return line.ToString().Trim();
    }
}
