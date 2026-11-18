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
                    return DatabaseQueryResult.Failure("db.query_failed", false); // not a query: nothing is kept (rolled back below)
                }

                var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
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
                    for (var i = 0; i < columns.Count; i++)
                    {
                        row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    }

                    size += JsonSerializer.Serialize(row).Length;
                    if (size > MaxResultChars)
                    {
                        truncated = true;
                        break;
                    }

                    rows.Add(row);
                }

                return DatabaseQueryResult.Success(columns, rows, truncated, clock.ElapsedMilliseconds);
            }
            finally
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None); // always: nothing a statement did is kept
                }
                catch (Exception ex) when (ex is InvalidOperationException or SqlException)
                {
                    // the connection is gone or the transaction already ended; the connection is disposed next, which discards it
                }
            }
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
