using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Verso.Application.Mail;
using Verso.Domain.Audit;
using Verso.Domain.Connections;

namespace Verso.Application.Connections;

/// <summary>
/// API connection use cases (ADR 0017): create, update, deactivate and activate. Every change is audited with changed field names, never a
/// value and never the secret, which is encrypted before it reaches the database and never returned.
/// </summary>
public sealed partial class ApiConnectionService(IVersoDbContext db, ICurrentUser user, TimeProvider clock, ISecretProtector secrets)
{
    public const string EntityType = "ApiConnection";
    private const int MaxSecretLength = 2000;

    public static class Actions
    {
        public const string Created = "ApiConnectionCreated";
        public const string Updated = "ApiConnectionUpdated";
        public const string SecretChanged = "ApiConnectionSecretChanged";
        public const string Deactivated = "ApiConnectionDeactivated";
        public const string Activated = "ApiConnectionActivated";
    }

    public async Task<IReadOnlyList<ApiConnectionDto>> ListAsync(bool includeInactive, CancellationToken ct)
    {
        var query = db.ApiConnections.AsNoTracking();
        if (!includeInactive)
        {
            query = query.Where(c => c.IsActive);
        }

        return (await query.OrderBy(c => c.Key).ToListAsync(ct)).Select(ToDto).ToList();
    }

