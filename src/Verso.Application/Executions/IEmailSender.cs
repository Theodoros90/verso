using System.Net.Mail;
using System.Text;

namespace Verso.Application.Executions;

/// <summary>One file attached to an outgoing email.</summary>
public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

/// <summary>
/// An outgoing email. Subject and addresses are already validated (single line, plain addresses). <see cref="Html"/> is
/// optional: when set the message is multipart with <see cref="Text"/> as the plain alternative.
/// </summary>
public sealed record EmailMessage(
    IReadOnlyList<string> To, string Subject, string Text, string? Html, IReadOnlyList<EmailAttachment> Attachments);

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
