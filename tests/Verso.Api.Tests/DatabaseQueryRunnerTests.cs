using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Oracle.ManagedDataAccess.Client;
using Verso.Application.Connections;
using Verso.Databases;

namespace Verso.Api.Tests;

// IMPORTANT: execution against live PostgreSQL, MySQL / MariaDB and Oracle servers is NOT covered here (there are none in this environment; the SQL Server
// runner is covered in DatabaseQueryTests against LocalDB). These tests cover what needs no server: each runner's provider, that its connection string is the probe's
// (so the TLS, timeout and no-pooling rules stay shared), the read-only statement, the Oracle SQL rewrite, the DI registration, the error mapping that does not need a driver
// exception, and the provider-neutral result reader (DbQueryExecution) over a System.Data reader.
// Known limitation, also NOT provable here: an Oracle NUMBER with more precision than .NET's decimal (about 28 digits) makes the Oracle driver throw when read; the step reports
// db.query_failed (use TO_CHAR or ROUND in the query). The Oracle LONG / LOB fetch sizes are set (OracleQueryRunner.Configure) but their effect on a live server is untested.

/// <summary>The Query database step's runners for PostgreSQL, MySQL and Oracle (no server contacted) and the shared result reader.</summary>
public sealed class DatabaseQueryRunnerTests
{
    private static DatabaseConnectionSettings Settings(string provider, bool encrypt = true, bool trust = false) =>
        new(provider, "db.example.test", 0, "app", "reader", "pa;ss=\"w'ord", encrypt, trust, TimeSpan.FromSeconds(10));

