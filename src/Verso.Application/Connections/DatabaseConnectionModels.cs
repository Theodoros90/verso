using System.Text.RegularExpressions;

namespace Verso.Application.Connections;

/// <summary>
/// Creates or updates a database connection. <see cref="Key"/> and <see cref="Provider"/> are used on create only (they never change).
/// <see cref="Port"/> null or 0 means the provider's default. <see cref="Password"/>: null keeps the stored one, a value replaces it;
/// <see cref="ClearPassword"/> removes it. The password is never returned.
/// </summary>
public sealed record SaveDatabaseConnectionRequest(
    string? Key, string? Provider, string? Name, string? Server, int? Port, string? Database, string? UserName, bool? Encrypt,
    bool? TrustServerCertificate, int? TimeoutSeconds, string? Password, bool? ClearPassword);

/// <summary>A database connection as the Configuration screen sees it. No password: only whether one is stored.</summary>
public sealed record DatabaseConnectionDto(
    Guid Id, string Key, string Provider, string Name, string Server, int Port, string Database, string UserName, bool Encrypt,
    bool TrustServerCertificate, int TimeoutSeconds, bool IsActive, bool HasPassword, DateTimeOffset UpdatedAt, string UpdatedBy);

/// <summary>One choice in the Query database step's connection dropdown: key, name and database type only.</summary>
public sealed record DatabaseConnectionOptionDto(string Key, string Name, string Provider);

/// <summary>The state of a connection test. Never carries the server, the login or a password.</summary>
public sealed record DatabaseConnectionTestDto(
    Guid Id, string ConnectionKey, string Status, string? Code, string? Message, string? ServerVersion, DateTimeOffset RequestedAt, DateTimeOffset? CompletedAt);

/// <summary>What the Worker needs to connect, password decrypted in memory only. <see cref="Port"/> 0 means the provider's default.</summary>
public sealed record DatabaseConnectionSettings(
    string Provider, string Server, int Port, string Database, string UserName, string Password, bool Encrypt, bool TrustServerCertificate, TimeSpan Timeout);

/// <summary>The result of trying a connection: a code and a safe message on failure, the server version on success.</summary>
public sealed record DatabaseProbeResult(bool Ok, string? Code, string? Message, string? ServerVersion)
{
    public static DatabaseProbeResult Success(string? serverVersion) => new(true, null, null, serverVersion);

    /// <summary>A failure with Verso's own wording for <paramref name="code"/> (never the driver's text).</summary>
    public static DatabaseProbeResult Failure(string code) => new(false, code, DatabaseProbeMessages.For(code), null);
}

/// <summary>
/// Opens a connection and runs a fixed <c>SELECT 1</c>, nothing else (ADR 0016). The router picks the provider. Implemented in
/// Infrastructure (SQL Server) and Verso.Databases (PostgreSQL, MySQL / MariaDB, Oracle); registered in the Worker only. Never runs text a
/// person wrote; never returns the server name, the login or the password in a message.
/// </summary>
public interface IDatabaseProbe
{
    Task<DatabaseProbeResult> ProbeAsync(DatabaseConnectionSettings settings, CancellationToken ct);
}

/// <summary>One provider's probe, found by <see cref="Provider"/> (a <see cref="DatabaseProviders"/> value).</summary>
public interface IProviderProbe
{
    string Provider { get; }

    Task<DatabaseProbeResult> ProbeAsync(DatabaseConnectionSettings settings, CancellationToken ct);
}

/// <summary>Verso's own wording for each failure code. Drivers' messages can name the server or the login, so they are never passed on.</summary>
public static class DatabaseProbeMessages
{
    public static string For(string code) => code switch
    {
        "db.login_failed" => "The server rejected the login or the password.",
        "db.password_expired" => "The password has expired, or the account is locked or must be changed on the server.",
        "db.cannot_open_database" => "The login can't open that database (or service). Check the name and the login's permissions.",
        "db.timeout" => "The server did not answer in time.",
        "db.unreachable" => "The server could not be reached. Check the name, the port and the network or firewall.",
        "db.certificate_untrusted" => "The server's certificate is not trusted. Install its certificate authority on the Worker server, or allow 'trust server certificate' on a non-production server only.",
        "db.provider_unavailable" => "This database type is not available on the Worker (its driver is not installed).",
        _ => "The connection could not be opened.",
    };
}

