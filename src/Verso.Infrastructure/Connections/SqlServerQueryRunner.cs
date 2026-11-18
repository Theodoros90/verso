using Microsoft.Data.SqlClient;
using Verso.Application.Connections;

namespace Verso.Infrastructure.Connections;

/// <summary>
/// SQL Server runner of the Query database step. Worker only. Same connection string rules as <see cref="SqlServerProbe"/> (read-only intent, no pooling).
/// The query runs inside a transaction that is always rolled back, parameters are bound as driver parameters, and a statement that returns no result set
/// is a failure (all in <see cref="DbQueryExecution"/>, shared with the other providers). The server's own text is never passed on (it can name the server, the login or a value).
/// </summary>
public sealed class SqlServerQueryRunner : IProviderQueryRunner
{
    /// <summary>The serialised result may not exceed this many characters; beyond it the rows are cut and the result is marked truncated.</summary>
    internal const int MaxResultChars = DbQueryExecution.MaxResultChars;

    public string Provider => DatabaseProviders.SqlServer;

    public Task<DatabaseQueryResult> RunAsync(DatabaseConnectionSettings settings, DatabaseQueryRequest request, CancellationToken ct)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = SqlServerProbe.DataSource(settings),
            UserID = settings.UserName,
            Password = settings.Password,
            Encrypt = settings.Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = settings.TrustServerCertificate,
            ConnectTimeout = Math.Max(1, (int)settings.Timeout.TotalSeconds),
            ApplicationIntent = ApplicationIntent.ReadOnly,
            Pooling = false,
            ApplicationName = "Verso query",
        };
        if (settings.Database.Length > 0)
        {
            builder.InitialCatalog = settings.Database;
        }

        return DbQueryExecution.RunAsync(
            new SqlConnection(builder.ConnectionString), null, false, request.Sql, request, "@",
            ex => ex is SqlException sql ? Map(sql) : DatabaseQueryResult.Failure("db.query_failed", false), ct);
    }

    internal static DatabaseQueryResult Map(SqlException ex)
    {
        var (code, _) = SqlServerProbe.Describe(ex.Number, ex.Message);
        return DbQueryExecution.MapFailure(code, ex.Number is 102 or 156 or 207 or 208 or 229 or 262 or 4060);
    }
}
