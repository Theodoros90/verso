using System.Net.Sockets;
using System.Security.Authentication;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using Verso.Application.Connections;

namespace Verso.Databases;

/// <summary>Shared rules of every probe: a fixed <c>SELECT 1</c> only, never the driver's own message, the same codes for the same problems.</summary>
internal static class ProbeErrors
{
    /// <summary>Walks the exception chain for the failures every driver reports the same way: a certificate problem, a refused or lost network connection, a timeout.</summary>
    public static string? FromChain(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            switch (e)
            {
                case AuthenticationException:
                    return "db.certificate_untrusted";
                case SocketException:
                    return "db.unreachable";
                case TimeoutException:
                    return "db.timeout";
            }

            if (e.InnerException is null)
            {
                break;
            }
        }

        return null;
    }
}

/// <summary>PostgreSQL through Npgsql (PostgreSQL licence). TLS verified unless "trust certificate" is on; read-only session; no pooling.</summary>
public sealed class PostgreSqlProbe : IProviderProbe
{
    public string Provider => DatabaseProviders.PostgreSql;

    internal static NpgsqlConnectionStringBuilder Build(DatabaseConnectionSettings s, string applicationName = "Verso connection test", bool readOnly = true)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = s.Server,
            Port = DatabaseProviders.EffectivePort(DatabaseProviders.PostgreSql, s.Port),
            Username = s.UserName,
            Password = s.Password,
            SslMode = !s.Encrypt ? SslMode.Disable : s.TrustServerCertificate ? SslMode.Require : SslMode.VerifyFull,
            Timeout = Math.Max(1, (int)s.Timeout.TotalSeconds),
            CommandTimeout = Math.Max(1, (int)s.Timeout.TotalSeconds),
            Pooling = false,
            ApplicationName = applicationName,
        };
        if (readOnly)
        {
            builder.Options = "-c default_transaction_read_only=on"; // only the Write to database step's runner builds without it
        }

        if (s.Database.Length > 0)
        {
            builder.Database = s.Database;
        }

        return builder;
    }

    public async Task<DatabaseProbeResult> ProbeAsync(DatabaseConnectionSettings settings, CancellationToken ct)
    {
        try
        {
            await using var connection = new NpgsqlConnection(Build(settings).ConnectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(ct);
            return DatabaseProbeResult.Success(connection.ServerVersion);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DatabaseProbeResult.Failure(Describe(ex));
        }
    }

    internal static string Describe(Exception ex) => ex switch
    {
        PostgresException { SqlState: "28P01" or "28000" } => "db.login_failed",
        PostgresException { SqlState: "3D000" } => "db.cannot_open_database",
        PostgresException { SqlState: "57P03" } => "db.unreachable", // the server is starting up
        _ => ProbeErrors.FromChain(ex) ?? "db.test_failed",
    };
}

/// <summary>MySQL and MariaDB through MySqlConnector (MIT). TLS verified unless "trust certificate" is on; no pooling.</summary>
public sealed class MySqlProbe : IProviderProbe
{
    public string Provider => DatabaseProviders.MySql;

    internal static MySqlConnectionStringBuilder Build(DatabaseConnectionSettings s)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = s.Server,
            Port = (uint)DatabaseProviders.EffectivePort(DatabaseProviders.MySql, s.Port),
            UserID = s.UserName,
            Password = s.Password,
            SslMode = !s.Encrypt ? MySqlSslMode.None : s.TrustServerCertificate ? MySqlSslMode.Required : MySqlSslMode.VerifyFull,
            ConnectionTimeout = (uint)Math.Max(1, (int)s.Timeout.TotalSeconds),
            DefaultCommandTimeout = (uint)Math.Max(1, (int)s.Timeout.TotalSeconds),
            Pooling = false,
        };
        if (s.Database.Length > 0)
        {
            builder.Database = s.Database;
        }

        return builder;
    }

    public async Task<DatabaseProbeResult> ProbeAsync(DatabaseConnectionSettings settings, CancellationToken ct)
    {
        try
        {
            await using var connection = new MySqlConnection(Build(settings).ConnectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(ct);
            return DatabaseProbeResult.Success(connection.ServerVersion);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DatabaseProbeResult.Failure(Describe(ex));
        }
    }

    internal static string Describe(Exception ex) => ex switch
    {
        MySqlException { ErrorCode: MySqlErrorCode.AccessDenied } => "db.login_failed",
        MySqlException { ErrorCode: MySqlErrorCode.UnknownDatabase or MySqlErrorCode.DatabaseAccessDenied } => "db.cannot_open_database",
        MySqlException { ErrorCode: MySqlErrorCode.UnableToConnectToHost or MySqlErrorCode.ConnectionCountError } => "db.unreachable",
        _ => ProbeErrors.FromChain(ex) ?? "db.test_failed",
    };
}

/// <summary>
/// Oracle through Oracle.ManagedDataAccess.Core (Oracle Free Use Terms, not open source: the bank's licence review must accept it).
/// Connects to a service name with a descriptor built from validated parts. "Encrypt" means TCPS (TLS); the server certificate must be trusted
/// by the Worker's wallet or certificate store (there is no trust-anything switch).
/// </summary>
public sealed class OracleProbe : IProviderProbe
{
    public string Provider => DatabaseProviders.Oracle;

    /// <summary>The connect descriptor for a host, port and service name. Refuses anything that could close or open a parenthesis, so nothing can be injected.</summary>
    internal static string BuildDescriptor(string host, int port, string serviceName, bool tcps)
    {
        if (!DatabaseProviders.IsValidServer(DatabaseProviders.Oracle, host) || !DatabaseProviders.IsValidDatabase(DatabaseProviders.Oracle, serviceName) || serviceName.Length == 0 || port is < 1 or > 65535)
        {
            throw new ArgumentException("The host, port or service name is not valid.");
        }

        return $"(DESCRIPTION=(ADDRESS=(PROTOCOL={(tcps ? "TCPS" : "TCP")})(HOST={host})(PORT={port}))(CONNECT_DATA=(SERVICE_NAME={serviceName})))";
    }

    internal static OracleConnectionStringBuilder Build(DatabaseConnectionSettings s) => new()
    {
        UserID = s.UserName,
        Password = s.Password,
        DataSource = BuildDescriptor(s.Server, DatabaseProviders.EffectivePort(DatabaseProviders.Oracle, s.Port), s.Database, s.Encrypt),
        ConnectionTimeout = Math.Max(1, (int)s.Timeout.TotalSeconds),
        Pooling = false,
    };

    public async Task<DatabaseProbeResult> ProbeAsync(DatabaseConnectionSettings settings, CancellationToken ct)
    {
        try
        {
            await using var connection = new OracleConnection(Build(settings).ConnectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM DUAL";
            await command.ExecuteScalarAsync(ct);
            return DatabaseProbeResult.Success(connection.ServerVersion);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DatabaseProbeResult.Failure(Describe(ex));
        }
    }

    internal static string Describe(Exception ex) => ex switch
    {
        OracleException { Number: 1017 } => "db.login_failed",
        OracleException { Number: 28000 or 28001 or 28002 or 28011 } => "db.password_expired", // locked or expired
        OracleException { Number: 12514 or 12505 or 12154 or 12504 } => "db.cannot_open_database", // unknown service
        OracleException { Number: 12541 or 12170 or 12543 or 12545 or 12537 or 12560 } => "db.unreachable",
        OracleException { Number: 29024 or 28860 or 28759 } => "db.certificate_untrusted",
        _ => ProbeErrors.FromChain(ex) ?? "db.test_failed",
    };
}
