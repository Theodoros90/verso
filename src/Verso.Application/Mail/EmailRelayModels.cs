using Verso.Application.Executions;

namespace Verso.Application.Mail;

/// <summary>
/// Creates or updates an email relay. <see cref="Key"/> is used on create only (a relay's key never changes).
/// <see cref="Password"/>: null keeps the stored one, a value replaces it. <see cref="ClearPassword"/> removes it. The password is never returned.
/// </summary>
public sealed record SaveEmailRelayRequest(
    string? Key, string? Name, string? Host, int? Port, bool? EnableSsl, string? UserName, string? FromAddress, string? FromName,
    int? TimeoutSeconds, string[]? AllowedRecipientDomains, string? Password, bool? ClearPassword);

/// <summary>An email relay as the Configuration screen sees it. There is deliberately no password: only whether one is stored.</summary>
public sealed record EmailRelayDto(
    Guid Id, string Key, string Name, string Host, int Port, bool EnableSsl, string UserName, string FromAddress, string FromName,
    int TimeoutSeconds, string[] AllowedRecipientDomains, bool IsActive, bool HasPassword, DateTimeOffset UpdatedAt, string UpdatedBy);

/// <summary>What the Worker needs to send through a relay, password decrypted in memory only.</summary>
public sealed record EmailRelaySettings(
    string Host, int Port, bool EnableSsl, string UserName, string Password, string From, string FromName,
    IReadOnlyList<string> AllowedRecipientDomains, TimeSpan Timeout);

/// <summary>Finds the relay a Send email step names: the database first (ADR 0015), then the Worker's configuration (ADR 0006).</summary>
public interface IEmailRelayStore
{
    /// <returns>The settings, or null when there is no such relay or it is deactivated.</returns>
    Task<EmailRelaySettings?> FindAsync(string key, CancellationToken ct);
}

/// <summary>
/// Encrypts the passwords Verso stores (ADR 0015). The master key is not in the database: the administrator provides it
/// (<c>Secrets:MasterKey</c>) to the API and every Worker.
/// </summary>
public interface ISecretProtector
{
    /// <summary>False when no usable master key is set: nothing can be stored or read.</summary>
    bool IsConfigured { get; }

    /// <param name="context">Binds the secret to what it belongs to (for a relay password, <c>email-relay:{key}</c>), so a stored value can't be moved to another row.</param>
    string Protect(string secret, string context);

    /// <returns>The secret, or null when the value is damaged, was made for another context, or the key is wrong.</returns>
    string? Unprotect(string protectedValue, string context);
}

public static class EmailRelayContexts
{
    public static string For(string key) => $"email-relay:{key}";
}

/// <summary>A relay as a workflow author picks it in a Send email step: its key and name only, nothing about the server or the account.</summary>
public sealed record EmailRelayOptionDto(string Key, string Name, bool IsActive);
