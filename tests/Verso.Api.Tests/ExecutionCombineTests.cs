using System.Text.Json;
using System.Text.Json.Nodes;

namespace Verso.Api.Tests;

/// <summary>The Combine PDFs step in a real run: it joins the PDFs the run generated earlier into one new document, which Archive then stores.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ExecutionCombineTests(MigratedDatabaseFixture fixture) : IDisposable
{
    private readonly HttpClient _client = fixture.Factory.CreateClient();
    private readonly TestDocumentStore _store = new();
    private readonly FakePdfPrinter _printer = new();
    private readonly FakePdfCombiner _combiner = new();
    private readonly RecordingExecutionJobs _jobs = new();
    private ExecutionTestKit Kit => new(fixture.Factory, _client);

    private static JsonObject Combine(string order = "generated", bool bookmarks = true) =>
        new JsonObject { ["order"] = order, ["bookmarks"] = bookmarks, ["fileName"] = "{{invoice.number}}-pack.pdf" };

    public void Dispose()
    {
        _store.Dispose();
        _client.Dispose();
    }

    [Fact]
    public async Task TwoGeneratedPdfs_BecomeOneArchivedPack_InTheOrderTheyWereMade()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(combineConfig: Combine());
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);

        await Kit.RunEngineAsync(id, _printer, _store, _jobs, combiner: _combiner);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(2, _printer.Calls);
        Assert.Equal(1, _combiner.Calls);
        Assert.Equal(["INV-2026-003901.pdf", "INV-2026-003901-copy.pdf"], _combiner.LastTitles);
        Assert.Equal(true, _combiner.LastBookmarks);

        var documents = done.GetProperty("documents").EnumerateArray().ToList();
        Assert.Equal(3, documents.Count); // two sources and the pack
        var pack = Assert.Single(documents, d => d.GetProperty("nodeId").GetString() == "combine");
        Assert.Equal("INV-2026-003901-pack.pdf", pack.GetProperty("fileName").GetString());
        Assert.Equal("application/pdf", pack.GetProperty("contentType").GetString());
        Assert.Equal(JsonValueKind.Null, pack.GetProperty("templateVersionId").ValueKind);
        Assert.Equal("Archived", pack.GetProperty("status").GetString()); // Archive stores the document the path ends with
        Assert.Single(documents, d => d.GetProperty("status").GetString() == "Archived");
        Assert.Single(_store.Files("archive"));

        var step = done.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("nodeId").GetString() == "combine");
        Assert.Equal(2, step.GetProperty("result").GetProperty("documents").GetInt32());
        Assert.Equal(2, step.GetProperty("result").GetProperty("pages").GetInt32());
    }

    [Fact]
    public async Task ReverseOrderAndNoBookmarks_AreHonoured()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(combineConfig: Combine(order: "reverse", bookmarks: false));
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);

        await Kit.RunEngineAsync(id, _printer, _store, _jobs, combiner: _combiner);

        Assert.Equal("Succeeded", (await Kit.GetExecutionAsync(id)).GetProperty("status").GetString());
        Assert.Equal(["INV-2026-003901-copy.pdf", "INV-2026-003901.pdf"], _combiner.LastTitles);
        Assert.Equal(false, _combiner.LastBookmarks);
    }

    [Fact]
    public async Task FewerThanTwoPdfs_IsRejected_AtTheCombineStep()
    {
        // Both earlier document steps are Word steps, so there is no PDF to combine.
        var key = await Kit.PublishInvoiceWorkflowAsync(docxEditing: "allow", combineConfig: Combine());
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);

        await Kit.RunEngineAsync(id, _printer, _store, _jobs, combiner: _combiner);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Rejected", e.GetProperty("status").GetString());
        Assert.Equal("combine.not_enough_documents", e.GetProperty("failureCode").GetString());
        var step = e.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("nodeId").GetString() == "combine");
        Assert.Equal("failed", step.GetProperty("outputPort").GetString());
        Assert.Equal(0, _combiner.Calls);
        Assert.Empty(_store.Files("archive"));
    }

    [Fact]
    public async Task ASourceFileChangedInStorage_FailsTheStep_WithoutCombining()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(combineConfig: Combine());
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);
        _store.AfterWrite = _ => // tamper with the first staged PDF once the second exists
        {
            var staged = _store.Files("staging").OrderBy(p => p, StringComparer.Ordinal).ToList();
            if (staged.Count == 2)
            {
                File.WriteAllText(staged[0], "tampered");
            }
        };

        await Kit.RunEngineAsync(id, _printer, _store, _jobs, combiner: _combiner);

        var e = await Kit.GetExecutionAsync(id);
        Assert.NotEqual("Succeeded", e.GetProperty("status").GetString());
        Assert.Equal(0, _combiner.Calls);
        Assert.Empty(_store.Files("archive"));
    }
}