    /// <summary>What a workflow author needs to pick a connection in a Fetch from API step: active connections, by name, with the host only (never the path, authentication or secret).</summary>
    public async Task<IReadOnlyList<ApiConnectionOptionDto>> ListOptionsAsync(CancellationToken ct) =>
        (await db.ApiConnections.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name).Select(c => new { c.Key, c.Name, c.BaseUrl }).ToListAsync(ct))
        .Select(c => new ApiConnectionOptionDto(c.Key, c.Name, ApiUrls.HostOf(c.BaseUrl) ?? "")).ToList();

    public async Task<Result<ApiConnectionDto>> CreateAsync(SaveApiConnectionRequest request, CancellationToken ct)
    {
        var errors = Validate(request, creating: true, out var f);
        if (errors is not null)
        {
            return AppError.Validation(errors);
        }

        var key = request.Key!.Trim();
        if (await db.ApiConnections.AnyAsync(c => c.Key == key, ct))
        {
            return KeyTaken(key);
        }

        var now = clock.GetUtcNow();
        var connection = ApiConnection.Create(key, f!.Name, f.BaseUrl, f.TestPath, f.AuthType, f.AuthName, f.TokenUrl, f.Scope, f.Timeout, user.Id, now);
        var hasSecret = !string.IsNullOrEmpty(request.Secret);
        if (hasSecret)
        {
            connection.SetSecret(secrets.Protect(request.Secret!, ApiConnectionContexts.For(key)), user.Id, now);
        }

        db.ApiConnections.Add(connection);
        Audit(Actions.Created, connection.Id, now, new { key, name = f.Name, authType = f.AuthType, hasSecret });
        if (hasSecret)
        {
            Audit(Actions.SecretChanged, connection.Id, now, new { key, action = "set" });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await db.ApiConnections.AnyAsync(c => c.Key == key, CancellationToken.None))
            {
                return KeyTaken(key);
            }

            throw;
        }

        return ToDto(connection);
    }

    public async Task<Result<ApiConnectionDto>> UpdateAsync(Guid id, SaveApiConnectionRequest request, CancellationToken ct)
    {
        if (await db.ApiConnections.SingleOrDefaultAsync(c => c.Id == id, ct) is not { } connection)
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
        Note("testPath", connection.TestPath != f.TestPath);
        Note("authType", connection.AuthType != f.AuthType);
        Note("authName", connection.AuthName != f.AuthName);
        Note("tokenUrl", connection.TokenUrl != f.TokenUrl);
        Note("scope", connection.Scope != f.Scope);
        Note("timeout", connection.TimeoutSeconds != f.Timeout);
        if (changed.Count > 0)
        {
            connection.Update(f.Name, f.BaseUrl, f.TestPath, f.AuthType, f.AuthName, f.TokenUrl, f.Scope, f.Timeout, user.Id, now);
            Audit(Actions.Updated, id, now, new { key = connection.Key, changed });
        }

        if ((request.ClearSecret == true || f.AuthType == ApiAuthTypes.None) && connection.SecretProtected is not null)
        {
            connection.SetSecret(null, user.Id, now); // choosing "no authentication" drops the saved secret too
            Audit(Actions.SecretChanged, id, now, new { key = connection.Key, action = "cleared" });
        }
        else if (!string.IsNullOrEmpty(request.Secret))
        {
            connection.SetSecret(secrets.Protect(request.Secret, ApiConnectionContexts.For(connection.Key)), user.Id, now);
            Audit(Actions.SecretChanged, id, now, new { key = connection.Key, action = "set" });
        }

        await db.SaveChangesAsync(ct);
        return ToDto(connection);
    }

    public Task<Result<ApiConnectionDto>> DeactivateAsync(Guid id, CancellationToken ct) => SetActiveAsync(id, false, ct);

    public Task<Result<ApiConnectionDto>> ActivateAsync(Guid id, CancellationToken ct) => SetActiveAsync(id, true, ct);

    private async Task<Result<ApiConnectionDto>> SetActiveAsync(Guid id, bool active, CancellationToken ct)
    {
        if (await db.ApiConnections.SingleOrDefaultAsync(c => c.Id == id, ct) is not { } connection)
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

    private sealed record Fields(string Name, string BaseUrl, string TestPath, string AuthType, string AuthName, string TokenUrl, string Scope, int Timeout);

    private Dictionary<string, string[]>? Validate(SaveApiConnectionRequest r, bool creating, out Fields? fields)
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

        var baseUrl = (r.BaseUrl ?? "").Trim();
        if (!ApiUrls.IsValidBaseUrl(baseUrl))
        {
            Fail("baseUrl", "Enter the API's base address starting with https:// (no login, no ? query), such as https://api.example.com/v1.");
        }

        var testPath = (r.TestPath ?? "").Trim();
        if (!ApiUrls.IsValidTestPath(testPath))
        {
            Fail("testPath", "Enter the test request path starting with /, such as /status or /latest?base=EUR (no spaces).");
        }

        var authType = string.IsNullOrWhiteSpace(r.AuthType) ? ApiAuthTypes.None : r.AuthType.Trim();
        if (!ApiAuthTypes.IsValid(authType))
        {
            Fail("authType", $"The authentication must be one of: {string.Join(", ", ApiAuthTypes.All)}.");
            authType = ApiAuthTypes.None;
        }

        var authName = (r.AuthName ?? "").Trim();
        if (ApiAuthTypes.NeedsName(authType))
        {
            var valid = authType switch
            {
                ApiAuthTypes.ApiKeyHeader => HeaderNameRegex().IsMatch(authName),
                ApiAuthTypes.ApiKeyQuery => QueryNameRegex().IsMatch(authName),
                _ => authName.Length is > 0 and <= 200 && !authName.Any(char.IsControl) && !(authType == ApiAuthTypes.Basic && authName.Contains(':', StringComparison.Ordinal)),
            };
            if (!valid)
            {
                Fail("authName", authType switch
                {
                    ApiAuthTypes.ApiKeyHeader => "Enter the header name, such as X-API-Key (letters, digits, - and _).",
                    ApiAuthTypes.ApiKeyQuery => "Enter the query parameter name, such as api_key.",
                    ApiAuthTypes.Basic => "Enter the user name (no colon).",
                    _ => "Enter the client id.",
                });
            }
        }
        else
        {
            authName = "";
        }

        var tokenUrl = (r.TokenUrl ?? "").Trim();
        if (authType == ApiAuthTypes.OAuthClientCredentials)
        {
            if (!ApiUrls.IsValidTokenUrl(tokenUrl))
            {
                Fail("tokenUrl", "Enter the token endpoint address starting with https://.");
            }
        }
        else
        {
            tokenUrl = "";
        }

        var scope = authType == ApiAuthTypes.OAuthClientCredentials ? (r.Scope ?? "").Trim() : "";
        if (scope.Length > 500 || scope.Any(char.IsControl))
        {
            Fail("scope", "The scope can be at most 500 characters.");
        }

        var timeout = r.TimeoutSeconds ?? 15;
        if (timeout is < 1 or > 120)
        {
            Fail("timeoutSeconds", "The timeout must be 1–120 seconds.");
        }

        if (!string.IsNullOrEmpty(r.Secret))
        {
            if (!ApiAuthTypes.NeedsSecret(authType))
            {
                Fail("secret", "No secret is needed when the authentication is None.");
            }
            else if (r.Secret.Length > MaxSecretLength || r.Secret.Any(char.IsControl))
            {
                Fail("secret", $"The secret can be at most {MaxSecretLength} characters and has no control characters.");
            }
            else if (!secrets.IsConfigured)
            {
                Fail("secret", "A secret can't be stored yet: the administrator must set Secrets:MasterKey for the API and the Worker (see the runbook).");
            }
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        fields = new Fields(name, baseUrl, testPath, authType, authName, tokenUrl, scope, timeout);
        return null;
    }

    private static ApiConnectionDto ToDto(ApiConnection c) => new(
        c.Id, c.Key, c.Name, c.BaseUrl, c.TestPath, c.AuthType, c.AuthName, c.TokenUrl, c.Scope, c.TimeoutSeconds, c.IsActive,
        c.SecretProtected is not null, c.UpdatedAt, c.UpdatedBy);

    private void Audit(string action, Guid id, DateTimeOffset now, object details) =>
        db.AuditEntries.Add(AuditEntry.Record(
            action, EntityType, id.ToString(), user.Id, user.Name, now,
            JsonSerializer.Serialize(details), Activity.Current?.TraceId.ToHexString()));

    private static AppError NotFound(Guid id) => AppError.NotFound("api_connection.not_found", $"API connection {id} was not found.");

    private static AppError KeyTaken(string key) => AppError.Conflict("api_connection.key_taken", $"An API connection with the key '{key}' already exists.");

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_\-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderNameRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_.\-\[\]]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex QueryNameRegex();
}

