using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Verso.Rendering.Docx;

namespace Verso.Api.Tests;

/// <summary>The Generate DOCX step in a real run: same pinning, rejection, storage and archive as the PDF step, with a Word file as the result.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ExecutionDocxTests(MigratedDatabaseFixture fixture) : IDisposable
{
    private const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private readonly HttpClient _client = fixture.Factory.CreateClient();
    private readonly TestDocumentStore _store = new();
    private readonly FakePdfPrinter _printer = new();
    private readonly FakeDocxWriter _docx = new();
    private readonly RecordingExecutionJobs _jobs = new();
    private ExecutionTestKit Kit => new(fixture.Factory, _client);

    /// <summary>An API host that serves documents from this test's store.</summary>
    private HttpClient StoreApi() => fixture.Factory.WithWebHostBuilder(b => b.UseSetting("Documents:RootPath", _store.Root)).CreateClient();

    public void Dispose()
    {
        _store.Dispose();
        _client.Dispose();
    }

    [Fact]
    public async Task ValidData_ProducesAnArchivedWordDocument_WithoutUsingChromium()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(docxEditing: "readOnly");
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);

        await Kit.RunEngineAsync(id, _printer, _store, _jobs, docx: _docx);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(0, _printer.Calls);
        Assert.Equal(1, _docx.Calls);
        Assert.True(done.GetProperty("templateVersions").TryGetProperty("pdf", out _)); // the step is pinned like a PDF step
        var document = Assert.Single(done.GetProperty("documents").EnumerateArray());
        Assert.Equal("INV-2026-003901.docx", document.GetProperty("fileName").GetString());
        Assert.Equal(DocxType, document.GetProperty("contentType").GetString());
        Assert.Equal("Archived", document.GetProperty("status").GetString());

        var file = Assert.Single(_store.Files("archive"));
        Assert.EndsWith(".docx", file, StringComparison.Ordinal);
        Assert.Equal(document.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(file))));
        Assert.Empty(_store.Files("staging"));
        Assert.Contains("ReadOnly", System.Text.Encoding.ASCII.GetString(await File.ReadAllBytesAsync(file)), StringComparison.Ordinal); // the chosen restriction reached the writer
    }

    [Fact]
    public async Task ArchivedWordDocument_IsDownloadedWithItsOwnContentType()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(docxEditing: "allow");
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);
        await Kit.RunEngineAsync(id, _printer, _store, _jobs, docx: _docx);
        var done = await Kit.GetExecutionAsync(id);
        var documentId = done.GetProperty("documents")[0].GetProperty("id").GetGuid();

        using var api = StoreApi();
        using var response = await api.GetAsync(new Uri($"/api/v1/documents/{documentId}/content", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(DocxType, response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("INV-2026-003901.docx", response.Content.Headers.ContentDisposition!.FileName);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task DataThatCantMakeACompleteDocument_IsRejected_AtTheGenerateDocxStep()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(JsonNode.Parse("""{ "required": ["invoice.number"], "types": [] }"""), docxEditing: "allow");
        var data = ExecutionTestKit.Sample;
        data["customer"]!.AsObject().Remove("name");
        var id = await Kit.SubmitNewAsync(key, data);

        await Kit.RunEngineAsync(id, _printer, _store, _jobs, docx: _docx);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Rejected", e.GetProperty("status").GetString());
        Assert.Equal("document.data_incomplete", e.GetProperty("failureCode").GetString());
        var step = e.GetProperty("steps")[2];
        Assert.Equal("generate-docx", step.GetProperty("type").GetString());
        Assert.Equal("failed", step.GetProperty("outputPort").GetString());
        Assert.Contains(step.GetProperty("result").GetProperty("issues").EnumerateArray(), i => i.GetProperty("path").GetString() == "customer.name");
        Assert.Equal(0, _docx.Calls);
        Assert.Empty(e.GetProperty("documents").EnumerateArray());
    }

    [Fact]
    public async Task StorageOutage_IsRetried_ThenCompletesWithOneDocument()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(docxEditing: "trackedChanges");
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);
        _store.FailWrites = true;

        await Kit.RunEngineAsync(id, _printer, _store, _jobs, docx: _docx); // the step fails once and a retry is scheduled
        _store.FailWrites = false;
        await Kit.RunEngineAsync(id, _printer, _store, _jobs, docx: _docx);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Single(done.GetProperty("documents").EnumerateArray());
    }
}