/// <summary>Queues the Worker job that runs a connection test (Hangfire in Infrastructure).</summary>
public interface IDatabaseConnectionTestJobs
{
    void Enqueue(Guid testId);
}

/// <summary>Optional limit set by the administrator on the Worker: when <see cref="AllowedServers"/> is not empty only these hosts may be connected to.</summary>
public sealed class DatabaseConnectionOptions
{
    public const string Section = "DatabaseConnections";
    public string[] AllowedServers { get; set; } = [];
}

public static class DatabaseConnectionContexts
{
    public static string For(string key) => $"database-connection:{key}";
}

/// <summary>The database types Verso can connect to (ADR 0016 and its addendum 1) and the rules that differ between them.</summary>
public static partial class DatabaseProviders
{
    public const string SqlServer = "SqlServer";
    public const string PostgreSql = "PostgreSql";
    public const string MySql = "MySql";
    public const string Oracle = "Oracle";

    public static readonly IReadOnlyList<string> All = [SqlServer, PostgreSql, MySql, Oracle];

    public static bool IsValid(string? provider) => provider is not null && All.Contains(provider, StringComparer.Ordinal);

    public static int DefaultPort(string provider) => provider switch
    {
        SqlServer => 1433,
        PostgreSql => 5432,
        MySql => 3306,
        Oracle => 1521,
        _ => 0,
    };

    /// <summary>Oracle connects to a service name, so a database name is required there.</summary>
    public static bool RequiresDatabase(string provider) => provider == Oracle;

    /// <summary>Oracle has no "trust any certificate" switch (it uses the Worker's wallet or certificate store).</summary>
    public static bool SupportsTrustServerCertificate(string provider) => provider != Oracle;

    /// <summary>The port to use: the saved one, or the provider's default when it is 0.</summary>
    public static int EffectivePort(string provider, int port) => port > 0 ? port : DefaultPort(provider);

    /// <summary>
    /// A server as typed. SQL Server: a host name or address, optionally <c>\INSTANCE</c> or <c>,port</c>. The others: a plain host name or address
    /// (the port is its own field). No spaces, quotes, semicolons or equals signs, so nothing can add a connection-string option.
    /// </summary>
    public static bool IsValidServer(string provider, string? server) =>
        server is { Length: > 0 and <= 255 } && (provider == SqlServer ? SqlServerRegex().IsMatch(server) : HostRegex().IsMatch(server));

    /// <summary>The database (or Oracle service) name: letters, digits and a few separators.</summary>
    public static bool IsValidDatabase(string provider, string database) =>
        database.Length == 0 ? !RequiresDatabase(provider) : database.Length <= 128 && DatabaseRegex().IsMatch(database);

    /// <summary>The host part (before an instance or a port), for the allowed-servers check.</summary>
    public static string HostOf(string server)
    {
        var end = server.IndexOfAny(['\\', ',']);
        return (end < 0 ? server : server[..end]).Trim();
    }

    public static string DisplayName(string provider) => provider switch
    {
        SqlServer => "SQL Server",
        PostgreSql => "PostgreSQL",
        MySql => "MySQL / MariaDB",
        Oracle => "Oracle",
        _ => provider,
    };

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9.\-_]*[A-Za-z0-9])?(\\[A-Za-z0-9_$#@\-]{1,64})?(,[0-9]{1,5})?$", RegexOptions.CultureInvariant)]
    private static partial Regex SqlServerRegex();

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9.\-_]*[A-Za-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex HostRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_.$#@\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseRegex();
}

public static partial class DatabaseServers
{
    public const string KeyPattern = "^[a-z0-9][a-z0-9-]{1,63}$";

    public static bool IsValidKey(string? key) => key is not null && KeyRegex().IsMatch(key);

    /// <summary>Kept for callers that only know SQL Server servers; use <see cref="DatabaseProviders.HostOf"/>.</summary>
    public static string HostOf(string server) => DatabaseProviders.HostOf(server);

    [GeneratedRegex(KeyPattern, RegexOptions.CultureInvariant)]
    private static partial Regex KeyRegex();
}
