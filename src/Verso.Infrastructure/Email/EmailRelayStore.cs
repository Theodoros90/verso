using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Verso.Application;
using Verso.Application.Executions;
using Verso.Application.Mail;

namespace Verso.Infrastructure.Email;

/// <summary>
/// Finds the relay a Send email step names (ADR 0015): the database first (relays managed in the Configuration screen, password
/// decrypted here, in memory), then the Worker's <c>Email</c> configuration (ADR 0006; the top-level section is <c>default</c>).
/// A relay in the database wins over one in configuration with the same key; a deactivated one is "unknown", not a fall-back.
/// </summary>
public sealed class EmailRelayStore(IOptions<EmailOptions> options, ISecretProtector? secrets = null, IServiceScopeFactory? scopes = null) : IEmailRelayStore
{
    public async Task<EmailRelaySettings?> FindAsync(string key, CancellationToken ct)
    {
        if (scopes is not null)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IVersoDbContext>();
            if (await db.EmailRelays.AsNoTracking().SingleOrDefaultAsync(r => r.Key == key, ct) is { } row)
            {
                if (!row.IsActive)
                {
                    return null;
                }

                var password = "";
                if (row.PasswordProtected is not null)
                {
                    password = secrets?.Unprotect(row.PasswordProtected, EmailRelayContexts.For(row.Key))
                        ?? throw new EmailSendException("email.password_unreadable",
                            $"The stored password of email relay '{key}' can't be read: the Worker's Secrets:MasterKey is missing or differs from the API's. Set it, or type the password again.", retryable: false);
                }

                return new EmailRelaySettings(
                    row.Host, row.Port, row.EnableSsl, row.UserName, password, row.FromAddress, row.FromName,
                    row.AllowedRecipientDomains.Length == 0 ? [] : row.AllowedRecipientDomains.Split(','), TimeSpan.FromSeconds(row.TimeoutSeconds));
            }
        }

        var o = options.Value;
        EmailOptions? config = key == EmailRelays.DefaultKey ? o : o.Relays.GetValueOrDefault(key);
        return config is null
            ? null
            : new EmailRelaySettings(config.Host, config.Port, config.EnableSsl, config.UserName, config.Password, config.From, config.FromName, config.AllowedRecipientDomains, config.Timeout);
    }
}
