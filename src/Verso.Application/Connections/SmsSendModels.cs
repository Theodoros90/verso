using System.Globalization;
using System.Text;

namespace Verso.Application.Connections;

/// <summary>
/// One SMS of the Send SMS step: the connection's key, the number (international format; the service re-reads it with
/// <see cref="SmsRules.TryNormaliseRecipient"/>, so a caller can't bypass the format), the text and the tag Verso builds (<c>verso-</c> plus the
/// execution and iteration, no personal data). The number and the text are personal data: never logged, audited or printed.
/// </summary>
public sealed record SmsSendRequest(string ConnectionKey, string RecipientDigits, string Text, string Tag)
{
    /// <summary>The number, the text and the tag are never printed (a record's ToString would otherwise list them).</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"ConnectionKey = {ConnectionKey}, RecipientDigits = [redacted], Text = [{Text?.Length ?? 0} characters redacted], Tag = [redacted]");
        return true;
    }
}

/// <summary>
/// The outcome of one Send SMS. <see cref="Message"/> is Verso's own wording for <see cref="Code"/>, never the provider's text, the number, the text or a
/// secret. Every failure is final: Verso never retries a message (there is no retry flag). <see cref="OutcomeUnknown"/> says whether the message may
/// have been sent (the request may have reached the provider); its message is then always <see cref="SmsSendMessages.OutcomeUnknown"/>.
/// <see cref="MessageId"/> and <see cref="SmsParts"/> come from the provider's answer when it has them, otherwise null.
/// </summary>
public sealed record SmsSendResult(string? Code, string? Message, bool OutcomeUnknown, string? MessageId, int? SmsParts, long DurationMs)
{
    public bool Ok => Code is null;

    public static SmsSendResult Success(string? messageId, int? smsParts, long durationMs) => new(null, null, false, messageId, smsParts, durationMs);

    /// <summary>A failure: with <paramref name="outcomeUnknown"/> the fixed unknown wording, otherwise Verso's wording for <paramref name="code"/>.</summary>
    public static SmsSendResult Failure(string code, bool outcomeUnknown, long durationMs = 0) =>
        new(code, outcomeUnknown ? SmsSendMessages.OutcomeUnknown : SmsSendMessages.For(code), outcomeUnknown, null, null, durationMs);
}

/// <summary>Worker side of the Send SMS step: checks the request and the connection, decrypts the key and sends one SMS (Worker only).</summary>
public interface ISmsSend
{
    Task<SmsSendResult> RunAsync(SmsSendRequest request, CancellationToken ct);
}

/// <summary>
/// Sends the one SMS with <c>HttpClient</c> (Infrastructure, Worker only): https only, no redirects, no blocked addresses, one deadline.
/// Internal on purpose: it does NOT check the "Allow sending" switch, the message type, the allowed countries or the hosts. Only
/// <see cref="SmsSendService"/> may call it; everything else goes through <see cref="ISmsSend"/>.
/// </summary>
internal interface ISmsSender
{
    Task<SmsSendResult> SendAsync(SmsConnectionSettings settings, SmsSendRequest request, CancellationToken ct);
}

/// <summary>Verso's own wording for the Send SMS codes (the connection test's wording, extended in <see cref="SmsProbeMessages"/>).</summary>
public static class SmsSendMessages
{
    /// <summary>Shown whenever the request may have reached the provider: Verso can't tell whether the message went out.</summary>
    public const string OutcomeUnknown =
        "The outcome is unknown: the message may or may not have been sent. Check the SMS provider's log before running this step again.";

    public static string For(string code) => SmsProbeMessages.For(code);
}
