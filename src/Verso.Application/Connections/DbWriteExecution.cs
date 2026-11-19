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
    /// after the rollback attempt (the engine's shutdown handling decides what that means for the step).
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
        var clock = Stopwatch.StartNew();
        var sent = false;
        var committing = false;
        try
        {
            await using (connection)
            {
                await connection.OpenAsync(ct);
                DbTransaction? transaction = null;
                if (!rawTransactionStatements)
                {
                    transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                }

                var committed = false;
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
                        return DatabaseWriteResult.Failure("db.query_failed", false);
                    }

                    if (rows > request.MaxRows)
                    {
                        return DatabaseWriteResult.Failure("db.too_many_rows_affected", false); // rolled back in finally: nothing changed
                    }

                    // The commit is not cancelled: once the statement ran, finishing it is better than leaving its outcome open.
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

                    committed = true;
                    return DatabaseWriteResult.Success(rows, clock.ElapsedMilliseconds);
                }
                finally
                {
                    if (!committed)
                    {
                        await DbQueryExecution.RollbackAsync(connection, transaction, rawTransactionStatements);
                    }

                    if (transaction is not null)
                    {
                        await transaction.DisposeAsync();
                    }
                }
            }
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
}
