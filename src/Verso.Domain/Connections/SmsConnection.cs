namespace Verso.Domain.Connections;

/// <summary>
/// An SMS connection created in Configuration, Connections (ADR 0018). The provider is Brevo. Deactivated, never deleted. The API key is
/// never held in clear: <see cref="SecretProtected"/> is the encrypted text. Field rules are enforced by the service.
/// </summary>
public sealed class SmsConnection
{
    public const string BrevoProvider = "brevo";
    public const string Marketing = "marketing";
    public const string Transactional = "transactional";

    private SmsConnection() { } // EF Core

    public Guid Id { get; private set; }
    public string Key { get; private set; } = "";
    public string Name { get; private set; } = "";
    /// <summary>Always <see cref="BrevoProvider"/> for now.</summary>
    public string Provider { get; private set; } = BrevoProvider;
    /// <summary>The provider's base address, https only, no query (for example <c>https://api.brevo.com/v3</c>).</summary>
    public string BaseUrl { get; private set; } = "";
    /// <summary>The sender name (up to 11 letters and digits) or number (up to 15 digits).</summary>
    public string Sender { get; private set; } = "";
    /// <summary><see cref="Marketing"/> or <see cref="Transactional"/>.</summary>
    public string MessageType { get; private set; } = Transactional;
    public bool UnicodeEnabled { get; private set; }
    /// <summary>Optional text the provider puts in front of the message; empty when none.</summary>
    public string OrganisationPrefix { get; private set; } = "";
    public int TimeoutSeconds { get; private set; }
    /// <summary>Whether a Send SMS workflow step may use this connection; off by default, switched on by an administrator.</summary>
    public bool AllowSending { get; private set; }
    /// <summary>The country dialling codes (digits, comma separated, no "+") a step may send to; empty means no restriction by country.</summary>
    public string AllowedCountryPrefixes { get; private set; } = "";
    public string Description { get; private set; } = "";
    public bool IsActive { get; private set; }
    public string? SecretProtected { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset UpdatedAt { get; private set; }
    public string UpdatedBy { get; private set; } = "";

    public static SmsConnection Create(
        string key, string name, string baseUrl, string sender, string messageType, bool unicodeEnabled, string organisationPrefix,
        int timeoutSeconds, string description, string actor, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        Key = key,
        Name = name.Trim(),
        Provider = BrevoProvider,
        BaseUrl = baseUrl.Trim(),
        Sender = sender.Trim(),
        MessageType = messageType,
        UnicodeEnabled = unicodeEnabled,
        OrganisationPrefix = organisationPrefix.Trim(),
        TimeoutSeconds = timeoutSeconds,
        Description = description.Trim(),
        IsActive = true,
        CreatedAt = now,
        CreatedBy = actor,
        UpdatedAt = now,
        UpdatedBy = actor,
    };

    public void Update(
        string name, string baseUrl, string sender, string messageType, bool unicodeEnabled, string organisationPrefix,
        int timeoutSeconds, string description, string actor, DateTimeOffset now)
    {
        Name = name.Trim();
        BaseUrl = baseUrl.Trim();
        Sender = sender.Trim();
        MessageType = messageType;
        UnicodeEnabled = unicodeEnabled;
        OrganisationPrefix = organisationPrefix.Trim();
        TimeoutSeconds = timeoutSeconds;
        Description = description.Trim();
        Touch(actor, now);
    }

    public void SetSending(bool allow, string prefixes, string actor, DateTimeOffset now)
    {
        AllowSending = allow;
        AllowedCountryPrefixes = prefixes;
        Touch(actor, now);
    }

    /// <summary>Stores the encrypted API key.</summary>
    public void SetSecret(string protectedSecret, string actor, DateTimeOffset now)
    {
        SecretProtected = protectedSecret;
        Touch(actor, now);
    }

    public void Deactivate(string actor, DateTimeOffset now)
    {
        IsActive = false;
        Touch(actor, now);
    }

    public void Activate(string actor, DateTimeOffset now)
    {
        IsActive = true;
        Touch(actor, now);
    }

    private void Touch(string actor, DateTimeOffset now)
    {
        UpdatedAt = now;
        UpdatedBy = actor;
    }
}

public enum SmsConnectionTestStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
}

/// <summary>
/// A request to test a saved SMS connection: the Worker sends ONE real SMS to the number typed in the test. The number is kept only encrypted
/// (<see cref="RecipientProtected"/>) until the Worker has used it, then removed; the message text and the API's answer are never kept, only the
/// HTTP status, the time, the provider's message id, the number of SMS parts and the remaining credits. Rows are deleted after 24 hours.
/// </summary>
public sealed class SmsConnectionTest
{
    public const int MaxMessageLength = 500;

    private SmsConnectionTest() { } // EF Core

    public Guid Id { get; private set; }
    public Guid ConnectionId { get; private set; }
    public string ConnectionKey { get; private set; } = "";
    public SmsConnectionTestStatus Status { get; private set; }
    /// <summary>The recipient, AES-GCM encrypted; null once the test has run.</summary>
    public string? RecipientProtected { get; private set; }
    public string? Code { get; private set; }
    public string? Message { get; private set; }
    public int? HttpStatus { get; private set; }
    public int? ElapsedMs { get; private set; }
    public string? MessageId { get; private set; }
    public int? SmsCount { get; private set; }
    public decimal? RemainingCredits { get; private set; }
    public string RequestedBy { get; private set; } = "";
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsFinished => Status is SmsConnectionTestStatus.Succeeded or SmsConnectionTestStatus.Failed;

    public static SmsConnectionTest Create(Guid connectionId, string connectionKey, string actor, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        ConnectionId = connectionId,
        ConnectionKey = connectionKey,
        Status = SmsConnectionTestStatus.Queued,
        RequestedBy = actor,
        RequestedAt = now,
    };

    /// <summary>Stores the encrypted recipient (its encryption context needs this test's id, so it is attached after <see cref="Create"/>).</summary>
    public void AttachRecipient(string protectedRecipient) => RecipientProtected = protectedRecipient;

    public void Start() => Status = SmsConnectionTestStatus.Running;

    public void Succeed(int? httpStatus, int? elapsedMs, string? messageId, int? smsCount, decimal? remainingCredits, DateTimeOffset now)
    {
        Status = SmsConnectionTestStatus.Succeeded;
        Record(httpStatus, elapsedMs, messageId, smsCount, remainingCredits);
        CompletedAt = now;
    }

    public void Fail(string code, string message, int? httpStatus, int? elapsedMs, DateTimeOffset now)
    {
        Status = SmsConnectionTestStatus.Failed;
        Code = code.Length > 64 ? code[..64] : code;
        Message = message.Length > MaxMessageLength ? message[..MaxMessageLength] : message;
        Record(httpStatus, elapsedMs, null, null, null);
        CompletedAt = now;
    }

    private void Record(int? httpStatus, int? elapsedMs, string? messageId, int? smsCount, decimal? remainingCredits)
    {
        RecipientProtected = null; // the number is not kept once the test has run
        HttpStatus = httpStatus;
        ElapsedMs = elapsedMs;
        MessageId = messageId is { Length: > 100 } ? messageId[..100] : messageId;
        SmsCount = smsCount;
        RemainingCredits = remainingCredits;
    }
}