    [Fact]
    public void Runners_ExposeTheirProvider()
    {
        Assert.Equal(DatabaseProviders.PostgreSql, new PostgreSqlQueryRunner().Provider);
        Assert.Equal(DatabaseProviders.MySql, new MySqlQueryRunner().Provider);
        Assert.Equal(DatabaseProviders.Oracle, new OracleQueryRunner().Provider);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ConnectionStrings_AreTheProbesOwn(bool encrypt, bool trust)
    {
        var pg = Settings(DatabaseProviders.PostgreSql, encrypt, trust);
        Assert.Equal(PostgreSqlProbe.Build(pg, "Verso query").ConnectionString, PostgreSqlQueryRunner.ConnectionString(pg));
        Assert.Equal(PostgreSqlProbe.Build(pg).ConnectionString.Replace("Verso connection test", "Verso query"), PostgreSqlQueryRunner.ConnectionString(pg));
        Assert.Contains("default_transaction_read_only=on", PostgreSqlQueryRunner.ConnectionString(pg));

        var my = Settings(DatabaseProviders.MySql, encrypt, trust);
        Assert.Equal(MySqlProbe.Build(my).ConnectionString, MySqlQueryRunner.ConnectionString(my));

        var or = Settings(DatabaseProviders.Oracle, encrypt, trust);
        Assert.Equal(OracleProbe.Build(or).ConnectionString, OracleQueryRunner.ConnectionString(or));
    }

    [Fact]
    public void ReadOnlyStatements_ArePerProvider()
    {
        Assert.Equal("BEGIN READ ONLY", PostgreSqlQueryRunner.ReadOnlyStatement);
        Assert.Equal("START TRANSACTION READ ONLY", MySqlQueryRunner.ReadOnlyStatement);
        Assert.Equal("SET TRANSACTION READ ONLY", OracleQueryRunner.ReadOnlyStatement);
    }

    [Fact]
    public void Oracle_RewritesPlaceholdersToSyntheticNames_AndDropsTrailingSemicolon()
    {
        var (sql, names) = OracleQueryRunner.PrepareSql("SELECT a FROM t WHERE id = @id AND n = ':no' AND x = @id ; \n");
        Assert.Equal("SELECT a FROM t WHERE id = :p1 AND n = ':no' AND x = :p1", sql);
        Assert.Equal(["id"], names);
    }

    [Fact]
    public void Oracle_LongReservedAndCaseDifferingNames_AreAllSafe()
    {
        var longName = new string('a', 64);
        var (sql, names) = OracleQueryRunner.PrepareSql($"SELECT 1 FROM t WHERE a = @{longName} AND b = @date AND c = @level AND d = @Id AND e = @id AND f = @user AND g = @number");
        Assert.Equal("SELECT 1 FROM t WHERE a = :p1 AND b = :p2 AND c = :p3 AND d = :p4 AND e = :p5 AND f = :p6 AND g = :p7", sql);
        Assert.Equal([longName, "date", "level", "Id", "id", "user", "number"], names);
    }

    [Fact]
    public void Oracle_Renamed_BindsValuesUnderTheSyntheticNames()
    {
        var (sql, names) = OracleQueryRunner.PrepareSql("SELECT 1 FROM t WHERE a = @Id AND b = @date");
        var request = new DatabaseQueryRequest("k", "ignored", new Dictionary<string, object?> { ["@id"] = 7, ["date"] = null, ["unused"] = 1 }, 5, TimeSpan.FromSeconds(5));
        var renamed = OracleQueryRunner.Renamed(request, sql, names);
        Assert.Equal(sql, renamed.Sql);
        Assert.Equal(2, renamed.Parameters.Count);
        Assert.Equal(7, renamed.Parameters["p1"]); // case-insensitive fallback, as the service checks
        Assert.Null(renamed.Parameters["p2"]);
        Assert.Throws<ArgumentException>(() => OracleQueryRunner.Renamed(request, sql, ["missing"]));
    }

    [Fact]
    public void Oracle_Configure_BindsByName_AndSetsLongAndLobFetchSizes()
    {
        using var command = new OracleCommand();
        OracleQueryRunner.Configure(command);
        Assert.True(command.BindByName);
        Assert.Equal(DbQueryExecution.MaxResultChars + 1, command.InitialLONGFetchSize);
        Assert.Equal(DbQueryExecution.MaxResultChars + 1, command.InitialLOBFetchSize);
    }

    [Fact]
    public void Failures_WithoutADriverException_AreMappedAndNeverCarryDriverText()
    {
        foreach (var map in new Func<Exception, DatabaseQueryResult>[] { PostgreSqlQueryRunner.Map, MySqlQueryRunner.Map, OracleQueryRunner.Map })
        {
            var timeout = map(new TimeoutException());
            Assert.Equal("db.timeout", timeout.Code);
            Assert.True(timeout.Retryable);

            var unknown = map(new DataException("secret server text"));
            Assert.Equal("db.query_failed", unknown.Code);
            Assert.True(unknown.Retryable); // unknown: may be transient
            Assert.DoesNotContain("secret", unknown.Message);
        }
    }

    [Fact]
    public void AddVersoDatabaseDrivers_RegistersAllThreeRunners()
    {
        using var provider = new ServiceCollection().AddVersoDatabaseDrivers().BuildServiceProvider();
        var providers = provider.GetServices<IProviderQueryRunner>().Select(r => r.Provider).OrderBy(p => p).ToList();
        Assert.Equal(new[] { DatabaseProviders.MySql, DatabaseProviders.Oracle, DatabaseProviders.PostgreSql }.OrderBy(p => p), providers);
    }

    // ---- the shared result reader over a System.Data reader ----

    private static DataTable Table(params string[] columns)
    {
        var t = new DataTable();
        foreach (var c in columns) t.Columns.Add(c, typeof(string));
        return t;
    }

    private static Task<DatabaseQueryResult> Read(DataTable table, int maxRows = 10) =>
        DbQueryExecution.ReadAsync(new ChunkingReader(table.CreateDataReader()), maxRows, Stopwatch.StartNew(), CancellationToken.None);

    [Fact]
    public async Task Reader_ReadsRows_AndNull()
    {
        var t = Table("a", "b");
        t.Rows.Add("x", DBNull.Value);
        var r = await Read(t);
        Assert.True(r.Ok);
        Assert.Equal(["a", "b"], r.Columns);
        Assert.Equal("x", r.Rows[0]["a"]);
        Assert.Null(r.Rows[0]["b"]);
        Assert.False(r.Truncated);
    }

    [Fact]
    public async Task Reader_CapsRows_AndMarksTruncated()
    {
        var t = Table("a");
        for (var i = 0; i < 4; i++) t.Rows.Add("r" + i);
        var cut = await Read(t, 3);
        Assert.Equal(3, cut.Rows.Count);
        Assert.True(cut.Truncated);
        var exact = await Read(t, 4);
        Assert.Equal(4, exact.Rows.Count);
        Assert.False(exact.Truncated);
    }

    [Fact]
    public async Task Reader_FirstRowTooLarge_IsAFailure_NotAnEmptyResult()
    {
        var t = Table("a");
        t.Rows.Add(new string('x', DbQueryExecution.MaxResultChars + 1));
        var r = await Read(t);
        Assert.Equal("db.result_too_large", r.Code);
        Assert.False(r.Retryable);
    }

    [Fact]
    public async Task Reader_LaterRowTooLarge_KeepsEarlierRows_AndMarksTruncated()
    {
        var t = Table("a");
        t.Rows.Add("small");
        t.Rows.Add(new string('x', DbQueryExecution.MaxResultChars + 1));
        var r = await Read(t);
        Assert.True(r.Ok);
        Assert.Single(r.Rows);
        Assert.True(r.Truncated);
    }

    [Fact]
    public async Task Reader_RowsAddingUpPastTheCap_AreCut()
    {
        var t = Table("a");
        for (var i = 0; i < 5; i++) t.Rows.Add(new string('x', 100 * 1024));
        var r = await Read(t);
        Assert.True(r.Ok);
        Assert.True(r.Truncated);
        Assert.InRange(r.Rows.Count, 1, 4);
    }

    [Fact]
    public async Task Reader_EmptyOrRepeatedColumnNames_AreBadColumn()
    {
        var empty = Table(" ");
        Assert.Equal("db.bad_column", (await Read(empty)).Code);

        // A DataTable refuses two identical names, so the repeated names come from a fake reader.
        var r = await DbQueryExecution.ReadAsync(new RepeatedNamesReader(), 10, Stopwatch.StartNew(), CancellationToken.None);
        Assert.Equal("db.bad_column", r.Code);
    }

    [Fact]
    public async Task Reader_StatementWithoutAResultSet_IsAFailure()
    {
        var r = await DbQueryExecution.ReadAsync(new DataTable().CreateDataReader(), 10, Stopwatch.StartNew(), CancellationToken.None);
        Assert.Equal("db.query_failed", r.Code);
        Assert.False(r.Retryable);
    }

    [Fact]
    public void MapFailure_Retryability()
    {
        Assert.True(DbQueryExecution.MapFailure("db.timeout", false).Retryable);
        Assert.True(DbQueryExecution.MapFailure("db.unreachable", true).Retryable);
        Assert.False(DbQueryExecution.MapFailure("db.login_failed", false).Retryable);
        Assert.False(DbQueryExecution.MapFailure("db.test_failed", true).Retryable);
        Assert.True(DbQueryExecution.MapFailure("db.test_failed", false).Retryable);
        Assert.Equal("db.query_failed", DbQueryExecution.MapFailure("db.test_failed", true).Code);
    }

    /// <summary>Two columns that are both called "same", and no rows.</summary>
    private sealed class RepeatedNamesReader : DbDataReader
    {
        public override string GetName(int ordinal) => "same";
        public override int FieldCount => 2;
        public override int Depth => 0;
        public override bool HasRows => false;
        public override bool IsClosed => false;
        public override int RecordsAffected => 0;
        public override object this[int ordinal] => throw new NotSupportedException();
        public override object this[string name] => throw new NotSupportedException();
        public override bool GetBoolean(int ordinal) => throw new NotSupportedException();
        public override byte GetByte(int ordinal) => throw new NotSupportedException();
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override char GetChar(int ordinal) => throw new NotSupportedException();
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
        public override string GetDataTypeName(int ordinal) => "string";
        public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();
        public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();
        public override double GetDouble(int ordinal) => throw new NotSupportedException();
        public override System.Collections.IEnumerator GetEnumerator() => throw new NotSupportedException();
        public override Type GetFieldType(int ordinal) => typeof(string);
        public override float GetFloat(int ordinal) => throw new NotSupportedException();
        public override Guid GetGuid(int ordinal) => throw new NotSupportedException();
        public override short GetInt16(int ordinal) => throw new NotSupportedException();
        public override int GetInt32(int ordinal) => throw new NotSupportedException();
        public override long GetInt64(int ordinal) => throw new NotSupportedException();
        public override int GetOrdinal(string name) => 0;
        public override string GetString(int ordinal) => throw new NotSupportedException();
        public override object GetValue(int ordinal) => throw new NotSupportedException();
        public override int GetValues(object[] values) => 0;
        public override bool IsDBNull(int ordinal) => true;
        public override bool NextResult() => false;
        public override bool Read() => false;
    }

    /// <summary>A DataTable reader whose GetChars / GetBytes work on strings and byte arrays like a real driver's (DataTableReader's own throws), so the chunked cell reading is exercised.</summary>
    private class ChunkingReader(DbDataReader inner) : DbDataReader
    {
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        public override int FieldCount => inner.FieldCount;
        public override int Depth => inner.Depth;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override object this[int ordinal] => inner[ordinal];
        public override object this[string name] => inner[name];
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        {
            var all = (byte[])inner.GetValue(ordinal);
            var n = (int)Math.Max(0, Math.Min(length, all.Length - dataOffset));
            Array.Copy(all, dataOffset, buffer!, bufferOffset, n);
            return n;
        }
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        {
            var all = inner.GetString(ordinal);
            var n = (int)Math.Max(0, Math.Min(length, all.Length - dataOffset));
            all.CopyTo((int)dataOffset, buffer!, bufferOffset, n);
            return n;
        }
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override System.Collections.IEnumerator GetEnumerator() => inner.GetEnumerator();
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override string GetString(int ordinal) => inner.GetString(ordinal);
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);
        public override int GetValues(object[] values) => inner.GetValues(values);
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override bool NextResult() => inner.NextResult();
        public override bool Read() => inner.Read();
    }

