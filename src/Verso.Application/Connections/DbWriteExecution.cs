using System.Data;
using System.Data.Common;
using System.Diagnostics;

namespace Verso.Application.Connections;

/// <summary>
/// The provider-neutral part of every write runner of the Write to database step: one INSERT, UPDATE or DELETE inside a transaction, committed only when it changed
/// no more than <see cref="DatabaseWriteRequest.MaxRows"/> rows, rolled back otherwise. It only knows <see cref="DbConnection"/>, so no driver is referenced here.
/// Each runner supplies its connection, how its transaction begins and its error mapping. Nothing here is ever retried.
/// </summary>
public static class DbWriteExecution
{
    /// <summary>
    /// Opens <paramref name="connection"/>, begins a transaction, runs <paramref name="sql"/> with <see cref="DbCommand.ExecuteNonQueryAsync(CancellationToken)"/>,
    /// and commits, or rolls back when more rows changed than allowed (or the count is unknown).
    /// When <paramref name="rawTransactionStatements"/> is false a driver transaction is used (SQL Server, Oracle) and <paramref name="beginStatement"/>, if any, runs inside it;
    /// when true <paramref name="beginStatement"/> itself begins the transaction (PostgreSQL <c>BEGIN</c>, MySQL <c>START TRANSACTION</c>) and raw <c>COMMIT</c> / <c>ROLLBACK</c> end it.
    /// <paramref name="map"/> turns a driver failure into a result and is told whether the statement had been sent (a failure before that changed nothing).
    /// A failing commit is always an unknown outcome. A failure while <paramref name="ct"/> is cancelled is thrown as <see cref="OperationCanceledException"/>
    /// after the rollback attempt (the engine's shutdown handling decides what that means for the step). Once the commit succeeded the result is success,
    /// whatever closing the transaction or the connection throws. Raw mode without a <paramref name="beginStatement"/> is refused with <see cref="ArgumentException"/>.
    /// </summary>
    public static async Task<DatabaseWriteResult> RunAsync(
        DbConnection connection,
        string? beginStatement,
        bool rawTransactionStatements,
        string sql,
        DatabaseWriteRequest request,
        string parameterPrefix,
        Func<Exception, bool, DatabaseWriteResult> map,
        CancellationToken ct,
        Action<DbCommand>? configureCommand = null)
    {
        if (rawTransactionStatements && beginStatement is null)
        {
            // Raw mode without a BEGIN would run the statement in autocommit: the cap could not roll it back.
            throw new ArgumentException("A raw transaction needs the statement that begins it.", nameof(beginStatement));
        }

        var clock = Stopwatch.StartNew();
        var sent = false;
        var committing = false;
        DatabaseWriteResult? committedResult = null; // set once the commit succeeded: from then on the result is success, whatever happens while closing
        try
        {
            try
            {
                await connection.OpenAsync(ct);
                DbTransaction? transaction = null;
                if (!rawTransactionStatements)
                {
                    transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                }

                try
                {
                    if (beginStatement is not null)
                    {
                        await using var begin = connection.CreateCommand();
                        begin.Transaction = transaction;
                        begin.CommandText = beginStatement;
                        await begin.ExecuteNonQueryAsync(ct);
                    }

                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    command.CommandTimeout = Math.Max(1, (int)Math.Ceiling(request.Timeout.TotalSeconds));
                    configureCommand?.Invoke(command);
                    DbQueryExecution.AddParameters(command, request.Parameters, parameterPrefix);

                    sent = true; // from here on the statement may have reached the database
                    var rows = await command.ExecuteNonQueryAsync(ct);
                    if (rows < 0)
                    {
                        // No row count (for example SET NOCOUNT ON set on the server): the cap can't be checked, so nothing is kept (the rollback is in finally).
                        return DatabaseWriteResult.Failure("db.rows_affected_unknown", false);
                    }

                    if (rows > request.MaxRows)
                    {
                        return DatabaseWriteResult.Failure("db.too_many_rows_affected", false); // rolled back in finally: nothing changed
                    }

                    // The commit is not cancelled: once the statement ran, finishing it is better than leaving its outcome open. It has no time bound of its own
                    // beyond the driver's (the command / connection timeout); a commit that never answers holds the step until the driver gives up.
                    committing = true;
                    if (transaction is not null)
                    {
                        await transaction.CommitAsync(CancellationToken.None);
                    }
                    else
                    {
                        await using var commit = connection.CreateCommand();
                        commit.CommandText = "COMMIT";
                        await commit.ExecuteNonQueryAsync(CancellationToken.None);
                    }

                    committing = false;
                    committedResult = DatabaseWriteResult.Success(rows, clock.ElapsedMilliseconds);
                    return committedResult;
                }
                finally
                {
                    if (committedResult is null)
                    {
                        await DbQueryExecution.RollbackAsync(connection, transaction, rawTransactionStatements);
                    }

                    if (transaction is not null)
                    {
                        await DisposeQuietlyAsync(transaction);
                    }
                }
            }
            finally
            {
                await DisposeQuietlyAsync(connection);
            }
        }
        catch (Exception) when (committedResult is not null)
        {
            return committedResult; // committed: a failure while closing (or a cancel arriving now) does not change what happened
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct); // a driver-side cancel (or what a driver throws after one) is a cancel; the rollback was attempted above
        }
        catch (Exception ex) when (ex is not OperationCanceledException && committing)
        {
            // A failing commit: the server may or may not have committed. Keep the real code, never the driver's text.
            var mapped = ex is DbException or TimeoutException ? map(ex, true) : DatabaseWriteResult.Failure("db.query_failed", true);
            return mapped.OutcomeUnknown ? mapped : DatabaseWriteResult.Failure(mapped.Code ?? "db.query_failed", true);
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            return map(ex, sent);
        }
        // Anything else (an InvalidCastException from a value the driver cannot convert, an IOException, ...) is a failure with Verso's own wording: never ex.Message,
        // which can carry a value or a server detail. After the statement was sent its outcome is unknown. A cancel keeps propagating.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DatabaseWriteResult.Failure("db.query_failed", sent);
        }
    }

    /// <summary>Closing must not replace a known result: the same failures the rollback tolerates are ignored here (the server discards an uncommitted transaction with the session).</summary>
    private static async Task DisposeQuietlyAsync(IAsyncDisposable disposable)
    {
        try
        {
            await disposable.DisposeAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbException or TimeoutException or IOException)
        {
            // nothing to do: the result is already known
        }
    }
}
