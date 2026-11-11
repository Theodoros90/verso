using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verso.Application.Ingestion;
using Verso.Domain.Ingestion;
using Verso.Infrastructure.Ingestion;
using Verso.Infrastructure.Persistence;

namespace Verso.Api.Tests;

/// <summary>Administration of folder locations: Admin only, audited, validated, and picked up by the Worker.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class FolderLocationAdminTests : IDisposable
{
    private readonly MigratedDatabaseFixture _fixture;
    private readonly HttpClient _admin;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "verso-test-folders", Guid.NewGuid().ToString("N"));
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    public FolderLocationAdminTests(MigratedDatabaseFixture fixture)
    {
        _fixture = fixture;
        _admin = fixture.Factory.CreateClientAs("dev.admin");
        Directory.CreateDirectory(Path.Combine(_root, "inbound"));
    }

    public void Dispose()
    {
        _admin.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private string Key(string name) => $"{name}-{_suffix}";

    private string Inbound => Path.Combine(_root, "inbound");

    private Task<HttpResponseMessage> PutAsync(string key, object body, HttpClient? client = null) =>
        (client ?? _admin).PutAsJsonAsync(new Uri($"/api/v1/admin/folder-locations/{key}", UriKind.Relative), body);

    private static Task<JsonElement> ReadAsync(HttpResponseMessage r) => ExecutionTestKit.ReadAsync(r);

    /// <summary>One Worker pass over the catalog, with the real adapter and no configured locations.</summary>
    private async Task<LocalFolderAccess> WorkerSyncAsync(LocalFolderAccess? folders = null)
    {
        folders ??= new LocalFolderAccess(Options.Create(new FolderAccessOptions()));
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VersoDbContext>();
        await new FolderIngestion(db, folders, null!, new FolderIngestionOptions(), TimeProvider.System, NullLogger<FolderIngestion>.Instance)
            .SyncLocationsAsync(CancellationToken.None);
        return folders;
    }

    [Fact]
    public async Task Admin_AddsALocation_TheWorkerPicksItUp_AndDesignersSeeNoPath()
    {
        var key = Key("ap-inbound");

        using var saved = await PutAsync(key, new { name = "AP inbound", path = Inbound, input = true, output = false });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var created = await ReadAsync(saved);
        Assert.Equal(Inbound, created.GetProperty("path").GetString());
        Assert.Contains("Waiting for the Worker", created.GetProperty("problem").GetString(), StringComparison.Ordinal); // not reported yet
        Assert.False(created.GetProperty("enabled").GetBoolean());

        var folders = await WorkerSyncAsync();
        Assert.Null(folders.Locations.Single(l => l.Key == key).Problem);

        using var list = await _admin.GetAsync(new Uri("/api/v1/admin/folder-locations", UriKind.Relative));
        var item = (await ReadAsync(list)).EnumerateArray().Single(l => l.GetProperty("key").GetString() == key);
        Assert.True(item.GetProperty("enabled").GetBoolean());
        Assert.True(item.GetProperty("managed").GetBoolean());

        // Designers (and everyone else) see key, name and purpose, never the path.
        using var designer = _fixture.Factory.CreateClientAs("dev.workflow-author");
        using var catalog = await designer.GetAsync(new Uri("/api/v1/folder-locations", UriKind.Relative));
        var body = await catalog.Content.ReadAsStringAsync();
        Assert.Contains($"\"key\":\"{key}\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain(_root.Replace("\\", "\\\\", StringComparison.Ordinal), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"path\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_CanTestAccessToTheNewLocation_AsTheWorkerIdentity()
    {
        var key = Key("tested");
        await PutAsync(key, new { name = "Tested", path = Inbound, input = true, output = false });
        var folders = await WorkerSyncAsync();

        using var queued = await _admin.PostAsJsonAsync(new Uri("/api/v1/folder-access-tests", UriKind.Relative),
            new { targets = new[] { new { role = "source", location = key, subfolder = "" } } });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var id = (await ReadAsync(queued)).GetProperty("id").GetGuid();
        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            await new FolderAccessTestRunner(scope.ServiceProvider.GetRequiredService<VersoDbContext>(), folders, TimeProvider.System).RunAsync(id, CancellationToken.None);
        }

        using var result = await _admin.GetAsync(new Uri($"/api/v1/folder-access-tests/{id}", UriKind.Relative));
        var test = await ReadAsync(result);
        Assert.All(test.GetProperty("results").EnumerateArray(), r => Assert.True(r.GetProperty("ok").GetBoolean(), r.ToString()));
    }

    [Fact]
    public async Task Edit_ChangesThePath_AndEverySaveIsAudited()
    {
        var key = Key("edited");
        await PutAsync(key, new { name = "First", path = Inbound, input = true, output = false });
        var second = Path.Combine(_root, "second");

        using var edited = await PutAsync(key, new { name = "Second", path = second, input = true, output = true });

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var dto = await ReadAsync(edited);
        Assert.Equal("Second", dto.GetProperty("name").GetString());
        Assert.True(dto.GetProperty("output").GetBoolean());
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VersoDbContext>();
        var audit = await db.AuditEntries.AsNoTracking()
            .Where(a => a.EntityType == FolderLocationAdmin.EntityType && a.EntityId == key).OrderBy(a => a.Sequence).ToListAsync();
        Assert.Equal(["FolderLocationSaved", "FolderLocationSaved"], audit.Select(a => a.Action));
        Assert.Contains(second.Replace("\\", "\\\\", StringComparison.Ordinal), audit[1].Details, StringComparison.Ordinal);
        Assert.Equal("dev.admin", audit[1].ActorId);
    }

    [Theory]
    [InlineData("Bad-Key", "AP", @"D:\x", true, "key")]
    [InlineData("ok-key", "", @"D:\x", true, "name")]
    [InlineData("ok-key", "AP", @"relative\folder", true, "path")]
    [InlineData("ok-key", "AP", @"D:\x\..\y", true, "path")]
    [InlineData("ok-key", "AP", @"D:\x", false, "input")] // neither input nor output
    public async Task InvalidLocations_AreRefused(string key, string name, string path, bool input, string field)
    {
        using var response = await PutAsync(key, new { name, path, input, output = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await ReadAsync(response)).GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task AKeyFromTheWorkersConfiguration_CantBeTaken_AndIsListedReadOnly()
    {
        var key = Key("configured");
        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VersoDbContext>();
            db.FolderLocations.Add(FolderLocation.Report(key, "From config", true, false, managed: false, null, "worker", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        using var response = await PutAsync(key, new { name = "Mine", path = Inbound, input = true, output = false });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("folder_location.key_taken", (await ReadAsync(response)).GetProperty("code").GetString());
        using var list = await _admin.GetAsync(new Uri("/api/v1/admin/folder-locations", UriKind.Relative));
        var item = (await ReadAsync(list)).EnumerateArray().Single(l => l.GetProperty("key").GetString() == key);
        Assert.False(item.GetProperty("managed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("path").ValueKind);
    }

    [Fact]
    public async Task Remove_DisablesTheLocation_AndAddingItAgainBringsItBack()
    {
        var key = Key("removable");
        await PutAsync(key, new { name = "Removable", path = Inbound, input = true, output = false });
        var folders = await WorkerSyncAsync();

        using var removed = await _admin.DeleteAsync(new Uri($"/api/v1/admin/folder-locations/{key}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        await WorkerSyncAsync(folders);

        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VersoDbContext>();
            var catalog = await db.FolderLocations.AsNoTracking().SingleAsync(l => l.Key == key); // disabled, not deleted
            Assert.False(catalog.Enabled);
            Assert.Equal("Removed by an administrator.", catalog.Problem);
        }

        using var list = await _admin.GetAsync(new Uri("/api/v1/admin/folder-locations", UriKind.Relative));
        Assert.DoesNotContain((await ReadAsync(list)).EnumerateArray(), l => l.GetProperty("key").GetString() == key);

        using var again = await PutAsync(key, new { name = "Removable", path = Inbound, input = true, output = false });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        await WorkerSyncAsync(folders);
        await using var scope2 = _fixture.Factory.Services.CreateAsyncScope();
        Assert.True((await scope2.ServiceProvider.GetRequiredService<VersoDbContext>().FolderLocations.AsNoTracking().SingleAsync(l => l.Key == key)).Enabled);
    }

    [Fact]
    public async Task Remove_IsRefusedWhileFilesAreBeingProcessed_AndForUnknownKeys()
    {
        var key = Key("busy");
        await PutAsync(key, new { name = "Busy", path = Inbound, input = true, output = false });
        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VersoDbContext>();
            db.FolderFiles.Add(FolderFile.Claim(Guid.NewGuid(), "wf", "trigger", key, "", "x.json", "x.json", 1, DateTimeOffset.UtcNow,
                "other-out", "p", "other-out", "f", "test", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2)));
            await db.SaveChangesAsync();
        }

        using var busy = await _admin.DeleteAsync(new Uri($"/api/v1/admin/folder-locations/{key}", UriKind.Relative));
        using var unknown = await _admin.DeleteAsync(new Uri($"/api/v1/admin/folder-locations/{Key("nothing")}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Equal("folder_location.in_use", (await ReadAsync(busy)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Theory]
    [InlineData("dev.viewer")]
    [InlineData("dev.operator")]
    [InlineData("dev.template-author")]
    [InlineData("dev.workflow-author")]
    [InlineData("dev.auditor")]
    public async Task OnlyAdminsMayManageLocations(string user)
    {
        using var client = _fixture.Factory.CreateClientAs(user);
        var key = Key("sneaky");

        using var list = await client.GetAsync(new Uri("/api/v1/admin/folder-locations", UriKind.Relative));
        using var save = await PutAsync(key, new { name = "Sneaky", path = @"C:\Windows", input = true, output = true }, client);
        using var remove = await client.DeleteAsync(new Uri($"/api/v1/admin/folder-locations/{key}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, save.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<VersoDbContext>().ManagedFolderLocations.AnyAsync(l => l.Key == key));
    }

    [Fact]
    public async Task Me_ReportsTheAdministerPermission_OnlyForAdmins()
    {
        using var admin = await _admin.GetAsync(new Uri("/api/v1/me", UriKind.Relative));
        using var auditorClient = _fixture.Factory.CreateClientAs("dev.auditor");
        using var auditor = await auditorClient.GetAsync(new Uri("/api/v1/me", UriKind.Relative));

        Assert.True((await ReadAsync(admin)).GetProperty("permissions").GetProperty("administer").GetBoolean());
        Assert.False((await ReadAsync(auditor)).GetProperty("permissions").GetProperty("administer").GetBoolean());
    }
}
