using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Verso.Application.Mail;
using Verso.Domain.Audit;
using Verso.Domain.Connections;

namespace Verso.Application.Connections;

/// <summary>
/// SMS connection use cases (ADR 0018): create, update, deactivate and activate. Every change is audited with changed field names, never a
/// value and never the API key, which is encrypted before it reaches the database and never returned.
/// </summary>
public sealed class SmsConnectionService(IVersoDbContext db, ICurrentUser user, TimeProvider clock, ISecretProtector secrets)
{
    public const string EntityType = "SmsConnection";
    private const int MaxSecretLength = 2000;

    public static class Actions
    {
        public const string Created = "SmsConnectionCreated";
        public const string Updated = "SmsConnectionUpdated";
        public const string SecretChanged = "SmsConnectionSecretChanged";
        public const string Deactivated = "SmsConnectionDeactivated";
        public const string Activated = "SmsConnectionActivated";
        public const string SendingChanged = "SmsConnectionSendingChanged";
    }

    public async Task<IReadOnlyList<SmsConnectionDto>> ListAsync(bool includeInactive, CancellationToken ct)
    {
        var query = db.SmsConnections.AsNoTracking();
        if (!includeInactive)
        {
            query = query.Where(c => c.IsActive);
        }

        return (await query.OrderBy(c => c.Key).ToListAsync(ct)).Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<SmsConnectionOptionDto>> ListOptionsAsync(CancellationToken ct) =>
        await db.SmsConnections.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name)
            .Select(c => new SmsConnectionOptionDto(c.Key, c.Name, c.MessageType, c.AllowSending)).ToListAsync(ct);

    public async Task<Result<SmsConnectionDto>> CreateAsync(SaveSmsConnectionRequest request, CancellationToken ct)
    {
        var errors = Validate(request, creating: true, out var f);
        if (errors is not null)
        {
            return AppError.Validation(errors);
        }

        var key = request.Key!.Trim();
        if (await db.SmsConnections.AnyAsync(c => c.Key == key, ct))
        {
            return KeyTaken(key);
        }

        var now = clock.GetUtcNow();
        var connection = SmsConnection.Create(key, f!.Name, f.BaseUrl, f.Sender, f.MessageType, f.Unicode, f.Prefix, f.Timeout, f.Description, user.Id, now);
        connection.SetSecret(secrets.Protect(request.Secret!, SmsConnectionContexts.For(key)), user.Id, now);
        var allowSending = request.AllowSending == true;
        if (allowSending || f.CountryPrefixes.Length > 0)
        {
            connection.SetSending(allowSending, f.CountryPrefixes, user.Id, now);
        }

        db.SmsConnections.Add(connection);
        Audit(Actions.Created, connection.Id, now, new { key, name = f.Name, messageType = f.MessageType });
        Audit(Actions.SecretChanged, connection.Id, now, new { key, action = "set" });
        if (allowSending)
        {
            Audit(Actions.SendingChanged, connection.Id, now, new { key, allowSending });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await db.SmsConnections.AnyAsync(c => c.Key == key, CancellationToken.None))
            {
                return KeyTaken(key);
            }

            throw;
        }

