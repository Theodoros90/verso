using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Verso.Application.Connections;

namespace Verso.Infrastructure.Connections;

/// <summary>
/// SQL Server runner of the Query database step. Worker only. Same connection string rules as <see cref="SqlServerProbe"/> (read-only intent, no pooling).
/// The query runs inside a transaction that is always rolled back, parameters are bound as driver parameters, and a statement that returns no result set
/// is a failure. The server's own text is never passed on (it can name the server, the login or a value).
/// </summary>
public sealed class SqlServerQueryRunner : IProviderQueryRunner
{
    /// <summary>The serialised result may not exceed this many characters; beyond it the rows are cut and the result is marked truncated.</summary>
    internal const int MaxResultChars = 256 * 1024;

    public string Provider => DatabaseProviders.SqlServer;

    public async Task<DatabaseQueryResult> RunAsync(DatabaseConnectionSettings settings, DatabaseQueryRequest request, CancellationToken ct)
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

        var clock = Stopwatch.StartNew();
        try
        {
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(ct);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            try
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = request.Sql;
                command.CommandTimeout = Math.Max(1, (int)Math.Ceiling(request.Timeout.TotalSeconds));
                foreach (var (name, value) in request.Parameters)
                {
                    command.Parameters.Add(new SqlParameter("@" + name.TrimStart('@'), value ?? DBNull.Value));
                }

                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
                if (reader.FieldCount == 0)
                {
                    return DatabaseQueryResult.Failure("db.query_failed", false); // not a query (the rollback below undoes anything it did)
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
                    if (rows.Count >= Math.Max(0, request.MaxRows))
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
            finally
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None); // always: undoes the data changes of the transaction, but not every side effect (sequences, linked servers), so the connection's login must itself be read-only
                }
                catch (Exception ex) when (ex is InvalidOperationException or SqlException)
                {
                    // the connection is gone or the transaction already ended; the connection is disposed next, which discards it
                }
            }
        }
        catch (SqlException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct); // a driver-side cancel is a cancel, not a retryable failure
        }
        catch (SqlException ex)
        {
            return Map(ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or System.IO.IOException)
        {
            return DatabaseQueryResult.Failure("db.query_failed", false);
        }
    }

    /// <summary>
    /// Reads one cell without ever holding more than <paramref name="budget"/> characters of it: text and binary are read in chunks and
    /// abandoned as soon as they are over (fits = false). The reader is sequential, so columns must be read in order.
    /// </summary>
    private static async Task<(object? Value, bool Fits)> ReadCellAsync(SqlDataReader reader, int i, int budget, CancellationToken ct)
    {
        if (await reader.IsDBNullAsync(i, ct))
        {
            return (null, true);
        }

        var type = reader.GetFieldType(i);
        if (type == typeof(string))
        {
            var text = new System.Text.StringBuilder();
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

    internal static DatabaseQueryResult Map(SqlException ex)
    {
        var (code, _) = SqlServerProbe.Describe(ex.Number, ex.Message);
        return code switch
        {
            "db.timeout" or "db.unreachable" => DatabaseQueryResult.Failure(code, true),
            "db.test_failed" => DatabaseQueryResult.Failure("db.query_failed", ex.Number is not (102 or 156 or 207 or 208 or 229 or 262 or 4060)),
            _ => DatabaseQueryResult.Failure(code, false),
        };
    }
}
