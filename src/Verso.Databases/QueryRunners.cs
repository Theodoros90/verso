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

    internal static string PrepareSql(string sql) => SqlGuard.RewriteParameters(sql.TrimEnd().TrimEnd(';').TrimEnd(), ":");

    public Task<DatabaseQueryResult> RunAsync(DatabaseConnectionSettings settings, DatabaseQueryRequest request, CancellationToken ct) =>
        DbQueryExecution.RunAsync(
            new OracleConnection(ConnectionString(settings)), ReadOnlyStatement, false, PrepareSql(request.Sql), request, "", Map, ct,
            command => ((OracleCommand)command).BindByName = true); // by default Oracle binds by position, which breaks a placeholder used twice

    internal static DatabaseQueryResult Map(Exception ex) => DbQueryExecution.MapFailure(
        OracleProbe.Describe(ex),
        ex is OracleException { Number: (>= 900 and <= 999) or 1031 or 1722 or 1843 }); // syntax, missing table or column, privileges, bad value
}
