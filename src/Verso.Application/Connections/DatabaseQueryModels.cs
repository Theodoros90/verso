namespace Verso.Application.Connections;

/// <summary>
/// One read-only query for the Query database step. <see cref="Parameters"/> are keyed by the placeholder name without the <c>@</c>; their values are only
/// ever bound as driver parameters, never joined into <see cref="Sql"/>.
/// </summary>
public sealed record DatabaseQueryRequest(string ConnectionKey, string Sql, IReadOnlyDictionary<string, object?> Parameters, int MaxRows, TimeSpan Timeout);

/// <summary>
/// The outcome of a query: either a failure (<see cref="Code"/> and Verso's own <see cref="Message"/>, never the driver's text, the server, the login or a parameter value)
/// or the columns and rows read. <see cref="Truncated"/> is true when more rows existed than <c>MaxRows</c> or the result reached the size cap.
/// </summary>
public sealed record DatabaseQueryResult(
    string? Code, string? Message, bool Retryable, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, bool Truncated, long DurationMs)
{
    public bool Ok => Code is null;

    public static DatabaseQueryResult Failure(string code, bool retryable) =>
        new(code, DatabaseQueryMessages.For(code), retryable, [], [], false, 0);

    public static DatabaseQueryResult Success(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, bool truncated, long durationMs) =>
        new(null, null, false, columns, rows, truncated, durationMs);
}

/// <summary>Runs one query on a saved database connection. Implemented in Application, registered in the Worker only.</summary>
public interface IDatabaseQuery
{
    Task<DatabaseQueryResult> RunAsync(DatabaseQueryRequest request, CancellationToken ct);
}

/// <summary>One provider's query runner, found by <see cref="Provider"/> (a <see cref="DatabaseProviders"/> value). Always read-only and rolled back.</summary>
public interface IProviderQueryRunner
{
    string Provider { get; }

    Task<DatabaseQueryResult> RunAsync(DatabaseConnectionSettings settings, DatabaseQueryRequest request, CancellationToken ct);
}

/// <summary>Verso's own wording for each failure code of a query.</summary>
public static class DatabaseQueryMessages
{
    /// <summary>A write that may or may not have changed data (the code of the failure stays the real one: db.timeout, db.unreachable, db.query_failed, ...).</summary>
    public const string OutcomeUnknown =
        "The outcome is unknown: the statement may or may not have changed data. Check the database before running this step again.";

    public static string For(string code) => code switch
    {
        "db.writes_not_allowed" => "Writes are switched off for this connection. An administrator can switch them on in Configuration, Connections.",
        "db.too_many_rows_affected" => "The statement would change more rows than the step allows, so nothing was changed.",
        "db.rows_affected_unknown" => "The server did not report how many rows changed (for example because of SET NOCOUNT), so nothing was kept.",
        "db.connection_unavailable" => "The connection no longer exists or is switched off.",
        "db.server_not_allowed" => "The administrator has not allowed the Worker to connect to this server (DatabaseConnections:AllowedServers).",
        "db.password_unreadable" => "The stored password can't be read: the Worker's Secrets:MasterKey is missing or differs from the API's.",
        "db.query_failed" => "The database could not run the query. Check the query, the table and column names and the login's permissions.",
        "db.result_too_large" => "The result is larger than the 256 KB limit. Select fewer or shorter columns.",
        "db.bad_column" => "Every column needs a unique name. Give each column an alias with AS.",
        "sql.empty" => "Type a SELECT query.",
        "sql.too_long" => "The query is too long.",
        "sql.multiple_statements" => "Only one statement is allowed.",
        "sql.not_select" => "Only a SELECT query is allowed (a WITH ... SELECT is fine).",
        "sql.select_into" => "SELECT ... INTO is not allowed.",
        "sql.not_write" => "Only an INSERT, UPDATE or DELETE statement is allowed.",
        "sql.no_where" => "UPDATE and DELETE need a WHERE condition, so a statement can never change a whole table by accident.",
        "sql.unterminated" => "The query has an unclosed quote or comment.",
        "sql.bad_parameter" => "A parameter is missing or its name is not valid.",
        "sql.unsupported_syntax" => "This SQL syntax is not supported by the Query database step.",
        _ => DatabaseProbeMessages.For(code),
    };
}
