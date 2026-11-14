using System.Text.Json;

namespace Verso.Api.Tests;

/// <summary>The Parallel branches step in a real run (ADR 0009): both paths run, Branch A then Branch B, and a resumed run doesn't repeat finished steps.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ExecutionParallelBranchesTests(MigratedDatabaseFixture fixture) : IDisposable
{
    private readonly HttpClient _client = fixture.Factory.CreateClient();
    private readonly TestDocumentStore _store = new();
    private readonly FakePdfPrinter _printer = new();
    private readonly RecordingExecutionJobs _jobs = new();
    private ExecutionTestKit Kit => new(fixture.Factory, _client);

    public void Dispose()
    {
        _store.Dispose();
        _client.Dispose();
    }

    private static JsonElement Step(JsonElement execution, string nodeId) =>
        execution.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("nodeId").GetString() == nodeId);

    [Fact]
    public async Task BothPaths_RunAndTheRunSucceeds()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(parallelBranches: true);
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", e.GetProperty("status").GetString());
        Assert.Equal("branch-a", Step(e, "fork").GetProperty("outputPort").GetString());
        foreach (var node in new[] { "pdf", "archive", "pdf2", "archive2" })
        {
            Assert.Equal("Succeeded", Step(e, node).GetProperty("status").GetString());
        }

        var documents = e.GetProperty("documents").EnumerateArray().ToList();
        Assert.Equal(2, documents.Count);
        Assert.All(documents, d => Assert.Equal(JsonValueKind.Object, d.GetProperty("archive").ValueKind)); // each path archived its own document
        Assert.Equal(2, documents.Select(d => d.GetProperty("fileName").GetString()).Distinct().Count());
    }

    [Fact]
    public async Task ResumedRun_DoesNotRepeatFinishedSteps()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(parallelBranches: true);
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);
        _store.FailWrites = true;

        await Kit.RunEngineAsync(id, _printer, _store, _jobs); // the first PDF step fails once and a retry is scheduled
        _store.FailWrites = false;
        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(1, Step(done, "fork").GetProperty("attempt").GetInt32());
        Assert.Equal(2, done.GetProperty("documents").GetArrayLength());
        foreach (var node in new[] { "pdf", "archive", "pdf2", "archive2" })
        {
            Assert.Equal("Succeeded", Step(done, node).GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task JoinedPaths_RunBothThenTheStepsAfterTheJoin()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(parallelBranches: true, joinBranches: true);
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", e.GetProperty("status").GetString());
        foreach (var node in new[] { "pdf", "pdf2", "join", "archive" })
        {
            Assert.Equal("Succeeded", Step(e, node).GetProperty("status").GetString());
        }

        Assert.Equal("out", Step(e, "join").GetProperty("outputPort").GetString());
        var documents = e.GetProperty("documents").EnumerateArray().ToList();
        Assert.Equal(2, documents.Count);
        // Branch B ran last, so the archive step after the join stored its document and Branch A's stays unarchived.
        var archived = documents.Single(d => d.GetProperty("archive").ValueKind == JsonValueKind.Object);
        Assert.Equal("pdf2", archived.GetProperty("nodeId").GetString());
    }

    [Fact]
    public async Task JoinedPaths_ResumedRun_DoesNotRepeatFinishedSteps()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(parallelBranches: true, joinBranches: true);
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);
        _store.FailWrites = true;

        await Kit.RunEngineAsync(id, _printer, _store, _jobs); // the first PDF step fails once and a retry is scheduled
        _store.FailWrites = false;
        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(1, Step(done, "join").GetProperty("attempt").GetInt32());
        Assert.Equal("Succeeded", Step(done, "archive").GetProperty("status").GetString());
    }
}
