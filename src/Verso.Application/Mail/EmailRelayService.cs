using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Verso.Application.Executions;
using Verso.Domain.Audit;
using Verso.Domain.Mail;

namespace Verso.Application.Mail;

/// <summary>
/// Email relay use cases (ADR 0015): create, update, deactivate and activate. Every change is audited with the changed field names,
/// never a value and never the password. The password is encrypted before it reaches the database and is never returned.
/// </summary>
public sealed partial class EmailRelayService(IVersoDbContext db, ICurrentUser user, TimeProvider clock, ISecretProtector secrets)
{
    public const string EntityType = "EmailRelay";
    private const int MaxPasswordLength = 256;

    public static class Actions
    {
        public const string Created = "EmailRelayCreated";
        public const string Updated = "EmailRelayUpdated";
        public const string PasswordChanged = "EmailRelayPasswordChanged";
        public const string Deactivated = "EmailRelayDeactivated";
        public const string Activated = "EmailRelayActivated";
    }

    public async Task<IReadOnlyList<EmailRelayDto>> ListAsync(bool includeInactive, CancellationToken ct)
    {
        var query = db.EmailRelays.AsNoTracking();
        if (!includeInactive)
        {
            query = query.Where(r => r.IsActive);
        }

        return (await query.OrderBy(r => r.Key).ToListAsync(ct)).Select(ToDto).ToList();
    }

    /// <summary>Key and name of every relay, for the dropdown of the Send email step. Any signed-in role may read it; it holds no server or account detail.</summary>
    public async Task<IReadOnlyList<EmailRelayOptionDto>> ListOptionsAsync(CancellationToken ct) =>
        await db.EmailRelays.AsNoTracking().OrderBy(r => r.Name).Select(r => new EmailRelayOptionDto(r.Key, r.Name, r.IsActive)).ToListAsync(ct);

    public async Task<Result<EmailRelayDto>> CreateAsync(SaveEmailRelayRequest request, CancellationToken ct)
    {
        var errors = Validate(request, creating: true, out var fields);
        if (errors is not null)
        {
            return AppError.Validation(errors);
        }

        var key = request.Key!.Trim();
        if (await db.EmailRelays.AnyAsync(r => r.Key == key, ct))
        {
            return KeyTaken(key);
        }

        var now = clock.GetUtcNow();
        var relay = EmailRelay.Create(key, fields!.Name, fields.Host, fields.Port, fields.EnableSsl, fields.UserName, fields.From, fields.FromName, fields.Timeout, fields.Domains, user.Id, now);
        var changedPassword = false;
        if (!string.IsNullOrEmpty(request.Password))
        {
            relay.SetPassword(secrets.Protect(request.Password, EmailRelayContexts.For(key)), user.Id, now);
            changedPassword = true;
        }

        db.EmailRelays.Add(relay);
        Audit(Actions.Created, relay.Id, now, new { key, name = fields.Name, hasPassword = changedPassword });
        if (changedPassword)
        {
            Audit(Actions.PasswordChanged, relay.Id, now, new { key, action = "set" });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await db.EmailRelays.AnyAsync(r => r.Key == key, CancellationToken.None))
            {
                return KeyTaken(key);
            }

            throw;
        }