/// <summary>API side of "Test connection" (ADR 0017): records the request (audited) and queues the Worker. The API never calls the remote API itself.</summary>
public sealed class ApiConnectionTests(IVersoDbContext db, ICurrentUser user, TimeProvider clock, IApiConnectionTestJobs jobs)
{
    public const string EntityType = "ApiConnectionTest";
    public const string RequestedAction = "ApiConnectionTestRequested";
    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromHours(24);

    public async Task<Result<ApiConnectionTestDto>> RequestAsync(Guid connectionId, CancellationToken ct)
    {
        if (await db.ApiConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == connectionId, ct) is not { } connection)
        {
            return AppError.NotFound("api_connection.not_found", $"API connection {connectionId} was not found.");
        }

        if (!connection.IsActive)
        {
            return AppError.Conflict("api_connection.inactive", "This connection is switched off. Turn it on to test it.");
        }

        var now = clock.GetUtcNow();
        var test = ApiConnectionTest.Create(connection.Id, connection.Key, user.Id, now);
        db.ApiConnectionTests.Add(test);
        db.AuditEntries.Add(AuditEntry.Record(
            RequestedAction, EntityType, test.Id.ToString(), user.Id, user.Name, now,
            JsonSerializer.Serialize(new { connection = connection.Key }), Activity.Current?.TraceId.ToHexString()));
        await db.SaveChangesAsync(ct);

        try
        {
            jobs.Enqueue(test.Id);
        }
        catch
        {
            test.Fail("api.test_not_queued", "The test could not be queued. Is the job storage reachable?", null, null, null, null, now);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        return ToDto(test);
    }

