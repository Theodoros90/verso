using System.Data.Common;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using Verso.Application.Connections;

namespace Verso.Databases;

// Write runners of the Write to database step for PostgreSQL, MySQL / MariaDB and Oracle (Worker only). Connection strings come from the probes' Build (same TLS, timeout and
// no-pooling rules; only PostgreSQL drops the read-only session option). Running, parameters, the row cap, commit and rollback are shared in DbWriteExecution. The server's own
// text is never passed on. Classification: a failure the server reported for the statement means the transaction is rolled back and nothing changed (a known failure); a timeout,
// a lost connection or anything unexpected after the statement was sent leaves the outcome unknown; a failure before sending (cannot connect, login) changed nothing and is
// described like the probes do. None of this is proven against a live server.

internal static class WriteFailure
{
    /// <summary>
    /// "Known" relies on the executor rolling back in its finally block and on Pooling=false in the runners' connection strings (the rollback is also done by closing the connection): turning pooling on would break that assumption.
    /// The shared decision. <paramref name="known"/>: the server reported an error for the statement itself. Before sending, or for a known error, nothing changed
    /// (OutcomeUnknown false); anything else after sending is unknown. The code is the probe's wording, with the probe's catch-all turned into db.query_failed.
    /// </summary>
    public static DatabaseWriteResult Map(bool known, string described, bool sent)
    {
        if (sent && known)
        {
            return DatabaseWriteResult.Failure("db.query_failed", false);
        }

        return DatabaseWriteResult.Failure(described == "db.test_failed" ? "db.query_failed" : described, sent);
    }
}

/// <summary>PostgreSQL: a plain <c>BEGIN</c> on a session that is not read-only, then <c>COMMIT</c> or <c>ROLLBACK</c> by the shared executor.</summary>
public sealed class PostgreSqlWriteRunner : IProviderWriteRunner
{
    internal const string BeginStatement = "BEGIN";

    /// <summary>
    /// SQLSTATE classes and codes where the server rejected the statement and the transaction is rolled back: 22 data exception, 23 integrity constraint, 42 syntax error or access rule
    /// (includes 42501 insufficient privilege), 28 invalid authorization, 3D000 unknown database, 25006 read_only_sql_transaction (rejected before any change), 40001 serialization failure and 40P01 deadlock detected (PostgreSQL aborts the transaction;
    /// PostgreSQL documentation, appendix A). Everything else (57014 query_canceled, 08 connection exception, 53 insufficient resources, 57P0x operator intervention, 58 system error,
    /// XX internal) is unknown: the server or the connection may have gone away while the statement or the commit was in flight.
    /// </summary>
    internal static bool IsKnownStatementError(string? sqlState) =>
        sqlState is { Length: 5 } && (sqlState.StartsWith("22", StringComparison.Ordinal) || sqlState.StartsWith("23", StringComparison.Ordinal) || sqlState.StartsWith("42", StringComparison.Ordinal)
            || sqlState.StartsWith("28", StringComparison.Ordinal) || sqlState is "3D000" or "40001" or "40P01" or "25006");

    public string Provider => DatabaseProviders.PostgreSql;

    internal static string ConnectionString(DatabaseConnectionSettings s) => PostgreSqlProbe.Build(s, "Verso write", readOnly: false).ConnectionString;

    public Task<DatabaseWriteResult> RunAsync(DatabaseConnectionSettings settings, DatabaseWriteRequest request, CancellationToken ct) =>
        RunOn(new NpgsqlConnection(ConnectionString(settings)), request, ct);

    internal static Task<DatabaseWriteResult> RunOn(DbConnection connection, DatabaseWriteRequest request, CancellationToken ct) =>
        DbWriteExecution.RunAsync(connection, BeginStatement, true, request.Sql, request, "@", MapAny, ct);

    internal static DatabaseWriteResult MapAny(Exception ex, bool sent) => Map((ex as PostgresException)?.SqlState, ex, sent);

    internal static DatabaseWriteResult Map(string? sqlState, Exception ex, bool sent) =>
        WriteFailure.Map(IsKnownStatementError(sqlState), PostgreSqlProbe.Describe(ex), sent);
}

/// <summary>MySQL / MariaDB: <c>START TRANSACTION</c> (never READ ONLY), then <c>COMMIT</c> or <c>ROLLBACK</c> by the shared executor.</summary>
public sealed class MySqlWriteRunner : IProviderWriteRunner
{
    internal const string BeginStatement = "START TRANSACTION";