        return ToDto(relay);
    }

    public async Task<Result<EmailRelayDto>> UpdateAsync(Guid id, SaveEmailRelayRequest request, CancellationToken ct)
    {
        if (await db.EmailRelays.SingleOrDefaultAsync(r => r.Id == id, ct) is not { } relay)
        {
            return NotFound(id);
        }

        var errors = Validate(request, creating: false, out var fields);
        if (errors is not null)
        {
            return AppError.Validation(errors);
        }

        var now = clock.GetUtcNow();
        var changed = new List<string>();
        void Note(string name, bool differs)
        {
            if (differs)
            {
                changed.Add(name);
            }
        }

        Note("name", relay.Name != fields!.Name);
        Note("host", relay.Host != fields.Host);
        Note("port", relay.Port != fields.Port);
        Note("security", relay.EnableSsl != fields.EnableSsl);
        Note("userName", relay.UserName != fields.UserName);
        Note("from", relay.FromAddress != fields.From);
        Note("fromName", relay.FromName != fields.FromName);
        Note("timeout", relay.TimeoutSeconds != fields.Timeout);
        Note("allowedRecipientDomains", relay.AllowedRecipientDomains != fields.Domains);
        if (changed.Count > 0)
        {
            relay.Update(fields.Name, fields.Host, fields.Port, fields.EnableSsl, fields.UserName, fields.From, fields.FromName, fields.Timeout, fields.Domains, user.Id, now);
            Audit(Actions.Updated, id, now, new { key = relay.Key, changed });
        }

        if (request.ClearPassword == true && relay.PasswordProtected is not null)
        {
            relay.SetPassword(null, user.Id, now);
            Audit(Actions.PasswordChanged, id, now, new { key = relay.Key, action = "cleared" });
        }
        else if (!string.IsNullOrEmpty(request.Password))
        {
            relay.SetPassword(secrets.Protect(request.Password, EmailRelayContexts.For(relay.Key)), user.Id, now);
            Audit(Actions.PasswordChanged, id, now, new { key = relay.Key, action = "set" });
        }

        await db.SaveChangesAsync(ct);
        return ToDto(relay);
    }

    public Task<Result<EmailRelayDto>> DeactivateAsync(Guid id, CancellationToken ct) => SetActiveAsync(id, false, ct);

    public Task<Result<EmailRelayDto>> ActivateAsync(Guid id, CancellationToken ct) => SetActiveAsync(id, true, ct);

    private async Task<Result<EmailRelayDto>> SetActiveAsync(Guid id, bool active, CancellationToken ct)
    {
        if (await db.EmailRelays.SingleOrDefaultAsync(r => r.Id == id, ct) is not { } relay)
        {
            return NotFound(id);
        }

        if (relay.IsActive != active) // repeating the same change is a no-op: no audit row
        {
            var now = clock.GetUtcNow();
            if (active)
            {
                relay.Activate(user.Id, now);
            }
            else
            {
                relay.Deactivate(user.Id, now);
            }

            Audit(active ? Actions.Activated : Actions.Deactivated, id, now, new { key = relay.Key });
            await db.SaveChangesAsync(ct);
        }

        return ToDto(relay);
    }

    private sealed record Fields(string Name, string Host, int Port, bool EnableSsl, string UserName, string From, string FromName, int Timeout, string Domains);

    private Dictionary<string, string[]>? Validate(SaveEmailRelayRequest r, bool creating, out Fields? fields)
    {
        fields = null;
        var errors = new Dictionary<string, string[]>();
        void Fail(string field, string message) => errors[field] = [message];

        if (creating && !EmailRelays.IsValidKey(r.Key?.Trim()))
        {
            Fail("key", "A key of 2–64 characters is required: lower-case letters, digits and hyphens.");
        }

        var name = (r.Name ?? "").Trim();
        if (name.Length is 0 or > 100)
        {
            Fail("name", "A name of 1–100 characters is required.");
        }

        var host = (r.Host ?? "").Trim();
        if (host.Length is 0 or > 255 || host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '\\' or '@'))
        {
            Fail("host", "A server address (host name) is required, such as smtp.example.com.");
        }

        var port = r.Port ?? 587;
        if (port is < 1 or > 65535)
        {
            Fail("port", "The port must be 1–65535.");
        }

        var timeout = r.TimeoutSeconds ?? 30;
        if (timeout is < 1 or > 120)
        {
            Fail("timeoutSeconds", "The timeout must be 1–120 seconds.");
        }

        var userName = (r.UserName ?? "").Trim();
        if (userName.Length > 200 || userName.Any(char.IsControl))
        {
            Fail("userName", "The user name can be at most 200 characters.");
        }

        var from = (r.FromAddress ?? "").Trim();
        var (addresses, problem) = EmailAddresses.Parse(from);
        if (problem is not null || addresses.Count != 1)
        {
            Fail("fromAddress", "The sender must be one plain email address.");
        }

        var fromName = (r.FromName ?? "Verso").Trim();
        if (fromName.Length > 100 || fromName.Any(char.IsControl))
        {
            Fail("fromName", "The sender name can be at most 100 characters.");
        }

        var domains = (r.AllowedRecipientDomains ?? []).Select(d => d.Trim().ToLowerInvariant()).Where(d => d.Length > 0).Distinct().ToList();
        if (domains.Count > 50 || domains.Any(d => !DomainPattern().IsMatch(d)))
        {
            Fail("allowedRecipientDomains", "Each recipient domain must look like example.com (at most 50).");
        }

        if (r.Password is not null && r.Password.Length > 0)
        {
            if (r.Password.Length > MaxPasswordLength || r.Password.Any(char.IsControl))
            {
                Fail("password", $"The password can be at most {MaxPasswordLength} characters and has no control characters.");
            }
            else if (!secrets.IsConfigured)
            {
                Fail("password", "A password can't be stored yet: the administrator must set Secrets:MasterKey for the API and the Worker (see the runbook).");
            }
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        fields = new Fields(name, host, port, r.EnableSsl ?? true, userName, from, fromName, timeout, string.Join(',', domains));
        return null;
    }

    private static EmailRelayDto ToDto(EmailRelay r) => new(
        r.Id, r.Key, r.Name, r.Host, r.Port, r.EnableSsl, r.UserName, r.FromAddress, r.FromName, r.TimeoutSeconds,
        r.AllowedRecipientDomains.Length == 0 ? [] : r.AllowedRecipientDomains.Split(','), r.IsActive, r.PasswordProtected is not null, r.UpdatedAt, r.UpdatedBy);

    private void Audit(string action, Guid id, DateTimeOffset now, object details) =>
        db.AuditEntries.Add(AuditEntry.Record(
            action, EntityType, id.ToString(), user.Id, user.Name, now,
            JsonSerializer.Serialize(details), Activity.Current?.TraceId.ToHexString()));

    private static AppError NotFound(Guid id) => AppError.NotFound("email_relay.not_found", $"Email relay {id} was not found.");

    private static AppError KeyTaken(string key) => AppError.Conflict("email_relay.key_taken", $"An email relay with the key '{key}' already exists.");

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();
}
