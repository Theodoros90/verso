namespace Verso.Domain.Mail;

/// <summary>
/// An SMTP relay the Send email step can use, by its key (ADR 0015). Relays are deactivated, never deleted. The password is never held
/// in clear: <see cref="PasswordProtected"/> is the encrypted text (or null when none is stored). Field rules are enforced by the service.
/// </summary>
public sealed class EmailRelay
{
    private EmailRelay() { } // EF Core

    public Guid Id { get; private set; }
    /// <summary>The key a Send email step names ("statements"). Never changes after creation.</summary>
    public string Key { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string Host { get; private set; } = "";
    public int Port { get; private set; }
    public bool EnableSsl { get; private set; }
    public string UserName { get; private set; } = "";
    public string FromAddress { get; private set; } = "";
    public string FromName { get; private set; } = "";
    public int TimeoutSeconds { get; private set; }
    /// <summary>Comma-separated domains that may receive mail through this relay; empty means no limit.</summary>
    public string AllowedRecipientDomains { get; private set; } = "";
    public bool IsActive { get; private set; }
    public string? PasswordProtected { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset UpdatedAt { get; private set; }
    public string UpdatedBy { get; private set; } = "";

    public static EmailRelay Create(
        string key, string name, string host, int port, bool enableSsl, string userName, string fromAddress, string fromName,
        int timeoutSeconds, string allowedRecipientDomains, string actor, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        Key = key,
        Name = name.Trim(),
        Host = host.Trim(),
        Port = port,
        EnableSsl = enableSsl,
        UserName = userName.Trim(),
        FromAddress = fromAddress.Trim(),
        FromName = fromName.Trim(),
        TimeoutSeconds = timeoutSeconds,
        AllowedRecipientDomains = allowedRecipientDomains,
        IsActive = true,
        CreatedAt = now,
        CreatedBy = actor,
        UpdatedAt = now,
        UpdatedBy = actor,
    };

    public void Update(
        string name, string host, int port, bool enableSsl, string userName, string fromAddress, string fromName,
        int timeoutSeconds, string allowedRecipientDomains, string actor, DateTimeOffset now)
    {
        Name = name.Trim();
        Host = host.Trim();
        Port = port;
        EnableSsl = enableSsl;
        UserName = userName.Trim();
        FromAddress = fromAddress.Trim();
        FromName = fromName.Trim();
        TimeoutSeconds = timeoutSeconds;
        AllowedRecipientDomains = allowedRecipientDomains;
        Touch(actor, now);
    }

    /// <summary>Stores the encrypted password, or removes it with null.</summary>
    public void SetPassword(string? protectedPassword, string actor, DateTimeOffset now)
    {
        PasswordProtected = protectedPassword;
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
