using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Verso.Application.Connections;

/// <summary>
/// The provider-neutral part of every query runner of the Query database step (SQL Server, PostgreSQL, MySQL, Oracle): running one statement inside a read-only
/// transaction that is always rolled back, binding parameters, and reading the result with the column, row and size limits. It only knows
/// <see cref="DbConnection"/> and <see cref="DbDataReader"/>, so no driver is referenced here. Each runner supplies its connection, its read-only statement and its error mapping.
/// </summary>
public static class DbQueryExecution
{
    /// <summary>The serialised result may not exceed this many characters; beyond it the rows are cut and the result is marked truncated.</summary>
    public const int MaxResultChars = 256 * 1024;

    /// <summary>
    /// Opens <paramref name="connection"/>, starts a read-only transaction, runs the query and always rolls back.
    /// <paramref name="readOnlyStatement"/> is the provider's statement (null: the driver's own transaction is enough); when <paramref name="rawTransactionStatements"/>
    /// is true the statement itself begins the transaction (PostgreSQL, MySQL) and "ROLLBACK" ends it, otherwise a driver transaction is used and the statement runs inside it (Oracle).
    /// <paramref name="map"/> turns a driver failure into a result; a driver failure during a requested cancel is rethrown as <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<DatabaseQueryResult> RunAsync(
        DbConnection connection,
        string? readOnlyStatement,
        bool rawTransactionStatements,
        string sql,
        DatabaseQueryRequest request,
        string parameterPrefix,
        Func<Exception, DatabaseQueryResult> map,
        CancellationToken ct,
        Action<DbCommand>? configureCommand = null)
    {
        var clock = Stopwatch.StartNew();
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

                try
                {
                    if (readOnlyStatement is not null)
                    {
                        await using var begin = connection.CreateCommand();
                        begin.Transaction = transaction;
                        begin.CommandText = readOnlyStatement;
                        await begin.ExecuteNonQueryAsync(ct);
                    }

                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    command.CommandTimeout = Math.Max(1, (int)Math.Ceiling(request.Timeout.TotalSeconds));
                    configureCommand?.Invoke(command);
                    AddParameters(command, request.Parameters, parameterPrefix);

                    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
                    return await ReadAsync(reader, request.MaxRows, clock, ct);
                }
                finally
                {
                    await RollbackAsync(connection, transaction, rawTransactionStatements);
                    if (transaction is not null)
                    {
                        await transaction.DisposeAsync();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is DbException or TimeoutException && ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct); // a driver-side cancel is a cancel, not a retryable failure
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            return map(ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or IOException)
        {
            return DatabaseQueryResult.Failure("db.query_failed", false);
        }
    }

    private static async Task RollbackAsync(DbConnection connection, DbTransaction? transaction, bool raw)
    {
        // Always: undoes the data changes of the transaction, but not every side effect (sequences, linked servers), so the connection's login must itself be read-only.
        try
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            else if (raw && connection.State == ConnectionState.Open)
            {
                await using var rollback = connection.CreateCommand();
                rollback.CommandText = "ROLLBACK";
                await rollback.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbException)
        {
            // the connection is gone or the transaction already ended; the connection is disposed next, which discards it
        }
    }

    /// <summary>Binds every parameter as a driver parameter named <paramref name="prefix"/> + name (a value is never joined into the text). null becomes DBNull.</summary>
    public static void AddParameters(DbCommand command, IReadOnlyDictionary<string, object?> parameters, string prefix)
    {
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = prefix + name.TrimStart('@');
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }

    /// <summary>
    /// Reads the result set: a statement without one is a failure, column names must be present and unique, at most <paramref name="maxRows"/> rows are kept
    /// (a further row marks the result truncated) and the serialised size is capped at <see cref="MaxResultChars"/>.
    /// </summary>
    public static async Task<DatabaseQueryResult> ReadAsync(DbDataReader reader, int maxRows, Stopwatch clock, CancellationToken ct)
    {
        if (reader.FieldCount == 0)
        {
            return DatabaseQueryResult.Failure("db.query_failed", false); // not a query (the rollback undoes anything it did)
        }

        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        if (columns.Any(string.IsNullOrWhiteSpace) || columns.Distinct(StringComparer.Ordinal).Count() != columns.Count)
        {
            return DatabaseQueryResult.Failure("db.bad_column", false); // a nameless or repeated column would silently lose data
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var truncated = false;
        var size = 0;
        while (await reader.ReadAsync(ct))
        {
            if (rows.Count >= Math.Max(0, maxRows))
            {
                truncated = true; // a further row exists
                break;
            }

            var row = new Dictionary<string, object?>(columns.Count, StringComparer.Ordinal);
            var tooLarge = false;
            for (var i = 0; i < columns.Count && !tooLarge; i++)
            {
                var (value, fits) = await ReadCellAsync(reader, i, MaxResultChars - size, ct);
                tooLarge = !fits;
                row[columns[i]] = value;
            }

            if (!tooLarge)
            {
                size += JsonSerializer.Serialize(row).Length;
                tooLarge = size > MaxResultChars;
            }

            if (tooLarge)
            {
                // Not even one row fits: say so, an empty "truncated" result would read as "no record".
                return rows.Count == 0 ? DatabaseQueryResult.Failure("db.result_too_large", false) : DatabaseQueryResult.Success(columns, rows, true, clock.ElapsedMilliseconds);
            }

            rows.Add(row);
        }

        return DatabaseQueryResult.Success(columns, rows, truncated, clock.ElapsedMilliseconds);
    }

    /// <summary>
    /// Reads one cell without ever holding more than <paramref name="budget"/> characters of it: text and binary are read in chunks and
    /// abandoned as soon as they are over (fits = false). The reader may be sequential, so columns must be read in order.
    /// </summary>
    private static async Task<(object? Value, bool Fits)> ReadCellAsync(DbDataReader reader, int i, int budget, CancellationToken ct)
    {
        if (await reader.IsDBNullAsync(i, ct))
        {
            return (null, true);
        }

        var type = reader.GetFieldType(i);
        if (type == typeof(string))
        {
            var text = new StringBuilder();
            var buffer = new char[4096];
            long offset = 0;
            int read;
            while ((read = (int)reader.GetChars(i, offset, buffer, 0, buffer.Length)) > 0)
            {
                text.Append(buffer, 0, read);
                offset += read;
                if (text.Length > budget)
                {
                    return (null, false);
                }
            }

            return (text.ToString(), true);
        }

        if (type == typeof(byte[]))
        {
            var bytes = new MemoryStream();
            var buffer = new byte[4096];
            long offset = 0;
            int read;
            while ((read = (int)reader.GetBytes(i, offset, buffer, 0, buffer.Length)) > 0)
            {
                bytes.Write(buffer, 0, read);
                offset += read;
                if (bytes.Length > budget / 4 * 3) // base64 makes it a third longer in the JSON
                {
                    return (null, false);
                }
            }

            return (bytes.ToArray(), true);
        }

        return (reader.GetValue(i), true);
    }

    /// <summary>
    /// Shared error wording of the runners: <paramref name="probeCode"/> is the provider probe's description of the failure. Timeouts and unreachable servers can be retried;
    /// an unknown failure becomes db.query_failed, retryable unless <paramref name="deterministic"/> (a syntax or permission error that will fail again); any other code is final.
    /// </summary>
    public static DatabaseQueryResult MapFailure(string probeCode, bool deterministic) => probeCode switch
    {
        "db.timeout" or "db.unreachable" => DatabaseQueryResult.Failure(probeCode, true),
        "db.test_failed" => DatabaseQueryResult.Failure("db.query_failed", !deterministic),
        _ => DatabaseQueryResult.Failure(probeCode, false),
    };
}
