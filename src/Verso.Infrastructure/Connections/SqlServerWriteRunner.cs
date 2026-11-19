using Microsoft.Data.SqlClient;
using Verso.Application.Connections;

namespace Verso.Infrastructure.Connections;

/// <summary>
/// SQL Server runner of the Write to database step. Worker only. Same connection string rules as <see cref="SqlServerQueryRunner"/> (no pooling, the same TLS and
/// timeout settings) but without read-only intent. The statement runs in a driver transaction that is committed only when it changed no more rows than allowed
/// (all in <see cref="DbWriteExecution"/>, shared with the other providers). The server's own text is never passed on (it can name the server, the login or a value).
/// </summary>
public sealed class SqlServerWriteRunner : IProviderWriteRunner
{
    /// <summary>
    /// Errors SQL Server reports for the statement itself while the session stays usable, so the transaction is rolled back and nothing changed: a known failure.
    /// 102/156 syntax, 207/208 unknown column/object, 213/109/110 column count, 229/230 permission, 262 permission in database, 547 constraint, 2601/2627 duplicate key,
    /// 515 NULL into NOT NULL, 8152/2628 truncation (2628 is the SQL Server 2019+ wording), 245/8114 conversion, 8115/220 overflow, 8134 divide by zero,
    /// 1205 deadlock victim (the server rolled the transaction back).
    /// </summary>
    internal static readonly IReadOnlySet<int> KnownStatementErrors = new HashSet<int>
    {
        102, 156, 207, 208, 213, 109, 110, 229, 230, 262, 547, 2601, 2627, 515, 8152, 2628, 245, 8114, 8115, 220, 8134, 1205,
    };

    public string Provider => DatabaseProviders.SqlServer;

    public Task<DatabaseWriteResult> RunAsync(DatabaseConnectionSettings settings, DatabaseWriteRequest request, CancellationToken ct) =>
        DbWriteExecution.RunAsync(new SqlConnection(ConnectionString(settings)), null, false, request.Sql, request, "@", MapAny, ct);

    internal static string ConnectionString(DatabaseConnectionSettings settings)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = SqlServerProbe.DataSource(settings),
            UserID = settings.UserName,
            Password = settings.Password,
            Encrypt = settings.Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = settings.TrustServerCertificate,
            ConnectTimeout = Math.Max(1, (int)settings.Timeout.TotalSeconds),
            Pooling = false,
            ApplicationName = "Verso write",
        };
        if (settings.Database.Length > 0)
        {
            builder.InitialCatalog = settings.Database;
        }

        return builder.ConnectionString;
    }

    /// <summary>A <see cref="SqlException"/> by its number; a driver <see cref="TimeoutException"/> is db.timeout; anything else db.query_failed. Unknown outcome only after sending.</summary>
    internal static DatabaseWriteResult MapAny(Exception ex, bool sent) => ex switch
    {
        SqlException sql => Map(sql.Number, sql.Message, sent),
        TimeoutException => DatabaseWriteResult.Failure("db.timeout", sent),
        _ => DatabaseWriteResult.Failure("db.query_failed", sent),
    };

    /// <summary>
    /// Before sending (cannot connect, login failed, ...): the probe's wording, a known failure. After sending: a statement error SQL Server reported is known
    /// (<see cref="KnownStatementErrors"/>); a timeout, a lost connection or anything else is an unknown outcome.
    /// </summary>
    internal static DatabaseWriteResult Map(int number, string? serverMessage, bool sent)
    {
        if (sent && KnownStatementErrors.Contains(number))
        {
            return DatabaseWriteResult.Failure("db.query_failed", false);
        }

        var (code, _) = SqlServerProbe.Describe(number, serverMessage);
        return DatabaseWriteResult.Failure(code == "db.test_failed" ? "db.query_failed" : code, sent);
    }
}
