using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Verso.Application.Connections;
using Verso.Domain.Connections;
using Verso.Infrastructure.Connections;
using Verso.Infrastructure.Persistence;
using Verso.Infrastructure.Security;

namespace Verso.Api.Tests;

/// <summary>
/// The Write to database step's service and SQL Server runner against a throwaway LocalDB database (VERSO_TEST_SQL_SERVER to override).
/// Synthetic table and login only (the login may read and write, as a write connection's would); the database and the login are dropped at the end.
/// </summary>
public sealed class DatabaseWriteTests : IAsyncLifetime
{
    private static readonly string Server =
        Environment.GetEnvironmentVariable("VERSO_TEST_SQL_SERVER") is { Length: > 0 } s ? s : @"(localdb)\MSSQLLocalDB";

    private const string Seeded = "1|Ada|a|1;2|Bo|b|1;3|Cy|c|1;4|Di|d|2";
    private const string Marker = "marker-value-7f3a91";

    /// <summary>Inserts up to 50 million generated rows: far more than a 1 second timeout (or a 0.5 second cancel) lets finish.</summary>
    private const string SlowInsert =
        "INSERT INTO dbo.VersoWriteTest (id, holder, note, grp) SELECT TOP (50000000) 1000 + n, CONCAT(N'g', n), NULL, 9 " +
        "FROM (SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c) s";

