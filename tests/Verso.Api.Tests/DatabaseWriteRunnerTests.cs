using System.Data;
using System.Data.Common;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using Verso.Application.Connections;
using Verso.Databases;

namespace Verso.Api.Tests;

// IMPORTANT: NO live PostgreSQL, MySQL / MariaDB or Oracle server is available in this environment, so executing a write against them is NOT covered here.
// Only structure is: each runner's provider, its connection string (the probe's, apart from the application name and, for PostgreSQL, the read-only option),
// the statement that begins its transaction (never READ ONLY), that PostgreSQL and MySQL run through the shared DbWriteExecution in raw mode over a fake DbConnection,
// the Oracle SQL rewrite and re-keying, the error classification tables (pure functions over error numbers) and the DI registration.
// Driver exceptions (PostgresException with a SqlState, MySqlException, OracleException) can only be partly constructed without a server, so the tables are tested on
// their codes and the exception wiring (which property is read) is NOT proven. The SQL Server write runner is covered in DatabaseWriteTests against LocalDB.

/// <summary>The Write to database step's runners for PostgreSQL, MySQL and Oracle (no server contacted).</summary>
public sealed class DatabaseWriteRunnerTests
{
    private static DatabaseConnectionSettings Settings(string provider, bool encrypt = true, bool trust = false) =>
        new(provider, "db.example.test", 0, "app", "writer", "pa;ss=\"w'ord", encrypt, trust, TimeSpan.FromSeconds(10));

    private static DatabaseWriteRequest Request(int maxRows = 5) =>
        new("k", "UPDATE t SET a = 1", new Dictionary<string, object?>(), maxRows, TimeSpan.FromSeconds(5));

