using System.Data.Common;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Verso.Application.Executions;
using Verso.Infrastructure.Persistence;

namespace Verso.Api.Tests;

/// <summary>
/// tools/db/least-privilege.sql, applied to its own database: the API and the Worker run the whole MVP flow under
/// their roles (proving the grants suffice), and the write-once rules hold even for code that tries to break them.
/// Each identity is a loginless database user; every EF connection switches to it with EXECUTE AS USER.
/// (The Hangfire client keeps the test's own identity: its connections aren't EF's.)
/// </summary>
public sealed class LeastPrivilegeFixture : IAsyncLifetime
{
    public const string ApiUser = "verso_api_test";
    public const string WorkerUser = "verso_worker_test";

    private readonly TestDatabase _database = new("grants");

    internal string ConnectionString { get; private set; } = "";
    internal WebApplicationFactory<Program> Api { get; private set; } = null!;
    internal VersoApiFactory BaseFactory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _database.MigrateAsync();
        ConnectionString = new SqlConnectionStringBuilder(_database.ConnectionString) { Pooling = false }.ConnectionString; // EXECUTE AS can't return to a pool

        await using (var connection = new SqlConnection(_database.ConnectionString))
        {
            await connection.OpenAsync();
            foreach (var batch in Script().Split(["\nGO\n"], StringSplitOptions.RemoveEmptyEntries))
            {
                await Execute(connection, batch);
            }

            await Execute(connection, $"""
                CREATE USER [{ApiUser}] WITHOUT LOGIN;    ALTER ROLE verso_api ADD MEMBER [{ApiUser}];
                CREATE USER [{WorkerUser}] WITHOUT LOGIN; ALTER ROLE verso_worker ADD MEMBER [{WorkerUser}];
                """);
            await Execute(connection, Script()); // idempotent: a second run changes nothing and doesn't fail
        }

        BaseFactory = new VersoApiFactory(_database.ConnectionString);
        Api = BaseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.ConfigureDbContext<VersoDbContext>(o => o.UseSqlServer(ConnectionString).AddInterceptors(new RunAs(ApiUser)))));
    }

    public async Task DisposeAsync()
    {
        await Api.DisposeAsync();
        await BaseFactory.DisposeAsync();
        await _database.DropAsync();
    }

    internal VersoDbContext WorkerDb() =>
        new(new DbContextOptionsBuilder<VersoDbContext>().UseSqlServer(ConnectionString).AddInterceptors(new RunAs(WorkerUser)).Options);

    internal VersoDbContext DbAs(string user) =>
        new(new DbContextOptionsBuilder<VersoDbContext>().UseSqlServer(ConnectionString).AddInterceptors(new RunAs(user)).Options);

    private static string Script()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Verso.slnx")))
        {
            dir = dir.Parent;
        }

        return File.ReadAllText(Path.Combine(dir!.FullName, "tools", "db", "least-privilege.sql")).ReplaceLineEndings("\n");
    }

    private static async Task Execute(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Runs every connection of a context as the given database user.</summary>
    private sealed class RunAs(string user) : DbConnectionInterceptor
    {
        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"EXECUTE AS USER = '{user}'";
            command.ExecuteNonQuery();
        }

        public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"EXECUTE AS USER = '{user}'";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}

