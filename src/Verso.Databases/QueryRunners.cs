using System.Data.Common;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using Verso.Application.Connections;
using Verso.Application.Executions;

namespace Verso.Databases;

// Query runners of the Query database step for PostgreSQL, MySQL / MariaDB and Oracle (Worker only). The connection strings come from the probes' Build
// (same TLS, timeout and no-pooling rules); running, binding, rollback and result reading are shared in DbQueryExecution. The server's own text is never passed on.

/// <summary>PostgreSQL: a <c>BEGIN READ ONLY</c> transaction on a session that is already <c>default_transaction_read_only=on</c>, always rolled back.</summary>
public sealed class PostgreSqlQueryRunner : IProviderQueryRunner
{
    internal const string ReadOnlyStatement = "BEGIN READ ONLY";

    public string Provider => DatabaseProviders.PostgreSql;

    internal static string ConnectionString(DatabaseConnectionSettings s) => PostgreSqlProbe.Build(s, "Verso query").ConnectionString;

    public Task<DatabaseQueryResult> RunAsync(DatabaseConnectionSettings settings, DatabaseQueryRequest request, CancellationToken ct) =>
        DbQueryExecution.RunAsync(new NpgsqlConnection(ConnectionString(settings)), ReadOnlyStatement, true, request.Sql, request, "@", Map, ct);

    internal static DatabaseQueryResult Map(Exception ex) => DbQueryExecution.MapFailure(
        PostgreSqlProbe.Describe(ex),
        ex is PostgresException { SqlState: { } state } && (state.StartsWith("42", StringComparison.Ordinal) || state.StartsWith("22", StringComparison.Ordinal) || state.StartsWith("23", StringComparison.Ordinal))); // syntax, permission, bad value
}

/// <summary>MySQL / MariaDB: <c>START TRANSACTION READ ONLY</c>, always rolled back.</summary>
public sealed class MySqlQueryRunner : IProviderQueryRunner
{
    internal const string ReadOnlyStatement = "START TRANSACTION READ ONLY";

    public string Provider => DatabaseProviders.MySql;

    internal static string ConnectionString(DatabaseConnectionSettings s) => MySqlProbe.Build(s).ConnectionString;

    public Task<DatabaseQueryResult> RunAsync(DatabaseConnectionSettings settings, DatabaseQueryRequest request, CancellationToken ct) =>
        DbQueryExecution.RunAsync(new MySqlConnection(ConnectionString(settings)), ReadOnlyStatement, true, request.Sql, request, "@", Map, ct);

    internal static DatabaseQueryResult Map(Exception ex) => DbQueryExecution.MapFailure(
        MySqlProbe.Describe(ex),
        ex is MySqlException { ErrorCode: MySqlErrorCode.ParseError or MySqlErrorCode.BadFieldError or MySqlErrorCode.NoSuchTable or MySqlErrorCode.TableAccessDenied or MySqlErrorCode.ColumnAccessDenied or MySqlErrorCode.DatabaseAccessDenied });
}

/// <summary>
/// Oracle: <c>SET TRANSACTION READ ONLY</c> as the first statement of a driver transaction that is always rolled back. Oracle binds <c>:name</c>, so the
/// <c>@name</c> placeholders are rewritten outside text and comments, parameters are bound by name, and a trailing semicolon (an error in Oracle) is dropped.
/// </summary>
public sealed class OracleQueryRunner : IProviderQueryRunner
{
    internal const string ReadOnlyStatement = "SET TRANSACTION READ ONLY";

    public string Provider => DatabaseProviders.Oracle;

    internal static string ConnectionString(DatabaseConnectionSettings s) => OracleProbe.Build(s).ConnectionString;

    /// <summary>
    /// The statement for Oracle: placeholders become <c>:p1..:pN</c> by order of first use (Oracle limits bind names to 30 characters, forbids reserved words such as :date or :level
    /// and ignores case, none of which the guard's names are held to), and a trailing semicolon is dropped. <c>Names</c> are the original placeholder names, index n is <c>p(n+1)</c>.
    /// </summary>
    internal static (string Sql, IReadOnlyList<string> Names) PrepareSql(string sql)
    {
        var rewritten = SqlGuard.RewriteParametersNumbered(sql.TrimEnd().TrimEnd(';').TrimEnd(), ":", out var names);
        return (rewritten, names);
    }

    /// <summary>The request's values re-keyed to the synthetic names p1..pN (a missing exact name falls back to a case-insensitive match, as the service checks them).</summary>
    internal static DatabaseQueryRequest Renamed(DatabaseQueryRequest request, string sql, IReadOnlyList<string> names) =>
        request with { Sql = sql, Parameters = Rekey(request.Parameters, names) };

    /// <summary>The values re-keyed to p1..pN by the original names (shared with the Oracle write runner). Throws <see cref="ArgumentException"/> when a name has no value.</summary>
    internal static Dictionary<string, object?> Rekey(IReadOnlyDictionary<string, object?> parameters, IReadOnlyList<string> names)
    {
        var bound = new Dictionary<string, object?>(names.Count, StringComparer.Ordinal);
        for (var n = 0; n < names.Count; n++)
        {
            var key = parameters.Keys.FirstOrDefault(k => k.TrimStart('@') == names[n])
                ?? parameters.Keys.FirstOrDefault(k => string.Equals(k.TrimStart('@'), names[n], StringComparison.OrdinalIgnoreCase));
            if (key is null)
            {
                throw new ArgumentException("A parameter is missing.");
            }

            bound["p" + (n + 1)] = parameters[key];
        }

        return bound;
    }

    public Task<DatabaseQueryResult> RunAsync(DatabaseConnectionSettings settings, DatabaseQueryRequest request, CancellationToken ct)
    {
        var (sql, names) = PrepareSql(request.Sql);
        DatabaseQueryRequest renamed;
        try
        {
            renamed = Renamed(request, sql, names);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(DatabaseQueryResult.Failure("sql.bad_parameter", false));
        }

        return DbQueryExecution.RunAsync(
            new OracleConnection(ConnectionString(settings)), ReadOnlyStatement, false, sql, renamed, "", Map, ct, Configure);
    }

    /// <summary>
    /// By default Oracle binds by position (here by name, though the names are synthetic and each appears once), and fetches a LONG column as empty (InitialLONGFetchSize 0) and a LOB as a locator:
    /// both are set to one more than the result cap so the shared reader sees an oversized value and answers db.result_too_large instead of an empty cell.
    /// Known limitation: an Oracle NUMBER with more precision than .NET's decimal (about 28 digits) makes the driver throw on read; that is reported as db.query_failed (use TO_CHAR or ROUND in the query).
    /// Not proven without a live Oracle server.
    /// </summary>
    internal static void Configure(DbCommand command)
    {
        var oracle = (OracleCommand)command;
        oracle.BindByName = true;
        oracle.InitialLONGFetchSize = DbQueryExecution.MaxResultChars + 1;
        oracle.InitialLOBFetchSize = DbQueryExecution.MaxResultChars + 1;
    }

    internal static DatabaseQueryResult Map(Exception ex) => DbQueryExecution.MapFailure(
        OracleProbe.Describe(ex),
        ex is OracleException { Number: (>= 900 and <= 999) or 1031 or 1722 or 1843 }); // syntax, missing table or column, privileges, bad value
}
