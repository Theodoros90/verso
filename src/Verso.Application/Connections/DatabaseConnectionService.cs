using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Verso.Application.Mail;
using Verso.Domain.Audit;
using Verso.Domain.Connections;

namespace Verso.Application.Connections;

/// <summary>
/// Database connection use cases (ADR 0016): create, update, deactivate and activate. Every change is audited with changed field names,
/// never a value and never the password, which is encrypted before it reaches the database and never returned.
/// </summary>
public sealed class DatabaseConnectionService(IVersoDbContext db, ICurrentUser user, TimeProvider clock, ISecretProtector secrets)
{
    public const string EntityType = "DatabaseConnection";
    private const int MaxPasswordLength = 256;

    public static class Actions
    {
        public const string Created = "DatabaseConnectionCreated";
        public const string Updated = "DatabaseConnectionUpdated";
        public const string PasswordChanged = "DatabaseConnectionPasswordChanged";
        public const string Deactivated = "DatabaseConnectionDeactivated";
        public const string Activated = "DatabaseConnectionActivated";
    }

    public async Task<IReadOnlyList<DatabaseConnectionDto>> ListAsync(bool includeInactive, CancellationToken ct)
    {
        var query = db.DatabaseConnections.AsNoTracking();
        if (!includeInactive)
        {
            query = query.Where(c => c.IsActive);
        }

        return (await query.OrderBy(c => c.Key).ToListAsync(ct)).Select(ToDto).ToList();
    }

    public async Task<Result<DatabaseConnectionDto>> CreateAsync(SaveDatabaseConnectionRequest request, CancellationToken ct)
    {
        var errors = Validate(request, null, out var f);
        if (errors is not null)
        {
            return AppError.Validation(errors);
        }

        var key = request.Key!.Trim();
        var provider = f!.Provider;
        if (await db.DatabaseConnections.AnyAsync(c => c.Key == key, ct))
        {
            return KeyTaken(key);
        }

        var now = clock.GetUtcNow();
        var connection = DatabaseConnection.Create(key, provider, f.Name, f.Server, f.Port, f.Database, f.UserName, f.Encrypt, f.Trust, f.Timeout, user.Id, now);
        var hasPassword = !string.IsNullOrEmpty(request.Password);
        if (hasPassword)
        {
            connection.SetPassword(secrets.Protect(request.Password!, DatabaseConnectionContexts.For(key)), user.Id, now);
        }

        db.DatabaseConnections.Add(connection);
        Audit(Actions.Created, connection.Id, now, new { key, provider, name = f.Name, hasPassword });
        if (hasPassword)
        {
            Audit(Actions.PasswordChanged, connection.Id, now, new { key, action = "set" });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await db.DatabaseConnections.AnyAsync(c => c.Key == key, CancellationToken.None))
            {
                return KeyTaken(key);
            }

            throw;
        }

