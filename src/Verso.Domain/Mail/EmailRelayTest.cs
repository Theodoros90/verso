namespace Verso.Domain.Mail;

public enum EmailRelayTestStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
}

/// <summary>
/// A request to send one test message through a saved email relay (ADR 0015). The API records it, the Worker runs it. The recipient
/// is held only until the test finishes (then cleared), and rows are deleted after 24 hours. The result never contains an address.
/// </summary>
public sealed class EmailRelayTest
{
    public const int MaxMessageLength = 500;

    private EmailRelayTest() { } // EF Core

    public Guid Id { get; private set; }
    public Guid RelayId { get; private set; }
    public string RelayKey { get; private set; } = "";
    /// <summary>Where the test goes; cleared as soon as the test finishes.</summary>
    public string? ToAddress { get; private set; }
    public EmailRelayTestStatus Status { get; private set; }
    /// <summary>The error code when the test failed (for example <c>email.relay_unavailable</c>).</summary>
    public string? Code { get; private set; }
    public string? Message { get; private set; }
    public string RequestedBy { get; private set; } = "";
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsFinished => Status is EmailRelayTestStatus.Succeeded or EmailRelayTestStatus.Failed;

    public static EmailRelayTest Create(Guid relayId, string relayKey, string toAddress, string actor, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        RelayId = relayId,
        RelayKey = relayKey,
        ToAddress = toAddress,
        Status = EmailRelayTestStatus.Queued,
        RequestedBy = actor,
        RequestedAt = now,
    };

    public void Start() => Status = EmailRelayTestStatus.Running;

    public void Succeed(DateTimeOffset now)
    {
        Status = EmailRelayTestStatus.Succeeded;
        Finish(now);
    }

    public void Fail(string code, string message, DateTimeOffset now)
    {
        Status = EmailRelayTestStatus.Failed;
        Code = code.Length > 64 ? code[..64] : code;
        Message = message.Length > MaxMessageLength ? message[..MaxMessageLength] : message;
        Finish(now);
    }

    private void Finish(DateTimeOffset now)
    {
        CompletedAt = now;
        ToAddress = null;
    }
}
