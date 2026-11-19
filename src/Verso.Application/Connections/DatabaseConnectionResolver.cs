using Microsoft.EntityFrameworkCore;
using Verso.Application.Mail;

namespace Verso.Application.Connections;

/// <summary>
/// Shared by the Query database and Write to database steps: finds a saved database connection by key, checks it is active and on an allowed server,
/// and decrypts its password in memory. With <c>requireWrites</c> a connection whose "Allow writes" is off is refused with <c>db.writes_not_allowed</c>
/// before its password is decrypted (and so before any connection is opened). Failures are codes, never exceptions (except cancellation).
/// </summary>
public sealed class DatabaseConnectionResolver(IVersoDbContext db, ISecretProtector secrets, DatabaseConnectionOptions options)
{
    public async Task<(DatabaseConnectionSettings? Settings, bool AllowsWrites, string? Code)> ResolveAsync(string key, bool requireWrites, CancellationToken ct)
    {
        var connection = await db.DatabaseConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Key == key, ct);
        if (connection is null || !connection.IsActive)
        {
            return (null, false, "db.connection_unavailable");
        }

        if (options.AllowedServers.Length > 0 && !options.AllowedServers.Contains(DatabaseServers.HostOf(connection.Server), StringComparer.OrdinalIgnoreCase))
        {
            return (null, connection.AllowWrites, "db.server_not_allowed");
        }

        if (requireWrites && !connection.AllowWrites)
        {
            return (null, false, "db.writes_not_allowed"); // before the password is decrypted
        }

        var password = "";
        if (connection.PasswordProtected is not null)
        {
            password = secrets.Unprotect(connection.PasswordProtected, DatabaseConnectionContexts.For(connection.Key)) ?? "";
            if (password.Length == 0)
            {
                return (null, connection.AllowWrites, "db.password_unreadable");
            }
        }

        var settings = new DatabaseConnectionSettings(
            connection.Provider, connection.Server, connection.Port, connection.Database, connection.UserName, password, connection.Encrypt, connection.TrustServerCertificate,
            TimeSpan.FromSeconds(connection.TimeoutSeconds));
        return (settings, connection.AllowWrites, null);
    }
}