    public async Task<Result<ApiConnectionTestDto>> GetAsync(Guid testId, CancellationToken ct) =>
        await db.ApiConnectionTests.AsNoTracking().SingleOrDefaultAsync(t => t.Id == testId, ct) is { } test
            ? ToDto(test)
            : AppError.NotFound("api_connection_test.not_found", $"API connection test {testId} was not found (tests are kept for 24 hours).");

    internal static ApiConnectionTestDto ToDto(ApiConnectionTest t) =>
        new(t.Id, t.ConnectionKey, t.Status.ToString(), t.Code, t.Message, t.HttpStatus, t.ElapsedMs, t.ContentType, t.ResponseBytes, t.RequestedAt, t.CompletedAt);
}

/// <summary>
/// Worker side: reads the saved connection, decrypts the secret in memory and asks the probe to send the one test request. A connection
/// that can't be called is a failed test, not an error of the job. The result never contains the body, the address or the secret.
/// </summary>
public sealed class ApiConnectionTestRunner(IVersoDbContext db, ISecretProtector secrets, IApiProbe probe, ApiConnectionOptions options, TimeProvider clock)
{
    public async Task RunAsync(Guid testId, CancellationToken ct)
    {
        if (await db.ApiConnectionTests.SingleOrDefaultAsync(t => t.Id == testId, ct) is not { IsFinished: false } test)
        {
            return; // gone (cleaned up) or already run
        }

        test.Start();
        await db.SaveChangesAsync(ct);

        try
        {
            var connection = await db.ApiConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == test.ConnectionId, ct);
            if (connection is null || !connection.IsActive)
            {
                Fail(test, "api.connection_unavailable");
            }
            else if (!HostsAllowed(connection))
            {
                Fail(test, "api.host_not_allowed");
            }
            else
            {
                var secret = "";
                if (connection.SecretProtected is not null)
                {
                    secret = secrets.Unprotect(connection.SecretProtected, ApiConnectionContexts.For(connection.Key)) ?? "";
                    if (secret.Length == 0)
                    {
                        Fail(test, "api.secret_unreadable");
                        await db.SaveChangesAsync(CancellationToken.None);
                        return;
                    }
                }

                var result = await probe.ProbeAsync(
                    new ApiConnectionSettings(connection.BaseUrl, connection.TestPath, connection.AuthType, connection.AuthName, secret, connection.TokenUrl, connection.Scope, TimeSpan.FromSeconds(connection.TimeoutSeconds)), ct);
                if (result.Ok)
                {
                    test.Succeed(result.HttpStatus, result.ElapsedMs, result.ContentType, result.ResponseBytes, clock.GetUtcNow());
                }
                else
                {
                    test.Fail(result.Code ?? "api.test_failed", result.Message ?? ApiProbeMessages.For(result.Code ?? ""), result.HttpStatus, result.ElapsedMs, result.ContentType, result.ResponseBytes, clock.GetUtcNow());
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(test, "api.test_failed");
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    private bool HostsAllowed(ApiConnection connection)
    {
        if (options.AllowedHosts.Length == 0)
        {
            return true;
        }

        var hosts = new[] { ApiUrls.HostOf(connection.BaseUrl), connection.AuthType == ApiAuthTypes.OAuthClientCredentials ? ApiUrls.HostOf(connection.TokenUrl) : null };
        return hosts.Where(h => h is not null).All(h => options.AllowedHosts.Contains(h!, StringComparer.OrdinalIgnoreCase));
    }

    private void Fail(ApiConnectionTest test, string code) => test.Fail(code, ApiProbeMessages.For(code), null, null, null, null, clock.GetUtcNow());

    /// <summary>Deletes tests older than 24 hours.</summary>
    public Task<int> CleanupAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - ApiConnectionTests.RetentionPeriod;
        return db.ApiConnectionTests.Where(t => t.RequestedAt < cutoff).ExecuteDeleteAsync(ct);
    }
}