        return ToDto(connection);
    }

    public async Task<Result<DatabaseConnectionDto>> UpdateAsync(Guid id, SaveDatabaseConnectionRequest request, CancellationToken ct)
    {
        if (await db.DatabaseConnections.SingleOrDefaultAsync(c => c.Id == id, ct) is not { } connection)
        {
            return NotFound(id);
        }

        var errors = Validate(request, connection.Provider, out var f);
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
        Note("server", connection.Server != f.Server);
        Note("port", connection.Port != f.Port);
        Note("database", connection.Database != f.Database);
        Note("userName", connection.UserName != f.UserName);
        Note("encrypt", connection.Encrypt != f.Encrypt);
        Note("trustServerCertificate", connection.TrustServerCertificate != f.Trust);
        Note("timeout", connection.TimeoutSeconds != f.Timeout);
        if (changed.Count > 0)
        {
            connection.Update(f.Name, f.Server, f.Port, f.Database, f.UserName, f.Encrypt, f.Trust, f.Timeout, user.Id, now);
            Audit(Actions.Updated, id, now, new { key = connection.Key, changed });
        }

        if (request.ClearPassword == true && connection.PasswordProtected is not null)
        {
            connection.SetPassword(null, user.Id, now);
            Audit(Actions.PasswordChanged, id, now, new { key = connection.Key, action = "cleared" });
        }
        else if (!string.IsNullOrEmpty(request.Password))
        {
            connection.SetPassword(secrets.Protect(request.Password, DatabaseConnectionContexts.For(connection.Key)), user.Id, now);
            Audit(Actions.PasswordChanged, id, now, new { key = connection.Key, action = "set" });
        }

        await db.SaveChangesAsync(ct);
        return ToDto(connection);
    }

    public Task<Result<DatabaseConnectionDto>> DeactivateAsync(Guid id, CancellationToken ct) => SetActiveAsync(id, false, ct);

    public Task<Result<DatabaseConnectionDto>> ActivateAsync(Guid id, CancellationToken ct) => SetActiveAsync(id, true, ct);

    private async Task<Result<DatabaseConnectionDto>> SetActiveAsync(Guid id, bool active, CancellationToken ct)
    {
        if (await db.DatabaseConnections.SingleOrDefaultAsync(c => c.Id == id, ct) is not { } connection)
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

    private sealed record Fields(string Provider, string Name, string Server, int Port, string Database, string UserName, bool Encrypt, bool Trust, int Timeout);

    /// <param name="existingProvider">The provider of the connection being updated, or null when creating (the request names it).</param>
    private Dictionary<string, string[]>? Validate(SaveDatabaseConnectionRequest r, string? existingProvider, out Fields? fields)
    {
        var creating = existingProvider is null;
        var provider = existingProvider ?? (string.IsNullOrWhiteSpace(r.Provider) ? DatabaseProviders.SqlServer : r.Provider.Trim());
        fields = null;
        var errors = new Dictionary<string, string[]>();
        void Fail(string field, string message) => errors[field] = [message];

        if (!DatabaseProviders.IsValid(provider))
        {
            Fail("provider", $"The database type must be one of: {string.Join(", ", DatabaseProviders.All)}.");
            provider = DatabaseProviders.SqlServer; // keep validating the other fields
        }

        if (creating && !DatabaseServers.IsValidKey(r.Key?.Trim()))
        {
            Fail("key", "A key of 2–64 characters is required: lower-case letters, digits and hyphens.");
        }

        var name = (r.Name ?? "").Trim();
        if (name.Length is 0 or > 100)
        {
            Fail("name", "A name of 1–100 characters is required.");
        }

        var server = (r.Server ?? "").Trim();
        if (!DatabaseProviders.IsValidServer(provider, server))
        {
            Fail("server", provider == DatabaseProviders.SqlServer
                ? "Enter a server name such as sql.example.com, optionally with \\INSTANCE or ,port."
                : "Enter a host name or address such as db.example.com (the port is its own field).");
        }

        var port = r.Port ?? 0;
        if (port is < 0 or > 65535)
        {
            Fail("port", "The port must be 1–65535 (empty or 0 uses the default).");
        }

        var database = (r.Database ?? "").Trim();
        if (!DatabaseProviders.IsValidDatabase(provider, database))
        {
            Fail("database", DatabaseProviders.RequiresDatabase(provider)
                ? "The service name is required (letters, digits and . _ - $ # @, at most 128 characters)."
                : "The database name can have letters, digits and . _ - $ # @, at most 128 characters.");
        }

        if (r.TrustServerCertificate == true && !DatabaseProviders.SupportsTrustServerCertificate(provider))
        {
            Fail("trustServerCertificate", $"{DatabaseProviders.DisplayName(provider)} has no trust-certificate option: install the certificate authority on the Worker server instead.");
        }

        var userName = (r.UserName ?? "").Trim();
        if (userName.Length is 0 or > 128 || userName.Any(c => char.IsControl(c) || c is ';' or '=' or '"' or '\''))
        {
            Fail("userName", "A login of 1–128 characters is required, without ; = or quotes.");
        }

        var timeout = r.TimeoutSeconds ?? 15;
        if (timeout is < 1 or > 120)
        {
            Fail("timeoutSeconds", "The timeout must be 1–120 seconds.");
        }

        if (!string.IsNullOrEmpty(r.Password))
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

        fields = new Fields(provider, name, server, port, database, userName, r.Encrypt ?? true, r.TrustServerCertificate ?? false, timeout);
        return null;
    }

    private static DatabaseConnectionDto ToDto(DatabaseConnection c) => new(
        c.Id, c.Key, c.Provider, c.Name, c.Server, c.Port, c.Database, c.UserName, c.Encrypt, c.TrustServerCertificate, c.TimeoutSeconds, c.IsActive,
        c.PasswordProtected is not null, c.UpdatedAt, c.UpdatedBy);

    private void Audit(string action, Guid id, DateTimeOffset now, object details) =>
        db.AuditEntries.Add(AuditEntry.Record(
            action, EntityType, id.ToString(), user.Id, user.Name, now,
            JsonSerializer.Serialize(details), Activity.Current?.TraceId.ToHexString()));

    private static AppError NotFound(Guid id) => AppError.NotFound("database_connection.not_found", $"Database connection {id} was not found.");

    private static AppError KeyTaken(string key) => AppError.Conflict("database_connection.key_taken", $"A database connection with the key '{key}' already exists.");
}