        return ToDto(connection);
    }

    public async Task<Result<SmsConnectionDto>> UpdateAsync(Guid id, SaveSmsConnectionRequest request, CancellationToken ct)
    {
        if (await db.SmsConnections.SingleOrDefaultAsync(c => c.Id == id, ct) is not { } connection)
        {
            return NotFound(id);
        }

        var errors = Validate(request, creating: false, out var f);
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

        Note("name", connection.Name != f!.Name);
        Note("baseUrl", connection.BaseUrl != f.BaseUrl);
        Note("sender", connection.Sender != f.Sender);
        Note("messageType", connection.MessageType != f.MessageType);
        Note("unicodeEnabled", connection.UnicodeEnabled != f.Unicode);
        Note("organisationPrefix", connection.OrganisationPrefix != f.Prefix);
        Note("timeout", connection.TimeoutSeconds != f.Timeout);
        Note("description", connection.Description != f.Description);
        var sendingChanged = request.AllowSending is { } wanted && wanted != connection.AllowSending;
        var newPrefixes = request.AllowedCountryPrefixes is null ? connection.AllowedCountryPrefixes : f.CountryPrefixes;
        Note("allowSending", sendingChanged);
        Note("allowedCountryPrefixes", connection.AllowedCountryPrefixes != newPrefixes);
        if (changed.Count > 0)
        {
            connection.Update(f.Name, f.BaseUrl, f.Sender, f.MessageType, f.Unicode, f.Prefix, f.Timeout, f.Description, user.Id, now);
            connection.SetSending(sendingChanged ? request.AllowSending!.Value : connection.AllowSending, newPrefixes, user.Id, now);
            Audit(Actions.Updated, id, now, new { key = connection.Key, changed });
        }

        if (sendingChanged)
        {
            Audit(Actions.SendingChanged, id, now, new { key = connection.Key, allowSending = connection.AllowSending });
        }

        if (!string.IsNullOrEmpty(request.Secret))
        {
            connection.SetSecret(secrets.Protect(request.Secret, SmsConnectionContexts.For(connection.Key)), user.Id, now);
            Audit(Actions.SecretChanged, id, now, new { key = connection.Key, action = "set" });
        }

        await db.SaveChangesAsync(ct);
        return ToDto(connection);
    }

    public Task<Result<SmsConnectionDto>> DeactivateAsync(Guid id, CancellationToken ct) => SetActiveAsync(id, false, ct);

    public Task<Result<SmsConnectionDto>> ActivateAsync(Guid id, CancellationToken ct) => SetActiveAsync(id, true, ct);

    private async Task<Result<SmsConnectionDto>> SetActiveAsync(Guid id, bool active, CancellationToken ct)
    {
        if (await db.SmsConnections.SingleOrDefaultAsync(c => c.Id == id, ct) is not { } connection)
        {
            return NotFound(id);
        }

        if (connection.IsActive != active) // repeating the same change writes nothing
        {
            var now = clock.GetUtcNow();
            if (active)
            {
                connection.Activate(user.Id, now);
            }
            else
            {
                connection.Deactivate(user.Id, now);
            }

            Audit(active ? Actions.Activated : Actions.Deactivated, id, now, new { key = connection.Key });
            await db.SaveChangesAsync(ct);
        }

        return ToDto(connection);
    }

    private sealed record Fields(string Name, string BaseUrl, string Sender, string MessageType, bool Unicode, string Prefix, int Timeout, string Description, string CountryPrefixes);

    private Dictionary<string, string[]>? Validate(SaveSmsConnectionRequest r, bool creating, out Fields? fields)
    {
        fields = null;
        var errors = new Dictionary<string, string[]>();
        void Fail(string field, string message) => errors[field] = [message];

        if (creating && !DatabaseServers.IsValidKey(r.Key?.Trim()))
        {
            Fail("key", "A key of 2–64 characters is required: lower-case letters, digits and hyphens.");
        }

        var name = (r.Name ?? "").Trim();
        if (name.Length is 0 or > 100)
        {
            Fail("name", "A name of 1–100 characters is required.");
        }

        if (!string.IsNullOrWhiteSpace(r.Provider) && !string.Equals(r.Provider.Trim(), SmsConnection.BrevoProvider, StringComparison.Ordinal))
        {
            Fail("provider", "The only SMS provider supported is brevo.");
        }

        var baseUrl = string.IsNullOrWhiteSpace(r.BaseUrl) ? SmsRules.DefaultBaseUrl : r.BaseUrl.Trim();
        if (!ApiUrls.IsValidBaseUrl(baseUrl))
        {
            Fail("baseUrl", "Enter the provider's base address starting with https:// (no login, no ? query), such as https://api.brevo.com/v3.");
        }

        var sender = (r.Sender ?? "").Trim();
        if (!SmsRules.IsValidSender(sender))
        {
            Fail("sender", "Enter a sender of up to 11 letters and digits (no spaces), or a number of up to 15 digits.");
        }

        var messageType = string.IsNullOrWhiteSpace(r.MessageType) ? SmsConnection.Transactional : r.MessageType.Trim();
        if (!SmsRules.MessageTypes.Contains(messageType, StringComparer.Ordinal))
        {
            Fail("messageType", $"The type must be one of: {string.Join(", ", SmsRules.MessageTypes)}.");
            messageType = SmsConnection.Transactional;
        }

        var prefix = (r.OrganisationPrefix ?? "").Trim();
        if (prefix.Length > SmsRules.MaxPrefixLength || prefix.Any(char.IsControl))
        {
            Fail("organisationPrefix", $"The organisation prefix can be at most {SmsRules.MaxPrefixLength} characters.");
        }

        var description = (r.Description ?? "").Trim();
        if (description.Length > SmsRules.MaxDescriptionLength)
        {
            Fail("description", $"The description can be at most {SmsRules.MaxDescriptionLength} characters.");
        }

        var timeout = r.TimeoutSeconds ?? 15;
        if (timeout is < 1 or > 120)
        {
            Fail("timeoutSeconds", "The timeout must be 1–120 seconds.");
        }

        if (!SmsRules.TryNormalisePrefixes(r.AllowedCountryPrefixes, out var countryPrefixes, out var prefixProblem))
        {
            Fail("allowedCountryPrefixes", prefixProblem!);
        }
        else if (countryPrefixes.Length > SmsRules.MaxCountryPrefixesLength)
        {
            Fail("allowedCountryPrefixes", $"The country prefixes can be at most {SmsRules.MaxCountryPrefixesLength} characters.");
        }

        if (string.IsNullOrEmpty(r.Secret))
        {
            if (creating)
            {
                Fail("secret", "The provider's API key is required.");
            }
        }
        else if (r.Secret.Length > MaxSecretLength || r.Secret.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            Fail("secret", $"The API key can be at most {MaxSecretLength} characters, with no spaces or control characters.");
        }
        else if (!secrets.IsConfigured)
        {
            Fail("secret", "An API key can't be stored yet: the administrator must set Secrets:MasterKey for the API and the Worker (see the runbook).");
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        fields = new Fields(name, baseUrl, sender, messageType, r.UnicodeEnabled ?? true, prefix, timeout, description, countryPrefixes);
        return null;
    }

    private static SmsConnectionDto ToDto(SmsConnection c) => new(
        c.Id, c.Key, c.Name, c.Provider, c.BaseUrl, c.Sender, c.MessageType, c.UnicodeEnabled, c.OrganisationPrefix, c.TimeoutSeconds,
        c.Description, c.IsActive, c.SecretProtected is not null, c.UpdatedAt, c.UpdatedBy, c.AllowSending, c.AllowedCountryPrefixes);

    private void Audit(string action, Guid id, DateTimeOffset now, object details) =>
        db.AuditEntries.Add(AuditEntry.Record(
            action, EntityType, id.ToString(), user.Id, user.Name, now,
            JsonSerializer.Serialize(details), Activity.Current?.TraceId.ToHexString()));

    private static AppError NotFound(Guid id) => AppError.NotFound("sms_connection.not_found", $"SMS connection {id} was not found.");

    private static AppError KeyTaken(string key) => AppError.Conflict("sms_connection.key_taken", $"An SMS connection with the key '{key}' already exists.");
}