    [Fact]
    public void Runners_ExposeTheirProvider()
    {
        Assert.Equal(DatabaseProviders.PostgreSql, new PostgreSqlWriteRunner().Provider);
        Assert.Equal(DatabaseProviders.MySql, new MySqlWriteRunner().Provider);
        Assert.Equal(DatabaseProviders.Oracle, new OracleWriteRunner().Provider);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ConnectionStrings_AreTheProbesOwn_ApartFromApplicationNameAndReadOnly(bool encrypt, bool trust)
    {
        var pg = Settings(DatabaseProviders.PostgreSql, encrypt, trust);
        var write = PostgreSqlWriteRunner.ConnectionString(pg);
        Assert.Equal(PostgreSqlProbe.Build(pg, "Verso write", readOnly: false).ConnectionString, write);
        Assert.DoesNotContain("default_transaction_read_only", write);
        Assert.Contains("Verso write", write);
        var readOnlyBuilder = PostgreSqlProbe.Build(pg, "Verso write");
        readOnlyBuilder.Options = null;
        Assert.Equal(readOnlyBuilder.ConnectionString, write); // identical apart from the read-only option

        // the read-only users of the same builder are unchanged
        Assert.Contains("default_transaction_read_only=on", PostgreSqlProbe.Build(pg).ConnectionString);
        Assert.Contains("default_transaction_read_only=on", PostgreSqlQueryRunner.ConnectionString(pg));

        var my = Settings(DatabaseProviders.MySql, encrypt, trust);
        Assert.Equal(MySqlProbe.Build(my).ConnectionString, MySqlWriteRunner.ConnectionString(my));

        var or = Settings(DatabaseProviders.Oracle, encrypt, trust);
        Assert.Equal(OracleProbe.Build(or).ConnectionString, OracleWriteRunner.ConnectionString(or));
    }

    [Fact]
    public void BeginStatements_ArePerProvider_AndNeverReadOnly()
    {
        Assert.Equal("BEGIN", PostgreSqlWriteRunner.BeginStatement);
        Assert.Equal("START TRANSACTION", MySqlWriteRunner.BeginStatement);
        Assert.DoesNotContain("READ ONLY", PostgreSqlWriteRunner.BeginStatement, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("READ ONLY", MySqlWriteRunner.BeginStatement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostgreSql_RunsBeginStatementCommit_InRawMode()
    {
        var connection = new RecordingConnection(1);
        var result = await PostgreSqlWriteRunner.RunOn(connection, Request(), CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Equal(1, result.RowsAffected);
        Assert.Equal(["BEGIN", "UPDATE t SET a = 1", "COMMIT"], connection.Commands);
        Assert.Equal(0, connection.DriverTransactions);
    }

    [Fact]
    public async Task MySql_RunsBeginStatementCommit_InRawMode()
    {
        var connection = new RecordingConnection(2);
        var result = await MySqlWriteRunner.RunOn(connection, Request(), CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Equal(2, result.RowsAffected);
        Assert.Equal(["START TRANSACTION", "UPDATE t SET a = 1", "COMMIT"], connection.Commands);
        Assert.Equal(0, connection.DriverTransactions);
    }

    [Fact]
    public async Task RawRunners_RollBack_WhenMoreRowsChangedThanAllowed()
    {
        var connection = new RecordingConnection(9);
        var result = await PostgreSqlWriteRunner.RunOn(connection, Request(3), CancellationToken.None);
        Assert.Equal("db.too_many_rows_affected", result.Code);
        Assert.Equal(["BEGIN", "UPDATE t SET a = 1", "ROLLBACK"], connection.Commands);
    }

    [Fact]
    public void Oracle_RewritesPlaceholdersToSyntheticNames_AndDropsTrailingSemicolon()
    {
        var (sql, names) = OracleWriteRunner.PrepareSql("UPDATE t SET a = @a WHERE id = @id AND n = ':no' AND x = @id ; \n");
        Assert.Equal("UPDATE t SET a = :p1 WHERE id = :p2 AND n = ':no' AND x = :p2", sql);
        Assert.Equal(["a", "id"], names);
    }

    [Fact]
    public void Oracle_Renamed_BindsValuesUnderTheSyntheticNames()
    {
        var (sql, names) = OracleWriteRunner.PrepareSql("UPDATE t SET a = @Id WHERE b = @date");
        var request = new DatabaseWriteRequest("k", "ignored", new Dictionary<string, object?> { ["@id"] = 7, ["date"] = null, ["unused"] = 1 }, 5, TimeSpan.FromSeconds(5));
        var renamed = OracleWriteRunner.Renamed(request, sql, names);
        Assert.Equal(sql, renamed.Sql);
        Assert.Equal(2, renamed.Parameters.Count);
        Assert.Equal(7, renamed.Parameters["p1"]);
        Assert.Null(renamed.Parameters["p2"]);
        Assert.Throws<ArgumentException>(() => OracleWriteRunner.Renamed(request, sql, ["missing"]));
    }

    [Fact]
    public async Task Oracle_MissingParameter_IsABadParameterFailure_BeforeAnythingIsSent()
    {
        var request = new DatabaseWriteRequest("k", "UPDATE t SET a = @a", new Dictionary<string, object?>(), 5, TimeSpan.FromSeconds(5));
        var result = await new OracleWriteRunner().RunAsync(Settings(DatabaseProviders.Oracle), request, CancellationToken.None);
        Assert.Equal("sql.bad_parameter", result.Code);
        Assert.False(result.OutcomeUnknown);
    }

    [Fact]
    public void Oracle_Configure_BindsByName()
    {
        using var command = new OracleCommand();
        OracleWriteRunner.Configure(command);
        Assert.True(command.BindByName);
    }

    // ---- error classification ----

    [Theory]
    [InlineData("22001")] [InlineData("22012")] [InlineData("23502")] [InlineData("23505")] [InlineData("23503")]
    [InlineData("42601")] [InlineData("42P01")] [InlineData("42703")] [InlineData("42501")]
    [InlineData("28000")] [InlineData("28P01")] [InlineData("3D000")] [InlineData("40001")] [InlineData("40P01")] [InlineData("25006")]
    public void PostgreSql_KnownStatementErrors(string sqlState)
    {
        Assert.True(PostgreSqlWriteRunner.IsKnownStatementError(sqlState));
        var result = PostgreSqlWriteRunner.Map(sqlState, new InvalidOperationException("x"), sent: true);
        Assert.Equal("db.query_failed", result.Code);
        Assert.False(result.OutcomeUnknown);
    }

    [Theory]
    [InlineData("57014")] [InlineData("08006")] [InlineData("08003")] [InlineData("53200")] [InlineData("53300")]
    [InlineData("57P01")] [InlineData("57P02")] [InlineData("58030")] [InlineData("XX000")] [InlineData("")]
    public void PostgreSql_UnknownAfterSending(string sqlState)
    {
        Assert.False(PostgreSqlWriteRunner.IsKnownStatementError(sqlState));
        var result = PostgreSqlWriteRunner.Map(sqlState, new InvalidOperationException("x"), sent: true);
        Assert.True(result.OutcomeUnknown);
        Assert.Equal("db.query_failed", result.Code);
    }

    [Theory]
    [InlineData(1062)] [InlineData(1048)] [InlineData(1054)] [InlineData(1146)] [InlineData(1064)] [InlineData(1136)]
    [InlineData(1264)] [InlineData(1265)] [InlineData(1366)] [InlineData(1451)] [InlineData(1452)]
    [InlineData(1142)] [InlineData(1143)] [InlineData(1044)] [InlineData(1045)] [InlineData(1213)]
    public void MySql_KnownStatementErrors(int number)
    {
        Assert.True(MySqlWriteRunner.IsKnownStatementError(number));
        var result = MySqlWriteRunner.Map(number, new InvalidOperationException("x"), sent: true);
        Assert.Equal("db.query_failed", result.Code);
        Assert.False(result.OutcomeUnknown);
    }

    [Theory]
    [InlineData(1205)] [InlineData(2006)] [InlineData(2013)] [InlineData(1927)] [InlineData(0)]
    public void MySql_UnknownAfterSending(int number)
    {
        Assert.False(MySqlWriteRunner.IsKnownStatementError(number));
        var result = MySqlWriteRunner.Map(number, new InvalidOperationException("x"), sent: true);
        Assert.True(result.OutcomeUnknown);
    }

    [Fact]
    public void MySql_TheDriversCommandTimeout_IsNotInTheKnownSet()
    {
        // MySqlConnector reports a command timeout as MySqlException with this error code; it must stay an unknown outcome.
        Assert.False(MySqlWriteRunner.IsKnownStatementError((int)MySqlErrorCode.CommandTimeoutExpired));
    }

    [Theory]
    [InlineData(1)] [InlineData(942)] [InlineData(904)] [InlineData(903)] [InlineData(1031)] [InlineData(1400)] [InlineData(1407)]
    [InlineData(1722)] [InlineData(2290)] [InlineData(2291)] [InlineData(2292)] [InlineData(12899)] [InlineData(1861)] [InlineData(1843)]
    [InlineData(1427)] [InlineData(60)]
    public void Oracle_KnownStatementErrors(int number)
    {
        Assert.True(OracleWriteRunner.IsKnownStatementError(number));
        var result = OracleWriteRunner.Map(number, new InvalidOperationException("x"), sent: true);
        Assert.Equal("db.query_failed", result.Code);
        Assert.False(result.OutcomeUnknown);
    }

    [Theory]
    [InlineData(3113)] [InlineData(3114)] [InlineData(3135)] [InlineData(12170)] [InlineData(12541)] [InlineData(12543)]
    [InlineData(12547)] [InlineData(12571)] [InlineData(0)]
    public void Oracle_UnknownAfterSending(int number)
    {
        Assert.False(OracleWriteRunner.IsKnownStatementError(number));
        var result = OracleWriteRunner.Map(number, new InvalidOperationException("x"), sent: true);
        Assert.True(result.OutcomeUnknown);
    }

    [Fact]
    public void GenericFailures_AreUnknownAfterSending_AndKnownBefore()
    {
        foreach (Func<Exception, bool, DatabaseWriteResult> map in new Func<Exception, bool, DatabaseWriteResult>[] { PostgreSqlWriteRunner.MapAny, MySqlWriteRunner.MapAny, OracleWriteRunner.MapAny })
        {
            var after = map(new DataException("secret server text"), true);
            Assert.Equal("db.query_failed", after.Code);
            Assert.True(after.OutcomeUnknown);
            Assert.DoesNotContain("secret", after.Message);

            var before = map(new DataException("secret server text"), false);
            Assert.Equal("db.query_failed", before.Code);
            Assert.False(before.OutcomeUnknown);

            var timeoutAfter = map(new TimeoutException(), true);
            Assert.Equal("db.timeout", timeoutAfter.Code);
            Assert.True(timeoutAfter.OutcomeUnknown);

            var timeoutBefore = map(new TimeoutException(), false);
            Assert.Equal("db.timeout", timeoutBefore.Code);
            Assert.False(timeoutBefore.OutcomeUnknown);

            var unreachable = map(new InvalidOperationException("x", new SocketException()), false);
            Assert.Equal("db.unreachable", unreachable.Code);
            Assert.False(unreachable.OutcomeUnknown);
        }
    }

    [Fact]
    public void KnownCodes_BeforeSending_GoThroughDescribe_AndAreNeverUnknown()
    {
        // The explicit "sent && known" branch: a known code before the statement was sent is not reported as the statement failing; the probe's wording applies.
        var pg = PostgreSqlWriteRunner.Map("23505", new TimeoutException(), sent: false);
        Assert.Equal("db.timeout", pg.Code);
        Assert.False(pg.OutcomeUnknown);
        var my = MySqlWriteRunner.Map(1062, new InvalidOperationException("x"), sent: false);
        Assert.Equal("db.query_failed", my.Code);
        Assert.False(my.OutcomeUnknown);
        var or = OracleWriteRunner.Map(1, new InvalidOperationException("x"), sent: false);
        Assert.Equal("db.query_failed", or.Code);
        Assert.False(or.OutcomeUnknown);
    }

    [Fact]
    public void AddVersoDatabaseDrivers_RegistersTheWriteRunners_AndStillTheQueryRunners()
    {
        using var provider = new ServiceCollection().AddVersoDatabaseDrivers().BuildServiceProvider();
        var expected = new[] { DatabaseProviders.MySql, DatabaseProviders.Oracle, DatabaseProviders.PostgreSql }.OrderBy(p => p);
        Assert.Equal(expected, provider.GetServices<IProviderWriteRunner>().Select(r => r.Provider).OrderBy(p => p));
        Assert.Equal(expected, provider.GetServices<IProviderQueryRunner>().Select(r => r.Provider).OrderBy(p => p));
    }

    // ---- a fake connection that records the statements the shared executor sends ----

    private sealed class RecordingConnection(int rows) : DbConnection
    {
        private ConnectionState _state = ConnectionState.Closed;
        public List<string> Commands { get; } = [];
        public int DriverTransactions { get; private set; }
        public int Rows => rows;
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get => ""; set { } }
        public override string Database => "";
        public override string DataSource => "";
        public override string ServerVersion => "";
        public override ConnectionState State => _state;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() => _state = ConnectionState.Closed;
        public override void Open() => _state = ConnectionState.Open;
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            DriverTransactions++;
            throw new NotSupportedException();
        }

        protected override DbCommand CreateDbCommand() => new RecordingCommand(this);
    }

    private sealed class RecordingCommand(RecordingConnection connection) : DbCommand
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get => connection; set { } }
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery()
        {
            connection.Commands.Add(CommandText);
            return CommandText is "BEGIN" or "START TRANSACTION" or "COMMIT" or "ROLLBACK" ? 0 : connection.Rows;
        }

        public override object? ExecuteScalar() => null;
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
    }
}