    // ---- the executor's error handling, through a fake connection whose reader throws ----

    public static TheoryData<Exception> UnexpectedReadFailures => new()
    {
        new InvalidCastException("secret value 42"),
        new OverflowException("secret value 42"),
        new NotSupportedException("secret value 42"),
        new SecretFailure("secret value 42"),
    };

    [Theory]
    [MemberData(nameof(UnexpectedReadFailures))]
    public async Task Executor_UnexpectedReadFailure_IsANonRetryableQueryFailure_WithoutDriverText(Exception thrown)
    {
        foreach (var kind in new[] { "number", "text", "bytes" })
        {
            var result = await RunFake(new ThrowingReader(OneRow(kind), thrown, kind));
            Assert.Equal("db.query_failed", result.Code);
            Assert.False(result.Retryable);
            Assert.DoesNotContain("secret", result.Message);
        }
    }

    [Fact]
    public async Task Executor_Cancellation_StillPropagates()
    {
        using var cts = new CancellationTokenSource();
        var cancel = new OperationCanceledException(cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunFake(new ThrowingReader(OneRow("number"), cancel, "number")));
    }

    [Fact]
    public async Task Executor_FailingRollback_DoesNotReplaceTheResult()
    {
        var t = Table("a");
        t.Rows.Add("x");
        foreach (Exception failure in new Exception[] { new TimeoutException("socket"), new IOException("socket") })
        {
            var result = await RunFake(new ChunkingReader(t.CreateDataReader()), failure);
            Assert.True(result.Ok);
            Assert.Equal("x", result.Rows[0]["a"]);
        }
    }

