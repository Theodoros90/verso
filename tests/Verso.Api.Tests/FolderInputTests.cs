using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verso.Application;
using Verso.Application.Executions;
using Verso.Application.Ingestion;
using Verso.Domain.Ingestion;
using Verso.Infrastructure.Ingestion;
using Verso.Infrastructure.Persistence;

namespace Verso.Api.Tests;

/// <summary>
/// Folder input end to end on real folders and a real database: the Worker's ingestion passes (called as the
/// Worker's loop would), the engine, and the API. Each test has its own folders and location keys.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class FolderInputTests : IDisposable
{
    private readonly MigratedDatabaseFixture _fixture;
    private readonly HttpClient _client;
    private readonly ExecutionTestKit _kit;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "verso-test-folders", Guid.NewGuid().ToString("N"));
    private readonly string _in;
    private readonly string _out;
    private readonly LocalFolderAccess _folders;
    private readonly RecordingExecutionJobs _jobs = new();
    private readonly TestDocumentStore _store = new();
    private readonly FakePdfPrinter _printer = new();

    public FolderInputTests(MigratedDatabaseFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Factory.CreateClient();
        _kit = new ExecutionTestKit(fixture.Factory, _client);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        _in = "in-" + suffix;
        _out = "out-" + suffix;
        Directory.CreateDirectory(Path.Combine(_root, "in"));
        Directory.CreateDirectory(Path.Combine(_root, "out"));
        _folders = new LocalFolderAccess(Options.Create(new FolderAccessOptions
        {
            Locations = new()
            {
                [_in] = new() { Name = "Inbound " + suffix, Path = Path.Combine(_root, "in"), Input = true },
                [_out] = new() { Name = "Outbound " + suffix, Path = Path.Combine(_root, "out"), Output = true },
            },
        }));
    }

    public void Dispose()
    {
        _client.Dispose();
        _store.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private JsonObject Trigger(string sourceSubfolder = "", string pattern = "*.json", string? source = null) => new()
    {
        ["id"] = "trigger", ["type"] = "folder-input", ["title"] = "AP folder", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 80 },
        ["config"] = new JsonObject
        {
            ["sourceLocation"] = source ?? _in, ["sourceSubfolder"] = sourceSubfolder, ["pattern"] = pattern, ["includeSubfolders"] = false,
            ["interval"] = "every-minute", ["processedLocation"] = _out, ["processedSubfolder"] = "processed",
            ["failedLocation"] = _out, ["failedSubfolder"] = "failed",
        },
    };

    /// <summary>Reports this test's locations (as the Worker does at startup) and publishes a folder workflow.</summary>
    private async Task<string> PublishAsync(JsonObject? trigger = null, JsonNode? rules = null)
    {
        await WithDb(db => Ingestion(db).SyncLocationsAsync(CancellationToken.None));
        return await _kit.PublishInvoiceWorkflowAsync(rules, trigger ?? Trigger());
    }

    private FolderIngestion Ingestion(VersoDbContext db, TimeSpan? clockOffset = null, FolderIngestionOptions? options = null) =>
        new(db, _folders, new ExecutionService(db, _jobs, new WorkerUser(), TimeProvider.System, NullLogger<ExecutionService>.Instance),
            options ?? new FolderIngestionOptions { SettleTime = TimeSpan.Zero }, new ShiftedClock(clockOffset ?? TimeSpan.Zero), NullLogger<FolderIngestion>.Instance);

    /// <summary>One pass of the Worker's folder-input loop.</summary>
    private Task<FolderPassResult> PassAsync(TimeSpan? clockOffset = null, FolderIngestionOptions? options = null) =>
        WithDb(db => Ingestion(db, clockOffset, options).RunOnceAsync(CancellationToken.None));

    private async Task RunQueuedExecutionsAsync()
    {
        while (_jobs.Enqueued.TryDequeue(out var id))
        {
            await _kit.RunEngineAsync(id, _printer, _store, _jobs);
        }
    }

    private async Task WithDb(Func<VersoDbContext, Task> action)
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<VersoDbContext>());
    }

    private async Task<T> WithDb<T>(Func<VersoDbContext, Task<T>> action)
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<VersoDbContext>());
    }

    private void Drop(string name, string content, string folder = "in")
    {
        var path = Path.Combine(_root, folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-5));
    }

    private string[] FilesIn(string relative) =>
        Directory.Exists(Path.Combine(_root, relative))
            ? [.. Directory.GetFiles(Path.Combine(_root, relative)).Select(Path.GetFileName).Order(StringComparer.Ordinal)!]
            : [];

    private Task<List<FolderFile>> RowsAsync(string workflowKey) =>
        WithDb(db => db.FolderFiles.AsNoTracking().Where(f => f.WorkflowKey == workflowKey).OrderBy(f => f.FileName).ToListAsync());

    private async Task<JsonElement> StatusAsync(string workflowKey)
    {
        var id = await WithDb(db => db.Workflows.Where(w => w.Key == workflowKey).Select(w => w.Id).SingleAsync());
        using var response = await _client.GetAsync(new Uri($"/api/v1/workflows/{id}/folder-input", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ExecutionTestKit.ReadAsync(response);
    }

    [Fact]
    public async Task ValidFile_StartsARun_AndMovesToProcessed()
    {
        var key = await PublishAsync();
        Drop("inv-001.json", ExecutionTestKit.Fixture("invoice/sample.json"));

        await PassAsync();
        var row = Assert.Single(await RowsAsync(key));
        Assert.Equal(FolderFileStatus.Submitted, row.Status);
        Assert.Empty(FilesIn("in")); // in Verso's working folder now
        var execution = await _kit.GetExecutionAsync(row.ExecutionId!.Value);
        Assert.Equal(FolderIngestion.ActorId, execution.GetProperty("submittedBy").GetString());
        Assert.Equal(row.Id.ToString("N"), execution.GetProperty("correlationId").GetString());

        await RunQueuedExecutionsAsync();
        await PassAsync();

        row = Assert.Single(await RowsAsync(key));
        Assert.Equal(FolderFileStatus.Completed, row.Status);
        Assert.Equal(FolderFileOutcome.Processed, row.Outcome);
        Assert.Equal(["inv-001.json"], FilesIn("out/processed"));
        Assert.Empty(FilesIn("in/.verso-inflight"));
        Assert.Equal("Succeeded", (await _kit.GetExecutionAsync(row.ExecutionId!.Value)).GetProperty("status").GetString());

        var status = await StatusAsync(key);
        Assert.Equal("Ok", status.GetProperty("watch").GetProperty("lastStatus").GetString());
        var file = Assert.Single(status.GetProperty("files").EnumerateArray());
        Assert.Equal("Processed", file.GetProperty("outcome").GetString());
        Assert.Equal(row.ExecutionId, file.GetProperty("executionId").GetGuid());
    }

    [Fact]
    public async Task RejectedRun_MovesToFailed_WithAnErrorFile()
    {
        var key = await PublishAsync();
        Drop("broken.json", ExecutionTestKit.Fixture("invoice/broken.json"));

        await PassAsync();
        await RunQueuedExecutionsAsync();
        await PassAsync();

        var row = Assert.Single(await RowsAsync(key));
        Assert.Equal(FolderFileOutcome.Failed, row.Outcome);
        Assert.Equal("data.invalid", row.ErrorCode);
        Assert.Equal(["broken.json", "broken.json.error.json"], FilesIn("out/failed"));
        using var error = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "out", "failed", "broken.json.error.json")));
        Assert.Equal("data.invalid", error.RootElement.GetProperty("code").GetString());
        Assert.Equal(row.ExecutionId, error.RootElement.GetProperty("executionId").GetGuid());
        Assert.DoesNotContain(_root, File.ReadAllText(Path.Combine(_root, "out", "failed", "broken.json.error.json")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnreadableContent_FailsWithoutARun()
    {
        var key = await PublishAsync(Trigger(pattern: "*"));
        Drop("not-json.json", "{ this isn't json");
        Drop("array.json", "[1, 2]");
        Drop("invoice.xml", "<invoice/>");

        await PassAsync();

        var rows = await RowsAsync(key);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal((FolderFileStatus.Completed, FolderFileOutcome.Failed, "file.invalid_json", (Guid?)null), (r.Status, r.Outcome, r.ErrorCode, r.ExecutionId)));
        Assert.Contains("XML and CSV", rows.Single(r => r.FileName == "invoice.xml").ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(_jobs.Enqueued);
        Assert.Equal(6, FilesIn("out/failed").Length); // three files and three error files
    }

    [Fact]
    public async Task TooLargeFiles_FailUnread()
    {
        var key = await PublishAsync();
        Drop("big.json", "{\"x\":\"" + new string('a', 2000) + "\"}");

        await PassAsync(options: new FolderIngestionOptions { SettleTime = TimeSpan.Zero, MaxFileBytes = 1000 });

        Assert.Equal("file.too_large", Assert.Single(await RowsAsync(key)).ErrorCode);
    }

    [Fact]
    public async Task IncompleteFiles_AreLeftAlone()
    {
        var key = await PublishAsync();
        Drop("inv.json.tmp", ExecutionTestKit.Fixture("invoice/sample.json"));       // still being written (temporary name)
        Drop(".inv.json", ExecutionTestKit.Fixture("invoice/sample.json"));
        File.WriteAllText(Path.Combine(_root, "in", "fresh.json"), ExecutionTestKit.Fixture("invoice/sample.json")); // just changed

        await PassAsync(options: new FolderIngestionOptions { SettleTime = TimeSpan.FromMinutes(5) });

        Assert.Empty(await RowsAsync(key));
        Assert.Equal([".inv.json", "fresh.json", "inv.json.tmp"], FilesIn("in"));
    }

    [Fact]
    public async Task SameData_RunsOnce_TheCopyGoesToFailedAsADuplicate()
    {
        var key = await PublishAsync();
        var data = ExecutionTestKit.Fixture("invoice/sample.json");
        Drop("a.json", data);
        Drop("b.json", data.Replace("\n", "\r\n", StringComparison.Ordinal)); // same data, different bytes

        await PassAsync();

        var rows = await RowsAsync(key);
        Assert.Single(_jobs.Enqueued);
        Assert.Single(rows, r => r.Status == FolderFileStatus.Submitted);
        var duplicate = Assert.Single(rows, r => r.ErrorCode == "file.duplicate");
        Assert.Contains(duplicate.FileName, FilesIn("out/failed"));

        // Dropped again later, after the first run finished: still not run again.
        await RunQueuedExecutionsAsync();
        Drop("a-again.json", data);
        await PassAsync(TimeSpan.FromMinutes(2));
        Assert.Empty(_jobs.Enqueued);
        Assert.Equal("file.duplicate", (await RowsAsync(key)).Single(r => r.FileName == "a-again.json").ErrorCode);
    }

    [Fact]
    public async Task TwoWorkers_ClaimEachFileOnce()
    {
        var key = await PublishAsync();
        for (var i = 0; i < 8; i++)
        {
            var data = JsonNode.Parse(ExecutionTestKit.Fixture("invoice/sample.json"))!;
            data["invoice"]!["number"] = $"INV-RACE-{i}";
            Drop($"race-{i}.json", data.ToJsonString());
        }

        await Task.WhenAll(PassAsync(), PassAsync(), PassAsync());
        await PassAsync(TimeSpan.FromMinutes(2)); // whatever a lost race left behind is picked up

        var rows = await RowsAsync(key);
        Assert.Equal(8, rows.Count);
        Assert.Equal(8, rows.Select(r => r.ExecutionId).Distinct().Count());
        Assert.Equal(8, _jobs.Enqueued.Distinct().Count());
        Assert.Empty(FilesIn("in"));
    }

    [Fact]
    public async Task TheDatabase_RefusesASecondClaimOfTheSameFile()
    {
        await WithDb(async db =>
        {
            FolderFile Claim() => FolderFile.Claim(Guid.NewGuid(), "wf", "trigger", _in, "", "same.json", "same.json", 2, DateTimeOffset.UtcNow,
                _out, "p", _out, "f", "test", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2));
            db.FolderFiles.Add(Claim());
            await db.SaveChangesAsync();
            db.FolderFiles.Add(Claim());
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        });
    }

    [Fact]
    public async Task AWorkerThatStopsAfterClaiming_IsTakenOverAfterTheLease()
    {
        var key = await PublishAsync();
        Drop("crash.json", ExecutionTestKit.Fixture("invoice/sample.json"));
        var workflowId = await WithDb(db => db.Workflows.Where(w => w.Key == key).Select(w => w.Id).SingleAsync());

        // What a Worker did before it died: the claim row and the move into the working folder.
        var file = FolderFile.Claim(workflowId, key, "trigger", _in, "", "crash.json", "crash.json", 10, DateTimeOffset.UtcNow,
            _out, "processed", _out, "failed", "dead-worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2));
        await WithDb(async db => { db.FolderFiles.Add(file); await db.SaveChangesAsync(); });
        Assert.True(_folders.TryMoveToInFlight(new FolderRef(_in, ""), "crash.json", file.Id));

        await PassAsync();
        Assert.Equal(FolderFileStatus.Claimed, Assert.Single(await RowsAsync(key)).Status); // the lease is still held

        await PassAsync(TimeSpan.FromMinutes(3));
        var row = Assert.Single(await RowsAsync(key));
        Assert.Equal(FolderFileStatus.Submitted, row.Status);
        Assert.Single(_jobs.Enqueued);
    }

    [Fact]
    public async Task AWorkerThatStopsAfterSubmitting_IsResumedWithTheSameRun()
    {
        var key = await PublishAsync();
        var data = ExecutionTestKit.Fixture("invoice/sample.json");
        Drop("crash2.json", data);
        var workflowId = await WithDb(db => db.Workflows.Where(w => w.Key == key).Select(w => w.Id).SingleAsync());
        var file = FolderFile.Claim(workflowId, key, "trigger", _in, "", "crash2.json", "crash2.json", 10, DateTimeOffset.UtcNow,
            _out, "processed", _out, "failed", "dead-worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2));
        await WithDb(async db => { db.FolderFiles.Add(file); await db.SaveChangesAsync(); });
        _folders.TryMoveToInFlight(new FolderRef(_in, ""), "crash2.json", file.Id);

        // ...and it had submitted the run, but died before recording it on the file.
        using var doc = JsonDocument.Parse(data);
        var first = await WithDb(db => new ExecutionService(db, _jobs, new WorkerUser(), TimeProvider.System, NullLogger<ExecutionService>.Instance)
            .SubmitAsync(key, "folder:" + CanonicalJson.Sha256Hex(doc.RootElement), null, new SubmitExecutionRequest(doc.RootElement), CancellationToken.None));
        Assert.True(first.IsSuccess);

        await PassAsync(TimeSpan.FromMinutes(3));

        var row = Assert.Single(await RowsAsync(key));
        Assert.Equal(FolderFileStatus.Submitted, row.Status);
        Assert.Equal(first.Value!.Execution.ExecutionId, row.ExecutionId); // found again, not a second run
        Assert.Single(_jobs.Enqueued);
    }

    [Fact]
    public async Task UnreachableDestination_IsRetried_UntilItWorks()
    {
        var key = await PublishAsync();
        File.WriteAllText(Path.Combine(_root, "out", "failed"), "a file where the failed folder should be");
        Drop("bad.json", "not json");

        await PassAsync();
        var row = Assert.Single(await RowsAsync(key));
        Assert.Equal(FolderFileStatus.Delivering, row.Status);
        Assert.StartsWith("folder.", row.DeliveryError, StringComparison.Ordinal);

        File.Delete(Path.Combine(_root, "out", "failed"));
        await PassAsync();
        row = Assert.Single(await RowsAsync(key));
        Assert.Equal(FolderFileStatus.Completed, row.Status);
        Assert.Null(row.DeliveryError);
        Assert.Contains("bad.json", FilesIn("out/failed"));
    }

    [Fact]
    public async Task UnavailableFolder_IsShownOnTheWorkflow()
    {
        var key = await PublishAsync(Trigger(sourceSubfolder: "not-there"));

        await PassAsync();

        var watch = (await StatusAsync(key)).GetProperty("watch");
        Assert.Equal("Unavailable", watch.GetProperty("lastStatus").GetString());
        var error = watch.GetProperty("lastError").GetString()!;
        Assert.Contains(_in + "/not-there", error, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Scans_FollowTheInterval()
    {
        var key = await PublishAsync();
        await PassAsync();
        Drop("later.json", ExecutionTestKit.Fixture("invoice/sample.json").Replace("INV-", "INV-LATER-", StringComparison.Ordinal));

        await PassAsync(TimeSpan.FromSeconds(20)); // within the minute: not scanned again
        Assert.Empty(await RowsAsync(key));

        await PassAsync(TimeSpan.FromMinutes(1));
        Assert.Single(await RowsAsync(key));
    }

    [Fact]
    public async Task Publishing_RefusesUnapprovedFolders()
    {
        await WithDb(db => Ingestion(db).SyncLocationsAsync(CancellationToken.None));
        var workflow = await CreateWorkflowWithTriggerAsync(Trigger(source: "c-drive"));

        using var validate = await _client.PostAsync(new Uri($"/api/v1/workflows/{workflow}/draft/validate", UriKind.Relative), null);
        var issues = (await ExecutionTestKit.ReadAsync(validate)).GetProperty("issues").EnumerateArray()
            .Select(i => i.GetProperty("code").GetString()).ToList();
        Assert.Contains("folder.location_unknown", issues);
    }

    [Fact]
    public async Task Locations_AreListedWithoutPaths()
    {
        await WithDb(db => Ingestion(db).SyncLocationsAsync(CancellationToken.None));

        using var response = await _client.GetAsync(new Uri("/api/v1/folder-locations", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"\"key\":\"{_in}\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain(_root.Replace("\\", "\\\\", StringComparison.Ordinal), body, StringComparison.OrdinalIgnoreCase);
        var location = (await ExecutionTestKit.ReadAsync(response)).EnumerateArray().Single(l => l.GetProperty("key").GetString() == _in);
        Assert.True(location.GetProperty("input").GetBoolean());
        Assert.False(location.GetProperty("output").GetBoolean());
        Assert.True(location.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task TestAccess_IsRunByTheWorker_AsItsIdentity()
    {
        await WithDb(db => Ingestion(db).SyncLocationsAsync(CancellationToken.None));

        using var request = await _client.PostAsJsonAsync(new Uri("/api/v1/folder-access-tests", UriKind.Relative), new
        {
            targets = new object[]
            {
                new { role = "source", location = _in, subfolder = "" },
                new { role = "processed", location = _out, subfolder = "processed" },
                new { role = "failed", location = _out, subfolder = "failed" },
            },
        });
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        var id = (await ExecutionTestKit.ReadAsync(request)).GetProperty("id").GetGuid();
        Assert.Equal($"/api/v1/folder-access-tests/{id}", request.Headers.Location!.ToString());

        await WithDb(db => new FolderAccessTestRunner(db, _folders, TimeProvider.System).RunAsync(id, CancellationToken.None));

        using var result = await _client.GetAsync(new Uri($"/api/v1/folder-access-tests/{id}", UriKind.Relative));
        var test = await ExecutionTestKit.ReadAsync(result);
        Assert.Equal("Completed", test.GetProperty("status").GetString());
        Assert.Equal(_folders.Identity, test.GetProperty("identity").GetString());
        Assert.All(test.GetProperty("results").EnumerateArray(), r => Assert.True(r.GetProperty("ok").GetBoolean(), r.ToString()));
    }

    [Fact]
    public async Task TestAccess_RefusesUnapprovedTargets()
    {
        await WithDb(db => Ingestion(db).SyncLocationsAsync(CancellationToken.None));

        using var response = await _client.PostAsJsonAsync(new Uri("/api/v1/folder-access-tests", UriKind.Relative), new
        {
            targets = new object[]
            {
                new { role = "source", location = _out, subfolder = "" },          // output only
                new { role = "processed", location = "c-drive", subfolder = "" },  // not approved
                new { role = "failed", location = _out, subfolder = "../../etc" },  // escapes
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await ExecutionTestKit.ReadAsync(response)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("targets[0].location", out _));
        Assert.True(errors.TryGetProperty("targets[1].location", out _));
        Assert.True(errors.TryGetProperty("targets[2].subfolder", out _));
    }

    [Fact]
    public async Task WithdrawnLocations_AreDisabledNotDeleted()
    {
        await WithDb(db => Ingestion(db).SyncLocationsAsync(CancellationToken.None));
        var empty = new LocalFolderAccess(Options.Create(new FolderAccessOptions()));
        await WithDb(db => new FolderIngestion(db, empty, null!, new FolderIngestionOptions(), TimeProvider.System, NullLogger<FolderIngestion>.Instance)
            .SyncLocationsAsync(CancellationToken.None));

        var location = await WithDb(db => db.FolderLocations.AsNoTracking().SingleAsync(l => l.Key == _in));
        Assert.False(location.Enabled);
        Assert.Contains("Not configured", location.Problem, StringComparison.Ordinal);
    }

    private async Task<Guid> CreateWorkflowWithTriggerAsync(JsonObject trigger)
    {
        using var create = await _client.PostAsJsonAsync(new Uri("/api/v1/workflows", UriKind.Relative),
            new { key = "fld-" + Guid.NewGuid().ToString("N")[..12], name = "Folder" });
        var id = (await ExecutionTestKit.ReadAsync(create)).GetProperty("id").GetGuid();
        using var draft = await _client.GetAsync(new Uri($"/api/v1/workflows/{id}/draft", UriKind.Relative));
        var save = new HttpRequestMessage(HttpMethod.Put, new Uri($"/api/v1/workflows/{id}/draft", UriKind.Relative))
        {
            Content = JsonContent.Create(new JsonObject { ["definition"] = new JsonObject { ["nodes"] = new JsonArray(trigger), ["edges"] = new JsonArray() } }),
        };
        save.Headers.TryAddWithoutValidation("If-Match", draft.Headers.ETag!.ToString());
        using var saved = await _client.SendAsync(save);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        return id;
    }

    /// <summary>The system clock moved forward, to let leases and intervals pass without waiting.</summary>
    private sealed class ShiftedClock(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
    }
}
