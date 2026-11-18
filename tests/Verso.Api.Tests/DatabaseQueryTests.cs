using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Verso.Application.Connections;
using Verso.Domain.Connections;
using Verso.Infrastructure.Connections;
using Verso.Infrastructure.Persistence;
using Verso.Infrastructure.Security;

namespace Verso.Api.Tests;

/// <summary>
/// The Query database step's service and SQL Server runner against a throwaway LocalDB database (VERSO_TEST_SQL_SERVER to override).
/// Synthetic table and logins only; the database and the logins are dropped at the end.
/// </summary>
public sealed class DatabaseQueryTests : IAsyncLifetime
{
    private static readonly string Server =
        Environment.GetEnvironmentVariable("VERSO_TEST_SQL_SERVER") is { Length: > 0 } s ? s : @"(localdb)\MSSQLLocalDB";

    private readonly TestDatabase _database = new("query");
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..10];
    private readonly string _password = "Vq!" + Guid.NewGuid().ToString("N");
    private readonly AesGcmSecretProtector _secrets = new(AesGcmSecretProtector.GenerateKey());
    private string WriterLogin => "verso_qw_" + _suffix;
    private string ReaderLogin => "verso_qr_" + _suffix;

    public async Task InitializeAsync()
    {
        await _database.MigrateAsync();
        await ExecAsync($@"
            CREATE TABLE dbo.Accounts (id int NOT NULL PRIMARY KEY, holder nvarchar(50) NOT NULL, note nvarchar(50) NULL);
            INSERT dbo.Accounts VALUES (1, N'Ada', NULL), (2, N'Bo', N'x'), (3, N'Cy', NULL);
            CREATE LOGIN [{WriterLogin}] WITH PASSWORD = '{_password}', CHECK_POLICY = OFF;
            CREATE LOGIN [{ReaderLogin}] WITH PASSWORD = '{_password}', CHECK_POLICY = OFF;
            CREATE USER [{WriterLogin}] FOR LOGIN [{WriterLogin}];
            CREATE USER [{ReaderLogin}] FOR LOGIN [{ReaderLogin}];
            ALTER ROLE db_datareader ADD MEMBER [{WriterLogin}];
            ALTER ROLE db_datawriter ADD MEMBER [{WriterLogin}];
            ALTER ROLE db_datareader ADD MEMBER [{ReaderLogin}];");
    }

    public async Task DisposeAsync()
    {
        await _database.DropAsync();
        await using var master = new SqlConnection(new SqlConnectionStringBuilder(_database.ConnectionString) { InitialCatalog = "master" }.ConnectionString);
        await master.OpenAsync();
        await using var command = master.CreateCommand();
        command.CommandText = $"DROP LOGIN [{WriterLogin}]; DROP LOGIN [{ReaderLogin}];";
        await command.ExecuteNonQueryAsync();
    }

    private async Task ExecAsync(string sql)
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> CountAsync()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dbo.Accounts";
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private VersoDbContext NewContext() => new(new DbContextOptionsBuilder<VersoDbContext>().UseSqlServer(_database.ConnectionString).Options);

    private async Task<string> AddConnectionAsync(string? login = null, bool active = true)
    {
        var key = "q" + Guid.NewGuid().ToString("N")[..12];
        var now = DateTimeOffset.UtcNow;
        var connection = DatabaseConnection.Create(key, DatabaseProviders.SqlServer, "Query " + key, Server, 0, _database.Name, login ?? WriterLogin, false, true, 15, "test", now);
        connection.SetPassword(_secrets.Protect(_password, DatabaseConnectionContexts.For(key)), "test", now);
        if (!active)
        {
            connection.Deactivate("test", now);
        }

        await using var db = NewContext();
        db.DatabaseConnections.Add(connection);
        await db.SaveChangesAsync();
        return key;
    }

    private async Task<DatabaseQueryResult> RunAsync(string key, string sql, Dictionary<string, object?>? parameters = null, int maxRows = 100, int timeoutSeconds = 10, DatabaseConnectionOptions? options = null)
    {
        await using var db = NewContext();
        var service = new DatabaseQueryService(db, _secrets, [new SqlServerQueryRunner()], options ?? new DatabaseConnectionOptions());
        return await service.RunAsync(new DatabaseQueryRequest(key, sql, parameters ?? [], maxRows, TimeSpan.FromSeconds(timeoutSeconds)), CancellationToken.None);
    }

    /// <summary>Bypasses the service and the guard: the runner's own defences.</summary>
    private async Task<DatabaseQueryResult> CallRunnerDirectlyAsync(string key, string sql, int timeoutSeconds = 10)
    {
        await using var db = NewContext();
        var connection = await db.DatabaseConnections.AsNoTracking().SingleAsync(c => c.Key == key);
        var settings = new DatabaseConnectionSettings(
            connection.Provider, connection.Server, connection.Port, connection.Database, connection.UserName,
            _secrets.Unprotect(connection.PasswordProtected!, DatabaseConnectionContexts.For(key))!, false, true, TimeSpan.FromSeconds(15));
        return await new SqlServerQueryRunner().RunAsync(settings, new DatabaseQueryRequest(key, sql, new Dictionary<string, object?>(), 10, TimeSpan.FromSeconds(timeoutSeconds)), CancellationToken.None);
    }

    [Fact]
    public async Task Returns_one_row_as_object_data()
    {
        var result = await RunAsync(await AddConnectionAsync(), "SELECT id, holder, note FROM dbo.Accounts WHERE id = @id", new() { ["id"] = 2 }, maxRows: 1);

        Assert.True(result.Ok);
        Assert.Equal(["id", "holder", "note"], result.Columns);
        var row = Assert.Single(result.Rows);
        Assert.Equal(2, row["id"]);
        Assert.Equal("Bo", row["holder"]);
        Assert.Equal("x", row["note"]);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Null_columns_come_back_as_null()
    {
        var result = await RunAsync(await AddConnectionAsync(), "SELECT note FROM dbo.Accounts WHERE id = @id", new() { ["id"] = 1 });

        Assert.Null(Assert.Single(result.Rows)["note"]);
    }

    [Fact]
    public async Task Caps_rows_and_marks_truncated()
    {
        var result = await RunAsync(await AddConnectionAsync(), "SELECT id FROM dbo.Accounts ORDER BY id", maxRows: 2);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Rows.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task Exactly_MaxRows_rows_is_not_truncated()
    {
        var result = await RunAsync(await AddConnectionAsync(), "SELECT id FROM dbo.Accounts", maxRows: 3);

        Assert.Equal(3, result.Rows.Count);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Zero_rows_is_success_with_no_rows()
    {
        var result = await RunAsync(await AddConnectionAsync(), "SELECT id FROM dbo.Accounts WHERE id = @id", new() { ["id"] = 99 });

        Assert.True(result.Ok);
        Assert.Empty(result.Rows);
        Assert.Equal(["id"], result.Columns);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Result_over_the_size_cap_is_cut_and_marked_truncated()
    {
        // 100 rows of ~10 KB each is about 1 MB of JSON: well over the 256 KB cap, well under MaxRows.
        var result = await RunAsync(await AddConnectionAsync(), "SELECT TOP 100 REPLICATE(CAST('a' AS varchar(max)), 10000) AS big FROM sys.all_objects", maxRows: 1000);

        Assert.True(result.Ok);
        Assert.True(result.Truncated);
        Assert.InRange(result.Rows.Count, 1, 40);
    }

    [Fact]
    public async Task Binds_hostile_text_as_data()
    {
        var result = await RunAsync(await AddConnectionAsync(), "SELECT id FROM dbo.Accounts WHERE holder = @x", new() { ["x"] = "x' OR 1=1 --" });

        Assert.True(result.Ok);
        Assert.Empty(result.Rows);
        Assert.Equal(3, await CountAsync());
    }

    [Fact]
    public async Task A_null_parameter_binds_as_database_null()
    {
        var result = await RunAsync(await AddConnectionAsync(), "SELECT CASE WHEN @n IS NULL THEN 1 ELSE 0 END AS is_null", new() { ["n"] = null });

        Assert.Equal(1, Assert.Single(result.Rows)["is_null"]);
    }

    [Fact]
    public async Task Cannot_write_with_a_login_that_has_no_write_permission()
    {
        var key = await AddConnectionAsync(ReaderLogin);
        var result = await CallRunnerDirectlyAsync(key, "INSERT dbo.Accounts VALUES (10, N'Zed', NULL)");

        Assert.False(result.Ok);
        Assert.Equal("db.query_failed", result.Code);
        Assert.False(result.Retryable);
        Assert.Equal(3, await CountAsync());
    }

    [Fact]
    public async Task Cannot_write_even_with_a_login_that_may_write_because_the_transaction_is_rolled_back()
    {
        // The guard is bypassed on purpose: this is the last defence. The login CAN insert (db_datawriter); the runner still keeps nothing.
        var key = await AddConnectionAsync(WriterLogin);

        var plain = await CallRunnerDirectlyAsync(key, "INSERT dbo.Accounts VALUES (10, N'Zed', NULL)");
        Assert.False(plain.Ok); // no result set: not a query
        Assert.Equal("db.query_failed", plain.Code);
        Assert.Equal(3, await CountAsync());

        // A write that does return a result set runs, but the rollback keeps nothing.
        var withResult = await CallRunnerDirectlyAsync(key, "INSERT dbo.Accounts OUTPUT inserted.id VALUES (11, N'Yan', NULL)");
        Assert.True(withResult.Ok);
        Assert.Equal(3, await CountAsync());
    }

    [Fact]
    public async Task Honours_timeout()
    {
        // WAITFOR cannot pass the guard; the runner is called directly to prove the command timeout.
        var key = await AddConnectionAsync();
        var result = await CallRunnerDirectlyAsync(key, "WAITFOR DELAY '00:00:05'", timeoutSeconds: 1);

        Assert.Equal("db.timeout", result.Code);
        Assert.True(result.Retryable);
    }

    [Fact]
    public async Task Rejects_inactive_connection()
    {
        var result = await RunAsync(await AddConnectionAsync(active: false), "SELECT 1 AS one");

        Assert.Equal("db.connection_unavailable", result.Code);
        Assert.False(result.Retryable);
    }

    [Fact]
    public async Task Rejects_unknown_connection()
    {
        var result = await RunAsync("nope", "SELECT 1 AS one");

        Assert.Equal("db.connection_unavailable", result.Code);
    }

    [Fact]
    public async Task Rejects_server_not_in_allowed_list()
    {
        var result = await RunAsync(await AddConnectionAsync(), "SELECT 1 AS one", options: new DatabaseConnectionOptions { AllowedServers = ["other.example.test"] });

        Assert.Equal("db.server_not_allowed", result.Code);
        Assert.False(result.Retryable);
    }

    [Fact]
    public async Task Rejects_second_statement()
    {
        var result = await RunAsync(await AddConnectionAsync(), "SELECT 1 AS one; DELETE dbo.Accounts");

        Assert.Equal("sql.multiple_statements", result.Code);
        Assert.False(result.Retryable);
        Assert.Equal(3, await CountAsync());
    }

    [Fact]
    public async Task Rejects_a_missing_parameter_value()
    {
        var result = await RunAsync(await AddConnectionAsync(), "SELECT id FROM dbo.Accounts WHERE id = @id");

        Assert.Equal("sql.bad_parameter", result.Code);
    }

    [Fact]
    public async Task A_failure_message_is_Verso_wording_and_never_names_the_parameter_value()
    {
        var result = await RunAsync(await AddConnectionAsync(), "SELECT nope FROM dbo.Accounts WHERE holder = @h", new() { ["h"] = "secret-value-123" });

        Assert.Equal("db.query_failed", result.Code);
        Assert.False(result.Retryable); // invalid column name (207)
        Assert.Equal(DatabaseQueryMessages.For("db.query_failed"), result.Message);
        Assert.DoesNotContain("secret-value-123", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("nope", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_master_key_gives_password_unreadable()
    {
        var key = await AddConnectionAsync();
        await using var db = NewContext();
        var service = new DatabaseQueryService(db, new AesGcmSecretProtector(AesGcmSecretProtector.GenerateKey()), [new SqlServerQueryRunner()], new DatabaseConnectionOptions());

        var result = await service.RunAsync(new DatabaseQueryRequest(key, "SELECT 1 AS one", new Dictionary<string, object?>(), 1, TimeSpan.FromSeconds(5)), CancellationToken.None);

        Assert.Equal("db.password_unreadable", result.Code);
    }

    [Fact]
    public async Task An_unregistered_provider_is_unavailable()
    {
        var key = await AddConnectionAsync();
        await using var db = NewContext();
        var service = new DatabaseQueryService(db, _secrets, [], new DatabaseConnectionOptions());

        var result = await service.RunAsync(new DatabaseQueryRequest(key, "SELECT 1 AS one", new Dictionary<string, object?>(), 1, TimeSpan.FromSeconds(5)), CancellationToken.None);

        Assert.Equal("db.provider_unavailable", result.Code);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var key = await AddConnectionAsync();
        await using var db = NewContext();
        var service = new DatabaseQueryService(db, _secrets, [new SqlServerQueryRunner()], new DatabaseConnectionOptions());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RunAsync(new DatabaseQueryRequest(key, "SELECT 1 AS one", new Dictionary<string, object?>(), 1, TimeSpan.FromSeconds(5)), cts.Token));
    }

    [Fact]
    public async Task One_value_over_the_cap_is_a_failure_not_an_empty_truncated_result()
    {
        var result = await RunAsync(await AddConnectionAsync(), "SELECT REPLICATE(CAST('a' AS varchar(max)), 2000000) AS big");

        Assert.Equal("db.result_too_large", result.Code);
        Assert.False(result.Retryable);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task A_huge_binary_or_xml_value_is_also_too_large()
    {
        var key = await AddConnectionAsync();

        Assert.Equal("db.result_too_large", (await RunAsync(key, "SELECT CAST(REPLICATE(CAST('a' AS varchar(max)), 2000000) AS varbinary(max)) AS big")).Code);
        Assert.Equal("db.result_too_large", (await RunAsync(key, "SELECT CAST('<r>' + REPLICATE(CAST('a' AS varchar(max)), 2000000) + '</r>' AS xml) AS big")).Code);
    }

    [Fact]
    public async Task A_first_row_that_fits_then_a_second_that_does_not_is_truncated_with_one_row()
    {
        var result = await RunAsync(await AddConnectionAsync(), "SELECT 1 AS n, 'a' AS v UNION ALL SELECT 2, REPLICATE(CAST('a' AS varchar(max)), 2000000)");

        Assert.True(result.Ok);
        Assert.True(result.Truncated);
        Assert.Single(result.Rows);
    }

    [Fact]
    public async Task Duplicate_column_names_are_refused()
    {
        var result = await RunAsync(await AddConnectionAsync(), "SELECT a.id, b.id FROM dbo.Accounts a CROSS JOIN dbo.Accounts b");

        Assert.Equal("db.bad_column", result.Code);
        Assert.False(result.Retryable);
    }

    [Fact]
    public async Task A_column_without_a_name_is_refused()
    {
        var result = await RunAsync(await AddConnectionAsync(), "SELECT COUNT(*) FROM dbo.Accounts");

        Assert.Equal("db.bad_column", result.Code);
    }

    [Fact]
    public async Task Limits_are_clamped_to_100_rows_and_60_seconds()
    {
        var key = await AddConnectionAsync();
        var many = await RunAsync(key, "SELECT TOP 150 object_id FROM sys.all_objects", maxRows: 1000);
        Assert.Equal(100, many.Rows.Count);
        Assert.True(many.Truncated);

        Assert.Single((await RunAsync(key, "SELECT TOP 5 object_id FROM sys.all_objects", maxRows: 0)).Rows);

        var runner = new CapturingRunner();
        await using var db = NewContext();
        var service = new DatabaseQueryService(db, _secrets, [runner], new DatabaseConnectionOptions());
        await service.RunAsync(new DatabaseQueryRequest(key, "SELECT 1 AS one", new Dictionary<string, object?>(), 5000, TimeSpan.FromHours(1)), CancellationToken.None);
        Assert.Equal(100, runner.Seen!.MaxRows);
        Assert.Equal(TimeSpan.FromSeconds(60), runner.Seen.Timeout);
        await service.RunAsync(new DatabaseQueryRequest(key, "SELECT 1 AS one", new Dictionary<string, object?>(), -3, TimeSpan.Zero), CancellationToken.None);
        Assert.Equal(1, runner.Seen.MaxRows);
        Assert.Equal(TimeSpan.FromSeconds(1), runner.Seen.Timeout);
    }

    private sealed class CapturingRunner : IProviderQueryRunner
    {
        public string Provider => DatabaseProviders.SqlServer;
        public DatabaseQueryRequest? Seen { get; private set; }

        public Task<DatabaseQueryResult> RunAsync(DatabaseConnectionSettings settings, DatabaseQueryRequest request, CancellationToken ct)
        {
            Seen = request;
            return Task.FromResult(DatabaseQueryResult.Success([], [], false, 0));
        }
    }

    /// <summary>Wraps a real runner and counts how often the service handed it a query (each call opens a connection).</summary>
    private sealed class CountingRunner(IProviderQueryRunner inner) : IProviderQueryRunner
    {
        private int _calls;
        public int Calls => _calls;
        public string Provider => inner.Provider;

        public Task<DatabaseQueryResult> RunAsync(DatabaseConnectionSettings settings, DatabaseQueryRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return inner.RunAsync(settings, request, ct);
        }
    }

    // Final review C1 / I1: texts that SQL Server reads as more than one statement although a naive tokenizer sees one SELECT.
    public static TheoryData<string> SqlServerAttacks => new()
    {
        "SELECT 1 AS one --'\nCOMMIT DELETE FROM dbo.Accounts --'",
        "SELECT 1 AS one COMMIT DELETE FROM dbo.Accounts",
        "SELECT 1 AS one /* /* */ '*/ COMMIT DELETE FROM dbo.Accounts --'",
        "SELECT 1DELETE FROM dbo.Accounts",
        "SELECT [a]] COMMIT DELETE FROM dbo.Accounts --] AS one",
        "SELECT 1 AS one -- x\r'\nCOMMIT DELETE FROM dbo.Accounts --'",
        "SELECT 1 AS one WAITFOR DELAY '00:00:05'",
        "SELECT 1 AS one KILL 1",
        "SELECT 1 AS one\u00A0COMMIT",
        "SELECT a@DELETE FROM dbo.Accounts",
    };

    [Theory]
    [MemberData(nameof(SqlServerAttacks))]
    public async Task The_service_refuses_an_attack_with_a_write_capable_login_and_never_connects(string sql)
    {
        var key = await AddConnectionAsync(WriterLogin); // this login CAN delete (db_datawriter)
        var runner = new CountingRunner(new SqlServerQueryRunner());
        await using var db = NewContext();
        var service = new DatabaseQueryService(db, _secrets, [runner], new DatabaseConnectionOptions());

        var result = await service.RunAsync(new DatabaseQueryRequest(key, sql, new Dictionary<string, object?>(), 10, TimeSpan.FromSeconds(10)), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.StartsWith("sql.", result.Code, StringComparison.Ordinal);
        Assert.False(result.Retryable);
        Assert.Equal(0, runner.Calls); // refused before any connection was opened
        Assert.Equal(3, await CountAsync());
    }

    [Fact]
    public async Task Without_the_guard_the_comment_attack_really_deletes_which_is_why_the_login_must_be_read_only()
    {
        // The runner alone (guard bypassed on purpose) with a login that may write: SQL Server reads "--'" as a comment, so the batch is
        // SELECT; COMMIT (ends the runner's transaction); DELETE (autocommitted). The rollback cannot undo it. This is the hole SqlGuard now closes,
        // and the reason ADR 0019 requires a read-only login: the guard and the rollback are defence in depth, the login is the protection.
        var key = await AddConnectionAsync(WriterLogin);

        await CallRunnerDirectlyAsync(key, "SELECT 1 AS one --'\nCOMMIT DELETE FROM dbo.Accounts --'");

        Assert.Equal(0, await CountAsync());
    }

    [Fact]
    public async Task The_same_attack_through_the_runner_with_a_read_only_login_changes_nothing()
    {
        var key = await AddConnectionAsync(ReaderLogin);

        await CallRunnerDirectlyAsync(key, "SELECT 1 AS one --'\nCOMMIT DELETE FROM dbo.Accounts --'");

        Assert.Equal(3, await CountAsync());
    }

    [Fact]
    public async Task Stops_reading_at_the_row_cap_instead_of_streaming_the_rest_of_a_large_result()
    {
        // Final review I2: closing a reader drains every remaining row unless the command is cancelled first. 60 million rows take far longer (about 20 s on LocalDB without the cancel)
        // than the bound below to stream; with the cancel the run returns as soon as the second row shows the result is truncated.
        var key = await AddConnectionAsync();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var result = await RunAsync(key,
            "SELECT TOP (60000000) a.object_id AS a, b.object_id AS b, c.object_id AS c FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c",
            maxRows: 1, timeoutSeconds: 60);

        Assert.True(result.Ok);
        Assert.Single(result.Rows);
        Assert.True(result.Truncated);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Fact]
    public void A_timeout_without_a_SqlException_is_a_retryable_db_timeout()
    {
        // Final review minor: it used to be a final db.query_failed on SQL Server only; the other runners already said db.timeout.
        var mapped = SqlServerQueryRunner.MapAny(new TimeoutException("socket"));
        Assert.Equal("db.timeout", mapped.Code);
        Assert.True(mapped.Retryable);
        Assert.Equal("db.query_failed", SqlServerQueryRunner.MapAny(new InvalidOperationException("x")).Code);
        Assert.False(SqlServerQueryRunner.MapAny(new InvalidOperationException("x")).Retryable);
    }

    [Fact]
    public async Task Cancelling_during_a_long_query_cancels_the_run_and_is_not_a_failure_result()
    {
        var key = await AddConnectionAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var started = System.Diagnostics.Stopwatch.StartNew();
        await using var db = NewContext();
        var connection = await db.DatabaseConnections.AsNoTracking().SingleAsync(c => c.Key == key);
        var settings = new DatabaseConnectionSettings(connection.Provider, connection.Server, 0, connection.Database, connection.UserName, _password, false, true, TimeSpan.FromSeconds(15));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SqlServerQueryRunner().RunAsync(settings, new DatabaseQueryRequest(key, "WAITFOR DELAY '00:00:10'", new Dictionary<string, object?>(), 1, TimeSpan.FromSeconds(30)), cts.Token));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(8));
    }
}