/// <summary>
/// API side of "Test connection" (ADR 0018): validates the number, keeps it encrypted, records the request (audited, without the number) and
/// queues the Worker. Each test sends a real SMS, so a connection can be tested once every 30 seconds at most. The API never calls the provider.
/// </summary>
public sealed class SmsConnectionTests(IVersoDbContext db, ICurrentUser user, TimeProvider clock, ISecretProtector secrets, ISmsConnectionTestJobs jobs)
{
    public const string EntityType = "SmsConnectionTest";
    public const string RequestedAction = "SmsConnectionTestRequested";
    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromHours(24);
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(30);

    public async Task<Result<SmsConnectionTestDto>> RequestAsync(Guid connectionId, RequestSmsConnectionTest request, CancellationToken ct)
    {
        if (await db.SmsConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == connectionId, ct) is not { } connection)
        {
            return AppError.NotFound("sms_connection.not_found", $"SMS connection {connectionId} was not found.");
        }

        if (!SmsRules.TryNormaliseRecipient(request.Recipient, out var digits))
        {
            return AppError.Validation(new Dictionary<string, string[]>
            {
                ["recipient"] = ["Enter the phone number in international format, such as +357 99 000000."],
            });
        }

        if (!connection.IsActive)
        {
            return AppError.Conflict("sms_connection.inactive", "This connection is switched off. Turn it on to test it.");
        }

        if (!secrets.IsConfigured)
        {
            return AppError.Conflict("sms_connection.no_master_key", "A test can't be sent yet: the administrator must set Secrets:MasterKey for the API and the Worker (see the runbook).");
        }

        var now = clock.GetUtcNow();
        var since = now - MinimumInterval;
        if (await db.SmsConnectionTests.AsNoTracking().AnyAsync(t => t.ConnectionId == connectionId && t.RequestedAt > since, ct))
        {
            return AppError.Conflict("sms_connection.test_too_soon", "A test was sent a moment ago. Each test sends a real SMS: wait 30 seconds and try again.");
        }

