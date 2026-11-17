using Microsoft.Data.SqlClient;
using Verso.Application.Connections;

namespace Verso.Infrastructure.Connections;

/// <summary>
/// SQL Server (ADR 0016): connects and runs a fixed <c>SELECT 1</c>. Worker only. The connection string is built with
/// <see cref="SqlConnectionStringBuilder"/> from validated parts (so no option can be injected), read-only intent, no pooling.
/// Messages are Verso's own text: the server's message can name the server or the login, so it is never passed on.
/// </summary>
public sealed class SqlServerProbe : IProviderProbe
{
    public string Provider => DatabaseProviders.SqlServer;

    public async Task<DatabaseProbeResult> ProbeAsync(DatabaseConnectionSettings settings, CancellationToken ct)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = DataSource(settings),
            UserID = settings.UserName,
            Password = settings.Password,
            Encrypt = settings.Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = settings.TrustServerCertificate,
            ConnectTimeout = Math.Max(1, (int)settings.Timeout.TotalSeconds),
            ApplicationIntent = ApplicationIntent.ReadOnly,
            Pooling = false,
            ApplicationName = "Verso connection test",
        };
        if (settings.Database.Length > 0)
        {
            builder.InitialCatalog = settings.Database;
        }

        try
        {
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = Math.Max(1, (int)settings.Timeout.TotalSeconds);
            await command.ExecuteScalarAsync(ct);
            return DatabaseProbeResult.Success(connection.ServerVersion);
        }
        catch (SqlException ex)
        {
            return DatabaseProbeResult.Failure(Describe(ex.Number, ex.Message).Code);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.IO.IOException)
        {
            return DatabaseProbeResult.Failure("db.test_failed");
        }
    }

    /// <summary>The data source: the typed server, with the separate port added when the server has none (<c>host,port</c> typed in the server wins).</summary>
    internal static string DataSource(DatabaseConnectionSettings settings) =>
        settings.Port > 0 && !settings.Server.Contains(',', StringComparison.Ordinal) ? $"{settings.Server},{settings.Port}" : settings.Server;

    /// <summary>Verso's wording for a SQL Server error number (and, for the number 0 certificate case, a hint from the server's text, which is not passed on).</summary>
    public static (string Code, string Message) Describe(int number, string? serverMessage = null)
    {
        var code = number switch
        {
            18456 => "db.login_failed",
            18487 or 18488 => "db.password_expired",
            4060 => "db.cannot_open_database",
            -2 or 258 => "db.timeout",
            -1 or 2 or 40 or 53 or 64 or 233 or 1225 or 10060 or 10061 or 11001 => "db.unreachable",
            _ when serverMessage?.Contains("certificate", StringComparison.OrdinalIgnoreCase) == true => "db.certificate_untrusted",
            _ => "db.test_failed",
        };
        return (code, DatabaseProbeMessages.For(code));
    }
}

/// <summary>
/// Picks the probe for a connection's provider (ADR 0016 addendum 1). A provider whose driver is not registered on the Worker fails the
/// test with <c>db.provider_unavailable</c> instead of throwing.
/// </summary>
public sealed class DatabaseProbeRouter(IEnumerable<IProviderProbe> probes) : IDatabaseProbe
{
    private readonly Dictionary<string, IProviderProbe> _probes = probes.ToDictionary(p => p.Provider, StringComparer.Ordinal);

    public Task<DatabaseProbeResult> ProbeAsync(DatabaseConnectionSettings settings, CancellationToken ct) =>
        _probes.TryGetValue(settings.Provider, out var probe)
            ? probe.ProbeAsync(settings, ct)
            : Task.FromResult(DatabaseProbeResult.Failure("db.provider_unavailable"));
}