    /// <summary>
    /// ASSUMES a transactional engine (InnoDB): only then is a statement the server rejected rolled back with the transaction. On MyISAM / MEMORY tables a failed multi-row UPDATE or INSERT can leave
    /// earlier rows changed while this reports a known failure. With a non-strict sql_mode the data errors 1264 / 1265 / 1366 are warnings, not errors, so those codes may never be raised.
    /// Server error numbers where the statement was rejected (MySQL error reference): 1062 duplicate entry, 1048 column cannot be null, 1054 unknown column,
    /// 1146 table does not exist, 1064 syntax, 1136 column count mismatch, 1264 / 1265 / 1366 value out of range, truncated or incorrect, 1451 / 1452 foreign key,
    /// 1142 / 1143 / 1044 / 1045 privileges, 1213 deadlock (InnoDB rolls the transaction back). 1205 lock wait timeout is deliberately NOT here: the server only rolls back
    /// the statement and keeps the transaction (innodb_rollback_on_timeout is off by default), and MySqlConnector reports its own command timeout with the same number
    /// (<see cref="MySqlErrorCode.CommandTimeoutExpired"/>), so the two cannot be told apart and it stays an unknown outcome. 2006 / 2013 (server gone, connection lost) are unknown.
    /// </summary>
    internal static bool IsKnownStatementError(int number) => number is 1062 or 1048 or 1054 or 1146 or 1064 or 1136 or 1264 or 1265 or 1366 or 1451 or 1452 or 1142 or 1143 or 1044 or 1045 or 1213;

    public string Provider => DatabaseProviders.MySql;

    internal static string ConnectionString(DatabaseConnectionSettings s) => MySqlProbe.Build(s).ConnectionString;

    public Task<DatabaseWriteResult> RunAsync(DatabaseConnectionSettings settings, DatabaseWriteRequest request, CancellationToken ct) =>
        RunOn(new MySqlConnection(ConnectionString(settings)), request, ct);

    internal static Task<DatabaseWriteResult> RunOn(DbConnection connection, DatabaseWriteRequest request, CancellationToken ct) =>
        DbWriteExecution.RunAsync(connection, BeginStatement, true, request.Sql, request, "@", MapAny, ct);

    internal static DatabaseWriteResult MapAny(Exception ex, bool sent) => Map(ex is MySqlException m ? m.Number : 0, ex, sent);

    internal static DatabaseWriteResult Map(int number, Exception ex, bool sent) =>
        WriteFailure.Map(IsKnownStatementError(number), MySqlProbe.Describe(ex), sent);
}

/// <summary>
/// Oracle: a driver transaction (committed or rolled back by the shared executor), no begin statement. Oracle binds <c>:name</c>, so the <c>@name</c> placeholders become
/// <c>:p1..:pN</c> as in the Oracle query runner, parameters are bound by name, and a trailing semicolon (an error in Oracle) is dropped.
/// </summary>
public sealed class OracleWriteRunner : IProviderWriteRunner
{
    /// <summary>
    /// ORA numbers where the statement was rejected and rolled back: 1 unique constraint, 942 table or view does not exist, 904 invalid identifier, 903 invalid table name,
    /// 1031 insufficient privileges, 1400 / 1407 cannot insert or update NULL, 1722 invalid number, 2290 check constraint, 2291 / 2292 foreign key (parent / child),
    /// 12899 value too large for column, 1861 / 1843 date format and month, 1427 single-row subquery returns more than one row, 60 deadlock detected (Oracle rolls the statement back
    /// and we roll the transaction back; Oracle Database error messages). 3113 / 3114 / 3135 (connection lost), 12170 / 12541-12547 / 12571 (network, TNS) and timeouts are unknown.
    /// </summary>
    internal static bool IsKnownStatementError(int number) => number is 1 or 942 or 904 or 903 or 1031 or 1400 or 1407 or 1722 or 2290 or 2291 or 2292 or 12899 or 1861 or 1843 or 1427 or 60;

    public string Provider => DatabaseProviders.Oracle;

    internal static string ConnectionString(DatabaseConnectionSettings s) => OracleProbe.Build(s).ConnectionString;

    /// <summary>The statement for Oracle: placeholders become <c>:p1..:pN</c> by order of first use and a trailing semicolon is dropped (same rules as the query runner).</summary>
    internal static (string Sql, IReadOnlyList<string> Names) PrepareSql(string sql) => OracleQueryRunner.PrepareSql(sql);

    internal static DatabaseWriteRequest Renamed(DatabaseWriteRequest request, string sql, IReadOnlyList<string> names) =>
        request with { Sql = sql, Parameters = OracleQueryRunner.Rekey(request.Parameters, names) };

    public Task<DatabaseWriteResult> RunAsync(DatabaseConnectionSettings settings, DatabaseWriteRequest request, CancellationToken ct)
    {
        var (sql, names) = PrepareSql(request.Sql);
        DatabaseWriteRequest renamed;
        try
        {
            renamed = Renamed(request, sql, names);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(DatabaseWriteResult.Failure("sql.bad_parameter", false));
        }

        return DbWriteExecution.RunAsync(new OracleConnection(ConnectionString(settings)), null, false, sql, renamed, "", MapAny, ct, Configure);
    }

    /// <summary>Oracle binds by position by default; here by name (the names are synthetic and each appears once). Not proven without a live Oracle server.</summary>
    internal static void Configure(DbCommand command) => ((OracleCommand)command).BindByName = true;

    internal static DatabaseWriteResult MapAny(Exception ex, bool sent) => Map(ex is OracleException o ? o.Number : 0, ex, sent);

    internal static DatabaseWriteResult Map(int number, Exception ex, bool sent) =>
        WriteFailure.Map(IsKnownStatementError(number), OracleProbe.Describe(ex), sent);
}