        var test = SmsConnectionTest.Create(connection.Id, connection.Key, user.Id, now);
        test.AttachRecipient(secrets.Protect(digits, SmsConnectionContexts.ForTest(test.Id)));
        db.SmsConnectionTests.Add(test);
        db.AuditEntries.Add(AuditEntry.Record(
            RequestedAction, EntityType, test.Id.ToString(), user.Id, user.Name, now,
            JsonSerializer.Serialize(new { connection = connection.Key }), Activity.Current?.TraceId.ToHexString())); // never the number
        await db.SaveChangesAsync(ct);

        try
        {
            jobs.Enqueue(test.Id);
        }
        catch
        {
            test.Fail("sms.test_not_queued", "The test could not be queued. Is the job storage reachable? Nothing was sent.", null, null, now);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        return ToDto(test);
    }

    public async Task<Result<SmsConnectionTestDto>> GetAsync(Guid testId, CancellationToken ct) =>
        await db.SmsConnectionTests.AsNoTracking().SingleOrDefaultAsync(t => t.Id == testId, ct) is { } test
            ? ToDto(test)
            : AppError.NotFound("sms_connection_test.not_found", $"SMS connection test {testId} was not found (tests are kept for 24 hours).");

    internal static SmsConnectionTestDto ToDto(SmsConnectionTest t) =>
        new(t.Id, t.ConnectionKey, t.Status.ToString(), t.Code, t.Message, t.HttpStatus, t.ElapsedMs, t.MessageId, t.SmsCount, t.RemainingCredits, t.RequestedAt, t.CompletedAt);
}

/// <summary>
/// Worker side: reads the saved connection, decrypts the API key and the test number in memory and asks the probe to send the one SMS. A
/// connection that can't be called is a failed test, not an error of the job. The job is never retried, so a test sends at most one message.
/// The result never contains the number, the text, the provider's answer or the key.
/// </summary>
public sealed class SmsConnectionTestRunner(IVersoDbContext db, ISecretProtector secrets, ISmsProbe probe, SmsConnectionOptions options, TimeProvider clock)
{
    public async Task RunAsync(Guid testId, CancellationToken ct)
    {
        if (await db.SmsConnectionTests.SingleOrDefaultAsync(t => t.Id == testId, ct) is not { IsFinished: false, Status: SmsConnectionTestStatus.Queued } test)
        {
            return; // gone (cleaned up), already running or already run: never send twice
        }

        test.Start();
        await db.SaveChangesAsync(ct);

        try
        {
            var connection = await db.SmsConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == test.ConnectionId, ct);
            if (connection is null || !connection.IsActive)
            {
                Fail(test, "sms.connection_unavailable");
            }
            else if (!HostsAllowed(connection))
            {
                Fail(test, "sms.host_not_allowed");
            }
            else
            {
                var apiKey = connection.SecretProtected is null ? null : secrets.Unprotect(connection.SecretProtected, SmsConnectionContexts.For(connection.Key));
                var recipient = test.RecipientProtected is null ? null : secrets.Unprotect(test.RecipientProtected, SmsConnectionContexts.ForTest(test.Id));
                if (string.IsNullOrEmpty(apiKey))
                {
                    Fail(test, "sms.secret_unreadable");
                }
                else if (string.IsNullOrEmpty(recipient))
                {
                    Fail(test, "sms.recipient_unreadable");
                }
                else
                {
                    var result = await probe.SendTestAsync(
                        new SmsConnectionSettings(connection.BaseUrl, connection.Sender, connection.MessageType, connection.UnicodeEnabled, connection.OrganisationPrefix, apiKey, TimeSpan.FromSeconds(connection.TimeoutSeconds)),
                        recipient, ct);
                    if (result.Ok)
                    {
                        test.Succeed(result.HttpStatus, result.ElapsedMs, result.MessageId, result.SmsCount, result.RemainingCredits, clock.GetUtcNow());
                    }
                    else
                    {
                        test.Fail(result.Code ?? "sms.test_failed", result.Message ?? SmsProbeMessages.For(result.Code ?? ""), result.HttpStatus, result.ElapsedMs, clock.GetUtcNow());
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(test, "sms.test_failed");
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    private bool HostsAllowed(SmsConnection connection)
    {
        if (options.AllowedHosts.Length == 0)
        {
            return true;
        }

        return ApiUrls.HostOf(connection.BaseUrl) is { } host && options.AllowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
    }

    private void Fail(SmsConnectionTest test, string code) => test.Fail(code, SmsProbeMessages.For(code), null, null, clock.GetUtcNow());

    /// <summary>Deletes tests older than 24 hours.</summary>
    public Task<int> CleanupAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - SmsConnectionTests.RetentionPeriod;
        return db.SmsConnectionTests.Where(t => t.RequestedAt < cutoff).ExecuteDeleteAsync(ct);
    }
}