[Collection(ApiTestGroup.Name)] // keeps Hangfire hosts from configuring themselves in parallel
public sealed class LeastPrivilegeTests(LeastPrivilegeFixture grants) : IClassFixture<LeastPrivilegeFixture>, IDisposable
{
    private readonly TestDocumentStore _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task TheWholeFlow_RunsUnderTheApiAndWorkerRoles()
    {
        using var client = grants.Api.CreateClient();
        var kit = new ExecutionTestKit(grants.BaseFactory, client);

        var key = await kit.PublishInvoiceWorkflowAsync();               // API: author, publish
        var ok = await kit.SubmitNewAsync(key, ExecutionTestKit.Sample);   // API: submit
        var rejected = await kit.SubmitNewAsync(key, ExecutionTestKit.Broken);
        await RunAsWorkerAsync(ok);                                        // Worker: validate, PDF, archive, audit
        await RunAsWorkerAsync(rejected);
        await using (var db = grants.WorkerDb())
        {
            Assert.Equal(0, await new ExecutionDispatcher(db, new RecordingExecutionJobs(), TimeProvider.System).RunOnceAsync(CancellationToken.None));
        }

        var done = await kit.GetExecutionAsync(ok);                        // API: read
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal("Rejected", (await kit.GetExecutionAsync(rejected)).GetProperty("status").GetString());
        using var input = await client.GetAsync(new Uri($"/api/v1/executions/{ok}/input", UriKind.Relative)); // API: audited read
        Assert.Equal(HttpStatusCode.OK, input.StatusCode);
        using var audit = await client.GetAsync(new Uri("/api/v1/audit?pageSize=5", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        using var health = await client.GetAsync(new Uri("/api/health", UriKind.Relative));
        Assert.Contains("\"pendingMigrations\":0", await health.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LeastPrivilegeFixture.ApiUser, "UPDATE [audit].[AuditEntries] SET [ActorName] = N'x'")]
    [InlineData(LeastPrivilegeFixture.ApiUser, "DELETE FROM [audit].[AuditEntries]")]
    [InlineData(LeastPrivilegeFixture.WorkerUser, "UPDATE [audit].[AuditEntries] SET [ActorName] = N'x'")]
    [InlineData(LeastPrivilegeFixture.WorkerUser, "DELETE FROM [audit].[AuditEntries]")]
    [InlineData(LeastPrivilegeFixture.WorkerUser, "UPDATE [documents].[ArchiveRecords] SET [FolderPath] = N'x'")]
    [InlineData(LeastPrivilegeFixture.WorkerUser, "DELETE FROM [documents].[ArchiveRecords]")]
    [InlineData(LeastPrivilegeFixture.WorkerUser, "DELETE FROM [documents].[Documents]")]
    [InlineData(LeastPrivilegeFixture.WorkerUser, "DELETE FROM [executions].[ExecutionEvents]")]
    [InlineData(LeastPrivilegeFixture.ApiUser, "INSERT INTO [documents].[ArchiveRecords] ([Id], [DocumentId], [ArchivedAt], [Retention], [FolderPath], [Metadata], [SearchText]) VALUES (NEWID(), NEWID(), SYSDATETIMEOFFSET(), '7y', N'x', N'{{}}', N'')")] // {{}}: ExecuteSqlRaw reads braces as format placeholders
    [InlineData(LeastPrivilegeFixture.ApiUser, "UPDATE [documents].[Documents] SET [FileName] = N'x'")]
    [InlineData(LeastPrivilegeFixture.ApiUser, "DELETE FROM [templates].[TemplateVersions]")]
    [InlineData(LeastPrivilegeFixture.WorkerUser, "UPDATE [templates].[TemplateVersions] SET [UpdatedBy] = N'x'")]
    [InlineData(LeastPrivilegeFixture.WorkerUser, "UPDATE [workflows].[WorkflowVersions] SET [UpdatedBy] = N'x'")]
    [InlineData(LeastPrivilegeFixture.ApiUser, "CREATE TABLE [dbo].[Sneaky] ([Id] int)")]
    [InlineData(LeastPrivilegeFixture.WorkerUser, "ALTER TABLE [audit].[AuditEntries] ADD [Sneaky] int NULL")]
    public async Task ForbiddenChanges_AreRefusedByTheDatabase(string user, string sql)
    {
        await using var db = grants.DbAs(user);

        var ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(sql));

        Assert.Contains(ex.Number, PermissionDenied);
    }

    /// <summary>SQL Server "permission denied" errors: object, column, CREATE, ALTER (missing object or permission).</summary>
    private static readonly int[] PermissionDenied = [229, 230, 262, 1088, 4902];

    private async Task RunAsWorkerAsync(Guid executionId)
    {
        await using var db = grants.WorkerDb();
        await new ExecutionEngine(db, new FakePdfPrinter(), _store, new RecordingExecutionJobs(), TimeProvider.System, NullLogger<ExecutionEngine>.Instance)
            .RunAsync(executionId, CancellationToken.None);
    }
}
