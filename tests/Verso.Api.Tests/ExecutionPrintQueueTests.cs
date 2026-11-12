using System.Text.Json;
using System.Text.Json.Nodes;
using Verso.Application.Executions;

namespace Verso.Api.Tests;

/// <summary>The Print queue step in a real run (ADR 0007): the PDF goes to the named queue's spool, once per step, and problems end the run the right way.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ExecutionPrintQueueTests(MigratedDatabaseFixture fixture) : IDisposable
{
    private readonly HttpClient _client = fixture.Factory.CreateClient();
    private readonly TestDocumentStore _store = new();
    private readonly FakePdfPrinter _printer = new();
    private readonly FakePrintSpooler _spooler = new();
    private readonly RecordingExecutionJobs _jobs = new();
    private ExecutionTestKit Kit => new(fixture.Factory, _client);

    private static JsonObject Print(string queue = "cyprus-print-nightly", string envelope = "c4", bool duplex = false) =>
        new() { ["queue"] = queue, ["envelope"] = envelope, ["duplex"] = duplex };

    public void Dispose()
    {
        _store.Dispose();
        _client.Dispose();
    }

    private async Task<JsonElement> RunAsync(JsonObject config)
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(printConfig: config);
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);
        await Kit.RunEngineAsync(id, _printer, _store, _jobs, spooler: _spooler);
        return await Kit.GetExecutionAsync(id);
    }

    [Fact]
    public async Task ThePdf_IsSpooledToTheNamedQueue_AndTheRunStillArchives()
    {
        var done = await RunAsync(Print());

        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        var job = Assert.Single(_spooler.Jobs);
        Assert.Equal("cyprus-print-nightly", job.Queue);
        Assert.Equal("c4", job.Envelope);
        Assert.False(job.Duplex);
        Assert.Equal("INV-2026-003901.pdf", job.FileName);
        Assert.Matches("^[0-9a-f]{32}$", job.JobId);
        Assert.Single(_store.Files("archive"));

        var step = done.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("nodeId").GetString() == "print");
        Assert.Equal("queued", step.GetProperty("outputPort").GetString());
        Assert.Equal(job.JobId, step.GetProperty("result").GetProperty("jobId").GetString());
    }

    [Fact]
    public async Task AnotherExecution_GetsAnotherJobId()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(printConfig: Print());
        var first = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);
        var second = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);

        await Kit.RunEngineAsync(first, _printer, _store, _jobs, spooler: _spooler);
        await Kit.RunEngineAsync(second, _printer, _store, _jobs, spooler: _spooler);

        Assert.Equal(2, _spooler.Jobs.Count);
        Assert.NotEqual(_spooler.Jobs[0].JobId, _spooler.Jobs[1].JobId);
    }

    [Fact]
    public async Task AQueueThatIsNotSetUp_FailsTheRun_WithoutRetry()
    {
        _spooler.Failure = new PrintSpoolException("print.queue_unknown", "not set up", retryable: false);

        var done = await RunAsync(Print());

        Assert.Equal("Failed", done.GetProperty("status").GetString());
        Assert.Equal("print.queue_unknown", done.GetProperty("failureCode").GetString());
        Assert.Empty(_jobs.Scheduled);
    }

    [Fact]
    public async Task AFolderThatCannotBeWritten_IsRetried()
    {
        _spooler.Failure = new PrintSpoolException("print.queue_unavailable", "folder down", retryable: true);

        var done = await RunAsync(Print());

        Assert.Equal("Running", done.GetProperty("status").GetString());
        Assert.NotEmpty(_jobs.Scheduled);
        Assert.Empty(_spooler.Jobs);
    }
}