/// <summary>API side of "Test connection" (ADR 0016): records the request (audited) and queues the Worker. The API never connects to the database itself.</summary>
public sealed class DatabaseConnectionTests(IVersoDbContext db, ICurrentUser user, TimeProvider clock, IDatabaseConnectionTestJobs jobs)
{
    public const string EntityType = "DatabaseConnectionTest";
    public const string RequestedAction = "DatabaseConnectionTestRequested";
    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromHours(24);

    public async Task<Result<DatabaseConnectionTestDto>> RequestAsync(Guid connectionId, CancellationToken ct)
    {
        if (await db.DatabaseConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == connectionId, ct) is not { } connection)
        {
            return AppError.NotFound("database_connection.not_found", $"Database connection {connectionId} was not found.");
        }

        if (!connection.IsActive)
        {
            return AppError.Conflict("database_connection.inactive", "This connection is switched off. Turn it on to test it.");
        }

        var now = clock.GetUtcNow();
        var test = DatabaseConnectionTest.Create(connection.Id, connection.Key, user.Id, now);
        db.DatabaseConnectionTests.Add(test);
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
            test.Fail("db.test_not_queued", "The test could not be queued. Is the job storage reachable?", now);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        return ToDto(test);
    }

    public async Task<Result<DatabaseConnectionTestDto>> GetAsync(Guid testId, CancellationToken ct) =>
        await db.DatabaseConnectionTests.AsNoTracking().SingleOrDefaultAsync(t => t.Id == testId, ct) is { } test
            ? ToDto(test)
            : AppError.NotFound("database_connection_test.not_found", $"Database connection test {testId} was not found (tests are kept for 24 hours).");

    internal static DatabaseConnectionTestDto ToDto(DatabaseConnectionTest t) =>
        new(t.Id, t.ConnectionKey, t.Status.ToString(), t.Code, t.Message, t.ServerVersion, t.RequestedAt, t.CompletedAt);
}

/// <summary>
/// Worker side: reads the saved connection, decrypts the password in memory and asks the probe to connect and run <c>SELECT 1</c>.
/// A connection that can't be opened is a failed test, not an error of the job. The result never contains the server, the login or the password.
/// </summary>
public sealed class DatabaseConnectionTestRunner(IVersoDbContext db, ISecretProtector secrets, IDatabaseProbe probe, DatabaseConnectionOptions options, TimeProvider clock)
{
    public async Task RunAsync(Guid testId, CancellationToken ct)
    {
        if (await db.DatabaseConnectionTests.SingleOrDefaultAsync(t => t.Id == testId, ct) is not { IsFinished: false } test)
        {
            return; // gone (cleaned up) or already run
        }

        test.Start();
        await db.SaveChangesAsync(ct);

        try
        {
            var connection = await db.DatabaseConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == test.ConnectionId, ct);
            if (connection is null || !connection.IsActive)
            {
                test.Fail("db.connection_unavailable", "The connection no longer exists or is switched off.", clock.GetUtcNow());
            }
            else if (options.AllowedServers.Length > 0 && !options.AllowedServers.Contains(DatabaseServers.HostOf(connection.Server), StringComparer.OrdinalIgnoreCase))
            {
                test.Fail("db.server_not_allowed", "The administrator has not allowed the Worker to connect to this server (DatabaseConnections:AllowedServers).", clock.GetUtcNow());
            }
            else
            {
                var password = "";
                if (connection.PasswordProtected is not null)
                {
                    password = secrets.Unprotect(connection.PasswordProtected, DatabaseConnectionContexts.For(connection.Key)) ?? "";
                    if (password.Length == 0)
                    {
                        test.Fail("db.password_unreadable", "The stored password can't be read: the Worker's Secrets:MasterKey is missing or differs from the API's. Set it, or type the password again.", clock.GetUtcNow());
                        await db.SaveChangesAsync(CancellationToken.None);
                        return;
                    }
                }

                var result = await probe.ProbeAsync(
                    new DatabaseConnectionSettings(connection.Provider, connection.Server, connection.Port, connection.Database, connection.UserName, password, connection.Encrypt, connection.TrustServerCertificate, TimeSpan.FromSeconds(connection.TimeoutSeconds)), ct);
                if (result.Ok)
                {
                    test.Succeed(result.ServerVersion, clock.GetUtcNow());
                }
                else
                {
                    test.Fail(result.Code ?? "db.test_failed", result.Message ?? "The test failed.", clock.GetUtcNow());
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            test.Fail("db.test_failed", "The test could not be run.", clock.GetUtcNow());
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Deletes tests older than 24 hours.</summary>
    public Task<int> CleanupAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - DatabaseConnectionTests.RetentionPeriod;
        return db.DatabaseConnectionTests.Where(t => t.RequestedAt < cutoff).ExecuteDeleteAsync(ct);
    }
}