    private readonly TestDatabase _database = new("write");
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..10];
    private readonly string _password = "Vw!" + Guid.NewGuid().ToString("N");
    private readonly AesGcmSecretProtector _secrets = new(AesGcmSecretProtector.GenerateKey());
    private string WriterLogin => "verso_ww_" + _suffix;

    public async Task InitializeAsync()
    {
        await _database.MigrateAsync();
        await ExecAsync($@"
            CREATE TABLE dbo.VersoWriteTest (
                id int NOT NULL PRIMARY KEY,
                holder nvarchar(200) NOT NULL CONSTRAINT UQ_VersoWriteTest_holder UNIQUE,
                note nvarchar(400) NULL,
                grp int NOT NULL);
            INSERT dbo.VersoWriteTest VALUES (1, N'Ada', N'a', 1), (2, N'Bo', N'b', 1), (3, N'Cy', N'c', 1), (4, N'Di', N'd', 2);
            CREATE LOGIN [{WriterLogin}] WITH PASSWORD = '{_password}', CHECK_POLICY = OFF;
            CREATE USER [{WriterLogin}] FOR LOGIN [{WriterLogin}];
            ALTER ROLE db_datareader ADD MEMBER [{WriterLogin}];
            ALTER ROLE db_datawriter ADD MEMBER [{WriterLogin}];");
    }

    public async Task DisposeAsync()
    {
        await _database.DropAsync();
        await using var master = new SqlConnection(new SqlConnectionStringBuilder(_database.ConnectionString) { InitialCatalog = "master" }.ConnectionString);
        await master.OpenAsync();
        await using var command = master.CreateCommand();
        command.CommandText = $"DROP LOGIN [{WriterLogin}];";
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

    /// <summary>Every row as "id|holder|note|grp", ordered by id and joined with ';'.</summary>
    private async Task<string> RowsAsync()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, holder, ISNULL(note, N'<null>'), grp FROM dbo.VersoWriteTest ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add($"{reader.GetInt32(0)}|{reader.GetString(1)}|{reader.GetString(2)}|{reader.GetInt32(3)}");
        }

        return string.Join(";", rows);
    }

    private VersoDbContext NewContext() => new(new DbContextOptionsBuilder<VersoDbContext>().UseSqlServer(_database.ConnectionString).Options);

    private async Task<string> AddConnectionAsync(bool allowWrites = true)
    {
        var key = "w" + Guid.NewGuid().ToString("N")[..12];
        var now = DateTimeOffset.UtcNow;
        var connection = DatabaseConnection.Create(key, DatabaseProviders.SqlServer, "Write " + key, Server, 0, _database.Name, WriterLogin, false, true, 15, "test", now);
        connection.SetPassword(_secrets.Protect(_password, DatabaseConnectionContexts.For(key)), "test", now);
        connection.SetAllowWrites(allowWrites, "test", now);

        await using var db = NewContext();
        db.DatabaseConnections.Add(connection);
        await db.SaveChangesAsync();
        return key;
    }

    private DatabaseWriteService NewService(VersoDbContext db, IProviderWriteRunner? runner = null, Verso.Application.Mail.ISecretProtector? secrets = null) =>
        new(new DatabaseConnectionResolver(db, secrets ?? _secrets, new DatabaseConnectionOptions()), [runner ?? new SqlServerWriteRunner()]);

    private async Task<DatabaseWriteResult> RunAsync(string key, string sql, Dictionary<string, object?>? parameters = null, int maxRows = 1, int timeoutSeconds = 10)
    {
        await using var db = NewContext();
        return await NewService(db).RunAsync(new DatabaseWriteRequest(key, sql, parameters ?? [], maxRows, TimeSpan.FromSeconds(timeoutSeconds)), CancellationToken.None);
    }

    private DatabaseConnectionSettings Settings() =>
        new(DatabaseProviders.SqlServer, Server, 0, _database.Name, WriterLogin, _password, false, true, TimeSpan.FromSeconds(15));

    [Fact]
    public async Task Insert_commits_and_reports_rows_affected()
    {
        var result = await RunAsync(await AddConnectionAsync(),
            "INSERT INTO dbo.VersoWriteTest (id, holder, note, grp) VALUES (@id, @holder, @note, @grp)",
            new() { ["id"] = 5, ["holder"] = "Ed", ["note"] = null, ["grp"] = 3 });

        Assert.True(result.Ok, result.Code);
        Assert.Equal(1, result.RowsAffected);
        Assert.False(result.OutcomeUnknown);
        Assert.Null(result.Message);
        Assert.True(result.DurationMs >= 0);
        Assert.Equal(Seeded + ";5|Ed|<null>|3", await RowsAsync());
    }

    [Fact]
    public async Task Update_with_parameters_changes_only_the_matching_row()
    {
        var result = await RunAsync(await AddConnectionAsync(), "UPDATE dbo.VersoWriteTest SET note = @note WHERE id = @id", new() { ["note"] = "changed", ["@id"] = 2 });

        Assert.True(result.Ok, result.Code);
        Assert.Equal(1, result.RowsAffected);
        Assert.Equal("1|Ada|a|1;2|Bo|changed|1;3|Cy|c|1;4|Di|d|2", await RowsAsync());
    }

    [Fact]
    public async Task Delete_with_a_where_removes_the_matching_row()
    {
        var result = await RunAsync(await AddConnectionAsync(), "DELETE FROM dbo.VersoWriteTest WHERE id = @id", new() { ["id"] = 4 });

        Assert.True(result.Ok, result.Code);
        Assert.Equal(1, result.RowsAffected);
        Assert.Equal("1|Ada|a|1;2|Bo|b|1;3|Cy|c|1", await RowsAsync());
    }

    [Fact]
    public async Task Hostile_text_in_a_parameter_is_data()
    {
        const string hostile = "x'; DROP TABLE dbo.VersoWriteTest; --";
        var result = await RunAsync(await AddConnectionAsync(),
            "INSERT INTO dbo.VersoWriteTest (id, holder, note, grp) VALUES (@id, @holder, @holder, 3)", new() { ["id"] = 5, ["holder"] = hostile });

        Assert.True(result.Ok, result.Code);
        Assert.Equal(Seeded + $";5|{hostile}|{hostile}|3", await RowsAsync()); // stored verbatim, the table is still there
    }

    [Fact]
    public async Task Over_the_cap_rolls_back_and_changes_nothing()
    {
        var result = await RunAsync(await AddConnectionAsync(), "UPDATE dbo.VersoWriteTest SET note = @note WHERE grp = @grp", new() { ["note"] = Marker, ["grp"] = 1 }, maxRows: 2);

        Assert.Equal("db.too_many_rows_affected", result.Code);
        Assert.False(result.OutcomeUnknown);
        Assert.Equal(DatabaseQueryMessages.For("db.too_many_rows_affected"), result.Message);
        Assert.Equal(0, result.RowsAffected);
        Assert.Equal(Seeded, await RowsAsync());
    }

    [Fact]
    public async Task Exactly_the_cap_commits()
    {
        var result = await RunAsync(await AddConnectionAsync(), "UPDATE dbo.VersoWriteTest SET note = N'z' WHERE grp = @grp", new() { ["grp"] = 1 }, maxRows: 3);

        Assert.True(result.Ok, result.Code);
        Assert.Equal(3, result.RowsAffected);
        Assert.Equal("1|Ada|z|1;2|Bo|z|1;3|Cy|z|1;4|Di|d|2", await RowsAsync());
    }

    [Fact]
    public async Task Constraint_violation_is_a_known_failure()
    {
        var key = await AddConnectionAsync();

        var unique = await RunAsync(key, "UPDATE dbo.VersoWriteTest SET holder = @holder WHERE id = @id", new() { ["holder"] = "Ada", ["id"] = 2 });
        Assert.Equal("db.query_failed", unique.Code);
        Assert.False(unique.OutcomeUnknown);
        Assert.Equal(DatabaseQueryMessages.For("db.query_failed"), unique.Message);

        var notNull = await RunAsync(key, "INSERT INTO dbo.VersoWriteTest (id, holder, grp) VALUES (@id, @holder, 1)", new() { ["id"] = 9, ["holder"] = null });
        Assert.Equal("db.query_failed", notNull.Code);
        Assert.False(notNull.OutcomeUnknown);

        Assert.Equal(Seeded, await RowsAsync());
    }

    [Fact]
    public async Task Timeout_after_send_is_outcome_unknown()
    {
        // The guard refuses WAITFOR, so the runner is called directly with a slow INSERT ... SELECT and a 1 second timeout.
        var result = await new SqlServerWriteRunner().RunAsync(
            Settings(), new DatabaseWriteRequest("k", SlowInsert, new Dictionary<string, object?>(), 1000, TimeSpan.FromSeconds(1)), CancellationToken.None);

        Assert.Equal("db.timeout", result.Code);
        Assert.True(result.OutcomeUnknown);
        Assert.Equal(DatabaseQueryMessages.OutcomeUnknown, result.Message);
        Assert.Equal(Seeded, await RowsAsync()); // the rollback followed: nothing was committed
    }

    [Fact]
    public async Task Writes_not_allowed_never_connects_or_decrypts()
    {
        var key = await AddConnectionAsync(allowWrites: false);
        var wrongKey = new AesGcmSecretProtector(AesGcmSecretProtector.GenerateKey()); // decrypting with it would give db.password_unreadable
        var runner = new CountingRunner(new SqlServerWriteRunner());
        await using var db = NewContext();

        var result = await NewService(db, runner, wrongKey).RunAsync(
            new DatabaseWriteRequest(key, "DELETE FROM dbo.VersoWriteTest WHERE id = @id", new Dictionary<string, object?> { ["id"] = 1 }, 1, TimeSpan.FromSeconds(5)), CancellationToken.None);

        Assert.Equal("db.writes_not_allowed", result.Code);
        Assert.False(result.OutcomeUnknown);
        Assert.Equal("Writes are switched off for this connection. An administrator can switch them on in Configuration, Connections.", result.Message);
        Assert.Equal(0, runner.Calls);
        Assert.Equal(Seeded, await RowsAsync());

        // The same connection and key without requireWrites reach the decrypt step and fail there: so the refusal above came before it.
        var (settings, code) = await new DatabaseConnectionResolver(db, wrongKey, new DatabaseConnectionOptions()).ResolveAsync(key, requireWrites: false, CancellationToken.None);
        Assert.Null(settings);
        Assert.Equal("db.password_unreadable", code);
    }

    public static TheoryData<string> RefusedTexts => new()
    {
        "UPDATE dbo.VersoWriteTest SET note = N'x' WHERE id = 1 DELETE FROM dbo.VersoWriteTest WHERE id = 2",
        "INSERT INTO dbo.VersoWriteTest (id, holder, grp) VALUES (9, N'Zed', 1) COMMIT",
        "UPDATE dbo.VersoWriteTest SET note = N'x' WHERE id = 1 SELECT 1",
        "UPDATE dbo.VersoWriteTest SET note = (SELECT 1 WHERE 1=1)",
        "DELETE FROM dbo.VersoWriteTest",
        "SELECT 1 AS one",
        "INSERT INTO dbo.VersoWriteTest (id, holder, grp) VALUES (9, N'Zed', 1); DELETE FROM dbo.VersoWriteTest WHERE id = 1",
    };

    [Theory]
    [MemberData(nameof(RefusedTexts))]
    public async Task Guard_refusal_never_connects(string sql)
    {
        var key = await AddConnectionAsync();
        var runner = new CountingRunner(new SqlServerWriteRunner());
        await using var db = NewContext();

        var result = await NewService(db, runner).RunAsync(new DatabaseWriteRequest(key, sql, new Dictionary<string, object?>(), 10, TimeSpan.FromSeconds(5)), CancellationToken.None);

        Assert.StartsWith("sql.", result.Code, StringComparison.Ordinal);
        Assert.False(result.OutcomeUnknown);
        Assert.Equal(0, runner.Calls);
        Assert.Equal(Seeded, await RowsAsync());
    }

    [Fact]
    public async Task A_missing_parameter_value_never_connects()
    {
        var key = await AddConnectionAsync();
        var runner = new CountingRunner(new SqlServerWriteRunner());
        await using var db = NewContext();

        var result = await NewService(db, runner).RunAsync(
            new DatabaseWriteRequest(key, "DELETE FROM dbo.VersoWriteTest WHERE id = @id", new Dictionary<string, object?> { ["other"] = 1 }, 1, TimeSpan.FromSeconds(5)), CancellationToken.None);

        Assert.Equal("sql.bad_parameter", result.Code);
        Assert.Equal(0, runner.Calls);
        Assert.Equal(Seeded, await RowsAsync());
    }

    [Fact]
    public async Task Switched_on_connection_runs()
    {
        var key = await AddConnectionAsync(allowWrites: false);
        const string sql = "UPDATE dbo.VersoWriteTest SET note = @note WHERE id = @id";
        Assert.Equal("db.writes_not_allowed", (await RunAsync(key, sql, new() { ["note"] = "on", ["id"] = 1 })).Code);

        await using (var db = NewContext())
        {
            var connection = await db.DatabaseConnections.SingleAsync(c => c.Key == key);
            connection.SetAllowWrites(true, "test", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        var result = await RunAsync(key, sql, new() { ["note"] = "on", ["id"] = 1 });
        Assert.True(result.Ok, result.Code);
        Assert.Equal(1, result.RowsAffected);
        Assert.Equal("1|Ada|on|1;2|Bo|b|1;3|Cy|c|1;4|Di|d|2", await RowsAsync());
    }

    [Fact]
    public async Task Connection_checks_are_shared_with_the_query_step()
    {
        var key = await AddConnectionAsync();
        await using var db = NewContext();
        var request = new DatabaseWriteRequest("nope", "DELETE FROM dbo.VersoWriteTest WHERE id = 1", new Dictionary<string, object?>(), 1, TimeSpan.FromSeconds(5));

        Assert.Equal("db.connection_unavailable", (await NewService(db).RunAsync(request, CancellationToken.None)).Code);

        var restricted = new DatabaseWriteService(new DatabaseConnectionResolver(db, _secrets, new DatabaseConnectionOptions { AllowedServers = ["other.example.test"] }), [new SqlServerWriteRunner()]);
        Assert.Equal("db.server_not_allowed", (await restricted.RunAsync(request with { ConnectionKey = key }, CancellationToken.None)).Code);

        var noRunner = new DatabaseWriteService(new DatabaseConnectionResolver(db, _secrets, new DatabaseConnectionOptions()), []);
        Assert.Equal("db.provider_unavailable", (await noRunner.RunAsync(request with { ConnectionKey = key }, CancellationToken.None)).Code);
        Assert.Equal(Seeded, await RowsAsync());
    }

    [Fact]
    public async Task Limits_are_clamped()
    {
        var key = await AddConnectionAsync();
        var runner = new CapturingRunner();
        await using var db = NewContext();
        var service = NewService(db, runner);
        const string sql = "DELETE FROM dbo.VersoWriteTest WHERE id = 1";

        await service.RunAsync(new DatabaseWriteRequest(key, sql, new Dictionary<string, object?>(), 5000, TimeSpan.FromHours(1)), CancellationToken.None);
        Assert.Equal(1000, runner.Seen!.MaxRows);
        Assert.Equal(TimeSpan.FromSeconds(60), runner.Seen.Timeout);

        await service.RunAsync(new DatabaseWriteRequest(key, sql, new Dictionary<string, object?>(), -3, TimeSpan.Zero), CancellationToken.None);
        Assert.Equal(1, runner.Seen.MaxRows);
        Assert.Equal(TimeSpan.FromSeconds(1), runner.Seen.Timeout);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var key = await AddConnectionAsync();
        await using (var db = NewContext())
        {
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewService(db).RunAsync(
                new DatabaseWriteRequest(key, "DELETE FROM dbo.VersoWriteTest WHERE id = 1", new Dictionary<string, object?>(), 1, TimeSpan.FromSeconds(5)), cancelled.Token));
        }

        // A cancel while the statement runs: a cancellation (the engine's shutdown handling decides), after the rollback. The cancel is sent only once the server
        // shows the INSERT running for the "Verso write" session, so it really lands during the statement (not while connecting).
        using var cts = new CancellationTokenSource();
        var run = new SqlServerWriteRunner().RunAsync(
            Settings(), new DatabaseWriteRequest(key, SlowInsert, new Dictionary<string, object?>(), 1000, TimeSpan.FromSeconds(30)), cts.Token);
        Assert.True(await WaitForRunningWriteAsync(TimeSpan.FromSeconds(15)), "the INSERT never showed as running");
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(Seeded, await RowsAsync());
    }

    /// <summary>Polls sys.dm_exec_requests (the test's own Windows login sees every session on LocalDB) until an INSERT of a "Verso write" session runs.</summary>
    private async Task<bool> WaitForRunningWriteAsync(TimeSpan limit)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id
            WHERE s.program_name = N'Verso write' AND r.command LIKE N'INSERT%'
            """;
        while (clock.Elapsed < limit)
        {
            if ((int)(await command.ExecuteScalarAsync())! > 0)
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }

    [Fact]
    public async Task A_runner_that_throws_is_an_unknown_outcome_without_its_text()
    {
        var key = await AddConnectionAsync();
        await using var db = NewContext();

        var result = await NewService(db, new ThrowingRunner(new InvalidOperationException(Marker))).RunAsync(
            new DatabaseWriteRequest(key, "DELETE FROM dbo.VersoWriteTest WHERE id = 1", new Dictionary<string, object?>(), 1, TimeSpan.FromSeconds(5)), CancellationToken.None);

        Assert.Equal("db.query_failed", result.Code);
        Assert.True(result.OutcomeUnknown);
        Assert.DoesNotContain(Marker, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_result_carries_a_parameter_value_or_the_SQL()
    {
        var key = await AddConnectionAsync();
        const string unique = "UPDATE dbo.VersoWriteTest SET holder = @holder, note = @note WHERE id = @id";
        const string tooMany = "UPDATE dbo.VersoWriteTest SET note = @note WHERE grp = @grp";
        var results = new List<DatabaseWriteResult>
        {
            await RunAsync(key, unique, new() { ["holder"] = "Ada", ["note"] = Marker, ["id"] = 2 }),
            await RunAsync(key, tooMany, new() { ["note"] = Marker, ["grp"] = 1 }),
            await RunAsync(key, "UPDATE dbo.VersoWriteTest SET nope = @note WHERE id = @id", new() { ["note"] = Marker, ["id"] = 1 }),
            await RunAsync(key, "UPDATE dbo.VersoWriteTest SET note = @note WHERE id = @id", new() { ["note"] = Marker, ["id"] = 1 }),
        };

        Assert.Equal(["db.query_failed", "db.too_many_rows_affected", "db.query_failed", null], results.Select(r => r.Code));
        foreach (var result in results)
        {
            var text = result.ToString();
            Assert.DoesNotContain(Marker, text, StringComparison.Ordinal);
            Assert.DoesNotContain("VersoWriteTest", text, StringComparison.Ordinal);
            Assert.DoesNotContain("nope", text, StringComparison.Ordinal);
            Assert.DoesNotContain("UQ_", text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(2627, true, "db.query_failed", false)]  // unique constraint
    [InlineData(2601, true, "db.query_failed", false)]  // unique index
    [InlineData(547, true, "db.query_failed", false)]   // foreign key / check
    [InlineData(515, true, "db.query_failed", false)]   // NULL into NOT NULL
    [InlineData(8152, true, "db.query_failed", false)]  // truncation
    [InlineData(2628, true, "db.query_failed", false)]  // truncation (SQL Server 2019+ wording)
    [InlineData(208, true, "db.query_failed", false)]   // invalid object
    [InlineData(229, true, "db.query_failed", false)]   // permission denied
    [InlineData(1205, true, "db.query_failed", false)]  // deadlock victim: the server rolled the transaction back
    [InlineData(-2, true, "db.timeout", true)]
    [InlineData(-1, true, "db.unreachable", true)]
    [InlineData(10054, true, "db.query_failed", true)] // connection reset by peer
    [InlineData(64, true, "db.unreachable", true)]
    [InlineData(50000, true, "db.query_failed", true)]  // anything else after sending
    [InlineData(18456, false, "db.login_failed", false)] // before sending: known
    [InlineData(53, false, "db.unreachable", false)]
    [InlineData(-2, false, "db.timeout", false)]
    [InlineData(50000, false, "db.query_failed", false)]
    public void SqlServer_errors_map_to_known_or_unknown(int number, bool sent, string code, bool unknown)
    {
        var result = SqlServerWriteRunner.Map(number, "server text " + Marker, sent);

        Assert.Equal(code, result.Code);
        Assert.Equal(unknown, result.OutcomeUnknown);
        Assert.Equal(unknown ? DatabaseQueryMessages.OutcomeUnknown : DatabaseQueryMessages.For(code), result.Message);
        Assert.DoesNotContain(Marker, result.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlServer_non_SqlException_failures()
    {
        Assert.Equal(("db.timeout", true), Pick(SqlServerWriteRunner.MapAny(new TimeoutException(Marker), true)));
        Assert.Equal(("db.timeout", false), Pick(SqlServerWriteRunner.MapAny(new TimeoutException(Marker), false)));
        Assert.Equal(("db.query_failed", true), Pick(SqlServerWriteRunner.MapAny(new InvalidOperationException(Marker), true)));
        Assert.Equal(("db.query_failed", false), Pick(SqlServerWriteRunner.MapAny(new InvalidOperationException(Marker), false)));

        static (string?, bool) Pick(DatabaseWriteResult r) => (r.Code, r.OutcomeUnknown);
    }

    [Fact]
    public void SqlServer_write_connection_is_not_read_only_intent()
    {
        var builder = new SqlConnectionStringBuilder(SqlServerWriteRunner.ConnectionString(Settings()));

        Assert.Equal(ApplicationIntent.ReadWrite, builder.ApplicationIntent);
        Assert.Equal("Verso write", builder.ApplicationName);
        Assert.False(builder.Pooling);
    }

    private sealed class CapturingRunner : IProviderWriteRunner
    {
        public string Provider => DatabaseProviders.SqlServer;
        public DatabaseWriteRequest? Seen { get; private set; }

        public Task<DatabaseWriteResult> RunAsync(DatabaseConnectionSettings settings, DatabaseWriteRequest request, CancellationToken ct)
        {
            Seen = request;
            return Task.FromResult(DatabaseWriteResult.Success(0, 0));
        }
    }

    private sealed class ThrowingRunner(Exception thrown) : IProviderWriteRunner
    {
        public string Provider => DatabaseProviders.SqlServer;
        public Task<DatabaseWriteResult> RunAsync(DatabaseConnectionSettings settings, DatabaseWriteRequest request, CancellationToken ct) => throw thrown;
    }

    /// <summary>Wraps a real runner and counts how often the service handed it a write (each call opens a connection).</summary>
    private sealed class CountingRunner(IProviderWriteRunner inner) : IProviderWriteRunner
    {
        private int _calls;
        public int Calls => _calls;
        public string Provider => inner.Provider;

        public Task<DatabaseWriteResult> RunAsync(DatabaseConnectionSettings settings, DatabaseWriteRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return inner.RunAsync(settings, request, ct);
        }
    }
}

/// <summary>
/// The provider-neutral write executor (<see cref="DbWriteExecution"/>) over a fake connection: both transaction modes (a driver transaction as SQL Server and Oracle
/// use, raw BEGIN / COMMIT / ROLLBACK statements as PostgreSQL and MySQL will), the row cap, and what a failure at each moment means. No server is contacted.
/// </summary>
public sealed class DatabaseWriteExecutorTests
{
    private const string Sql = "UPDATE t SET a = @a WHERE id = @id";

    private static Task<DatabaseWriteResult> Run(FakeConnection connection, bool raw, int maxRows = 1, List<bool>? sentSeen = null, CancellationToken ct = default) =>
        DbWriteExecution.RunAsync(
            connection, raw ? "BEGIN" : null, raw, Sql,
            new DatabaseWriteRequest("k", Sql, new Dictionary<string, object?> { ["a"] = "v", ["@id"] = null }, maxRows, TimeSpan.FromSeconds(7)),
            "@",
            (ex, sent) =>
            {
                sentSeen?.Add(sent);
                return DatabaseWriteResult.Failure(ex is DbException ? "db.mapped" : "db.other", sent && ex is not KnownServerError);
            },
            ct);

    [Fact]
    public async Task Driver_transaction_mode_binds_runs_and_commits()
    {
        var connection = new FakeConnection();

        var result = await Run(connection, raw: false);

        Assert.True(result.Ok);
        Assert.Equal(1, result.RowsAffected);
        Assert.Equal([Sql], connection.Executed);
        Assert.Equal(1, connection.TransactionCommits);
        Assert.Equal(0, connection.TransactionRollbacks);
        Assert.True(connection.StatementHadTransaction);
        Assert.Equal(7, connection.StatementTimeout);
        Assert.Equal(["@a=v", "@id=DBNull"], connection.Parameters);
    }

    [Fact]
    public async Task Raw_statement_mode_begins_runs_and_commits_with_statements()
    {
        var connection = new FakeConnection();

        var result = await Run(connection, raw: true);

        Assert.True(result.Ok);
        Assert.Equal(["BEGIN", Sql, "COMMIT"], connection.Executed);
        Assert.Equal(0, connection.TransactionsBegun);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Over_the_cap_rolls_back_and_never_commits(bool raw)
    {
        var connection = new FakeConnection { RowsAffected = 3 };

        var result = await Run(connection, raw, maxRows: 2);

        Assert.Equal("db.too_many_rows_affected", result.Code);
        Assert.False(result.OutcomeUnknown);
        Assert.Equal(0, connection.TransactionCommits);
        Assert.DoesNotContain("COMMIT", connection.Executed);
        if (raw)
        {
            Assert.Equal(["BEGIN", Sql, "ROLLBACK"], connection.Executed);
        }
        else
        {
            Assert.Equal(1, connection.TransactionRollbacks);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unknown_row_count_rolls_back(bool raw)
    {
        var connection = new FakeConnection { RowsAffected = -1 }; // e.g. SET NOCOUNT ON on the server: the cap can't be checked

        var result = await Run(connection, raw);

        Assert.Equal("db.rows_affected_unknown", result.Code);
        Assert.False(result.OutcomeUnknown);
        Assert.Equal("The server did not report how many rows changed (for example because of SET NOCOUNT), so nothing was kept.", result.Message);
        Assert.Equal(0, connection.TransactionCommits);
        Assert.DoesNotContain("COMMIT", connection.Executed);
    }

    [Fact]
    public async Task Raw_mode_without_a_begin_statement_is_refused_before_opening()
    {
        // Without BEGIN the statement would autocommit and the cap's ROLLBACK would undo nothing.
        var connection = new FakeConnection();

        await Assert.ThrowsAsync<ArgumentException>(() => DbWriteExecution.RunAsync(
            connection, null, true, Sql, new DatabaseWriteRequest("k", Sql, new Dictionary<string, object?>(), 1, TimeSpan.FromSeconds(5)), "@",
            (ex, sent) => DatabaseWriteResult.Failure("db.mapped", sent), CancellationToken.None));

        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Empty(connection.Executed);
    }

    public static TheoryData<bool, Exception, bool> FailuresWhileClosing => new()
    {
        { false, new IOException("socket"), false },
        { false, new InvalidOperationException("closed"), false },
        { false, new FakeDbException("gone"), true },
        { false, new NotSupportedException("odd"), true },
        { false, new NotSupportedException("odd"), false }, // not one of the tolerated failures: still success, the commit happened
        { true, new TimeoutException("socket"), true },     // raw mode has no driver transaction: only the connection is closed
        { true, new NotSupportedException("odd"), true },
    };

    [Theory]
    [MemberData(nameof(FailuresWhileClosing))]
    public async Task A_committed_write_is_success_whatever_closing_throws(bool raw, Exception failure, bool onConnection)
    {
        var connection = new FakeConnection
        {
            TransactionDisposeFailure = onConnection ? null : failure,
            ConnectionDisposeFailure = onConnection ? failure : null,
        };

        var result = await Run(connection, raw);

        Assert.True(result.Ok, result.Code);
        Assert.Equal(1, result.RowsAffected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cancel_after_the_commit_still_reports_success(bool raw)
    {
        using var cts = new CancellationTokenSource();
        var connection = new FakeConnection { OnCommit = cts.Cancel, ConnectionDisposeFailure = new InvalidOperationException("closed") };

        var result = await Run(connection, raw, ct: cts.Token);

        Assert.True(cts.IsCancellationRequested);
        Assert.True(result.Ok, result.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failing_dispose_does_not_turn_a_known_failure_into_an_unknown_one(bool raw)
    {
        foreach (Exception failure in new Exception[] { new IOException("socket"), new FakeDbException("gone"), new InvalidOperationException("closed"), new TimeoutException("x") })
        {
            var overCap = await Run(new FakeConnection { RowsAffected = 5, TransactionDisposeFailure = failure, ConnectionDisposeFailure = failure }, raw);
            Assert.Equal("db.too_many_rows_affected", overCap.Code);
            Assert.False(overCap.OutcomeUnknown);

            var known = await Run(new FakeConnection { StatementFailure = new KnownServerError("x"), TransactionDisposeFailure = failure, ConnectionDisposeFailure = failure }, raw);
            Assert.Equal("db.mapped", known.Code);
            Assert.False(known.OutcomeUnknown);
        }
    }

    public static TheoryData<bool, Exception> CommitFailures => new()
    {
        { false, new FakeDbException("secret") },
        { false, new KnownServerError("secret") },
        { false, new InvalidOperationException("secret") },
        { false, new IOException("secret") },
        { true, new FakeDbException("secret") },
        { true, new KnownServerError("secret") },
        { true, new TimeoutException("secret") },
    };

    [Theory]
    [MemberData(nameof(CommitFailures))]
    public async Task Commit_failure_is_outcome_unknown(bool raw, Exception failure)
    {
        var connection = new FakeConnection { CommitFailure = failure };

        var result = await Run(connection, raw);

        Assert.False(result.Ok);
        Assert.True(result.OutcomeUnknown); // even a "known" server error: the commit's outcome can't be known
        Assert.Equal(DatabaseQueryMessages.OutcomeUnknown, result.Message);
        Assert.DoesNotContain("secret", result.ToString(), StringComparison.Ordinal);
    }

    public static TheoryData<bool, Exception> RollbackFailures => new()
    {
        { false, new TimeoutException("socket") },
        { false, new IOException("socket") },
        { false, new FakeDbException("gone") },
        { false, new InvalidOperationException("closed") },
        { true, new TimeoutException("socket") },
        { true, new IOException("socket") },
        { true, new FakeDbException("gone") },
        { true, new InvalidOperationException("closed") },
    };

    [Theory]
    [MemberData(nameof(RollbackFailures))]
    public async Task Rollback_failure_does_not_replace_the_result(bool raw, Exception failure)
    {
        var overCap = await Run(new FakeConnection { RowsAffected = 5, RollbackFailure = failure }, raw);
        Assert.Equal("db.too_many_rows_affected", overCap.Code);
        Assert.False(overCap.OutcomeUnknown);

        var known = await Run(new FakeConnection { StatementFailure = new KnownServerError("x"), RollbackFailure = failure }, raw);
        Assert.Equal("db.mapped", known.Code);
        Assert.False(known.OutcomeUnknown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Map_is_told_whether_the_statement_was_sent(bool raw)
    {
        var seen = new List<bool>();

        var beforeSend = await Run(new FakeConnection { OpenFailure = new FakeDbException("login") }, raw, sentSeen: seen);
        Assert.Equal("db.mapped", beforeSend.Code);
        Assert.False(beforeSend.OutcomeUnknown);

        var afterSend = await Run(new FakeConnection { StatementFailure = new FakeDbException("lost") }, raw, sentSeen: seen);
        Assert.True(afterSend.OutcomeUnknown);

        Assert.Equal([false, true], seen);
    }

    [Fact]
    public async Task A_failing_begin_statement_is_a_known_failure()
    {
        var seen = new List<bool>();
        var result = await Run(new FakeConnection { BeginFailure = new FakeDbException("x") }, raw: true, sentSeen: seen);

        Assert.Equal("db.mapped", result.Code);
        Assert.False(result.OutcomeUnknown);
        Assert.Equal([false], seen);
        Assert.DoesNotContain(Sql, result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unexpected_exception_after_sending_is_unknown_and_before_is_known(bool raw)
    {
        var after = await Run(new FakeConnection { StatementFailure = new InvalidCastException("secret 42") }, raw);
        Assert.Equal("db.query_failed", after.Code);
        Assert.True(after.OutcomeUnknown);
        Assert.DoesNotContain("secret", after.ToString(), StringComparison.Ordinal);

        var before = await Run(new FakeConnection { OpenFailure = new InvalidCastException("secret 42") }, raw);
        Assert.Equal("db.query_failed", before.Code);
        Assert.False(before.OutcomeUnknown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_after_sending_throws_after_the_rollback_attempt(bool raw)
    {
        foreach (Exception thrown in new Exception[] { new FakeDbException("cancelled"), new InvalidOperationException("closed"), new TimeoutException("x") })
        {
            using var cts = new CancellationTokenSource();
            var connection = new FakeConnection { StatementFailure = thrown, OnStatement = cts.Cancel };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(connection, raw, ct: cts.Token));

            Assert.Equal(0, connection.TransactionCommits);
            if (raw)
            {
                Assert.Equal(["BEGIN", Sql, "ROLLBACK"], connection.Executed);
            }
            else
            {
                Assert.Equal(1, connection.TransactionRollbacks);
            }
        }
    }

    [Fact]
    public async Task An_OperationCanceledException_from_the_driver_propagates()
    {
        using var cts = new CancellationTokenSource();
        var connection = new FakeConnection { StatementFailure = new OperationCanceledException(cts.Token) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(connection, raw: false));
        Assert.Equal(1, connection.TransactionRollbacks);
    }

    /// <summary>A driver exception the test's map treats as a server-reported (known) error.</summary>
    private sealed class KnownServerError(string message) : DbException(message);

    private sealed class FakeDbException(string message) : DbException(message);

    private sealed class FakeConnection : DbConnection
    {
        private ConnectionState _state = ConnectionState.Closed;
        public int RowsAffected { get; init; } = 1;
        public Exception? OpenFailure { get; init; }
        public Exception? BeginFailure { get; init; }
        public Exception? StatementFailure { get; init; }
        public Exception? CommitFailure { get; init; }
        public Exception? RollbackFailure { get; init; }
        public Action? OnStatement { get; init; }
        public Action? OnCommit { get; init; }
        public Exception? TransactionDisposeFailure { get; init; }
        public Exception? ConnectionDisposeFailure { get; init; }

        public List<string> Executed { get; } = [];
        public List<string> Parameters { get; } = [];
        public int TransactionsBegun { get; set; }
        public int TransactionCommits { get; set; }
        public int TransactionRollbacks { get; set; }
        public int RawRollbacks { get; set; }
        public bool StatementHadTransaction { get; set; }
        public int StatementTimeout { get; set; }

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get => ""; set { } }
        public override string Database => "";
        public override string DataSource => "";
        public override string ServerVersion => "";
        public override ConnectionState State => _state;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() => _state = ConnectionState.Closed;

        public override void Open()
        {
            if (OpenFailure is not null)
            {
                throw OpenFailure;
            }

            _state = ConnectionState.Open;
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            TransactionsBegun++;
            return new FakeTransaction(this);
        }

        protected override DbCommand CreateDbCommand() => new FakeCommand(this);

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && ConnectionDisposeFailure is not null)
            {
                throw ConnectionDisposeFailure;
            }
        }
    }

    private sealed class FakeTransaction(FakeConnection connection) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => connection;

        public override void Commit()
        {
            if (connection.CommitFailure is not null)
            {
                throw connection.CommitFailure;
            }

            connection.TransactionCommits++;
            connection.OnCommit?.Invoke();
        }

        public override void Rollback()
        {
            connection.TransactionRollbacks++;
            if (connection.RollbackFailure is not null)
            {
                throw connection.RollbackFailure;
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && connection.TransactionDisposeFailure is not null)
            {
                throw connection.TransactionDisposeFailure;
            }
        }
    }

    private sealed class FakeCommand(FakeConnection connection) : DbCommand
    {
        private readonly SqlCommand _holder = new(); // only for its parameter collection
        private string _text = "";

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get => _text; set => _text = value ?? ""; }
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get => connection; set { } }
        protected override DbParameterCollection DbParameterCollection => _holder.Parameters;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override object? ExecuteScalar() => null;
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new SqlParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();

        public override int ExecuteNonQuery()
        {
            switch (CommandText)
            {
                case "BEGIN":
                    if (connection.BeginFailure is not null)
                    {
                        throw connection.BeginFailure;
                    }

                    connection.Executed.Add(CommandText);
                    return 0;
                case "COMMIT":
                    if (connection.CommitFailure is not null)
                    {
                        throw connection.CommitFailure;
                    }

                    connection.Executed.Add(CommandText);
                    connection.OnCommit?.Invoke();
                    return 0;
                case "ROLLBACK":
                    connection.RawRollbacks++;
                    if (connection.RollbackFailure is not null)
                    {
                        throw connection.RollbackFailure;
                    }

                    connection.Executed.Add(CommandText);
                    return 0;
                default:
                    connection.Executed.Add(CommandText);
                    connection.StatementHadTransaction = DbTransaction is not null;
                    connection.StatementTimeout = CommandTimeout;
                    foreach (SqlParameter p in _holder.Parameters)
                    {
                        connection.Parameters.Add($"{p.ParameterName}={(p.Value is DBNull ? "DBNull" : p.Value)}");
                    }

                    connection.OnStatement?.Invoke();
                    if (connection.StatementFailure is not null)
                    {
                        throw connection.StatementFailure;
                    }

                    return connection.RowsAffected;
            }
        }
    }
}