    private static DataTable OneRow(string kind)
    {
        var t = new DataTable();
        t.Columns.Add(kind, kind == "number" ? typeof(int) : kind == "bytes" ? typeof(byte[]) : typeof(string));
        t.Rows.Add(kind == "number" ? 1 : kind == "bytes" ? new byte[] { 1 } : "x");
        return t;
    }

    private static Task<DatabaseQueryResult> RunFake(DbDataReader reader, Exception? rollbackFailure = null) =>
        DbQueryExecution.RunAsync(
            new FakeConnection(reader, rollbackFailure), "BEGIN", true, "SELECT 1", new DatabaseQueryRequest("k", "SELECT 1", new Dictionary<string, object?>(), 10, TimeSpan.FromSeconds(5)),
            "@", ex => DatabaseQueryResult.Failure("db.query_failed", true), CancellationToken.None);

    /// <summary>Throws the given exception from whichever read call the column's kind uses (GetValue for numbers, GetChars for text, GetBytes for binary).</summary>
    private sealed class ThrowingReader(DataTable table, Exception thrown, string kind) : ChunkingReader(table.CreateDataReader())
    {
        public override object GetValue(int ordinal) => kind == "number" ? throw thrown : base.GetValue(ordinal);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => kind == "text" ? throw thrown : base.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => kind == "bytes" ? throw thrown : base.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
    }

    private sealed class SecretFailure(string message) : Exception(message);

    private sealed class FakeConnection(DbDataReader reader, Exception? rollbackFailure) : DbConnection
    {
        private ConnectionState _state = ConnectionState.Closed;
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get => ""; set { } }
        public override string Database => "";
        public override string DataSource => "";
        public override string ServerVersion => "";
        public override ConnectionState State => _state;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() => _state = ConnectionState.Closed;
        public override void Open() => _state = ConnectionState.Open;
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new FakeCommand(this, reader, rollbackFailure);
    }

    private sealed class FakeCommand(FakeConnection connection, DbDataReader reader, Exception? rollbackFailure) : DbCommand
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get => _text; set => _text = value ?? ""; }
        private string _text = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get => connection; set { } }
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery() => CommandText == "ROLLBACK" && rollbackFailure is not null ? throw rollbackFailure : 0;
        public override object? ExecuteScalar() => null;
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => reader;
    }
}
