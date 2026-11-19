namespace Verso.Application.Connections;

/// <summary>
/// One INSERT, UPDATE or DELETE for the Write to database step. <see cref="Parameters"/> are keyed by the placeholder name without the <c>@</c>; their values are
/// only ever bound as driver parameters, never joined into <see cref="Sql"/>. <see cref="MaxRows"/> is the most rows the statement may change (more: rolled back).
/// </summary>
public sealed record DatabaseWriteRequest(string ConnectionKey, string Sql, IReadOnlyDictionary<string, object?> Parameters, int MaxRows, TimeSpan Timeout);

/// <summary>
/// The outcome of a write: either a failure (<see cref="Code"/> and Verso's own <see cref="Message"/>, never the driver's text, the SQL or a value) or the rows changed.
/// <see cref="OutcomeUnknown"/> is true when the statement may have reached the database and Verso cannot tell whether it changed data (a timeout, a lost connection,
/// a failing commit): the message then says so. Nothing is ever retried automatically, so there is no "retryable" flag.
/// </summary>
public sealed record DatabaseWriteResult(string? Code, string? Message, bool OutcomeUnknown, int RowsAffected, long DurationMs)
{
    public bool Ok => Code is null;

    public static DatabaseWriteResult Failure(string code, bool outcomeUnknown) =>
        new(code, outcomeUnknown ? DatabaseQueryMessages.OutcomeUnknown : DatabaseQueryMessages.For(code), outcomeUnknown, 0, 0);

    public static DatabaseWriteResult Success(int rowsAffected, long durationMs) => new(null, null, false, rowsAffected, durationMs);
}

/// <summary>Runs one write on a saved database connection that allows writes. Implemented in Application, registered in the Worker only.</summary>
public interface IDatabaseWrite
{
    Task<DatabaseWriteResult> RunAsync(DatabaseWriteRequest request, CancellationToken ct);
}

/// <summary>One provider's write runner, found by <see cref="Provider"/> (a <see cref="DatabaseProviders"/> value). Commits only when no more than MaxRows rows changed.</summary>
public interface IProviderWriteRunner
{
    string Provider { get; }

    Task<DatabaseWriteResult> RunAsync(DatabaseConnectionSettings settings, DatabaseWriteRequest request, CancellationToken ct);
}
