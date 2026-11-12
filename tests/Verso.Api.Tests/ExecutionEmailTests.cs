using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Verso.Api.Tests;

/// <summary>The Generate HTML email step in a real run: no template, one HTML file, same rejection, storage and archive as the other document steps.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ExecutionEmailTests(MigratedDatabaseFixture fixture) : IDisposable
{
    private readonly HttpClient _client = fixture.Factory.CreateClient();
    private readonly TestDocumentStore _store = new();
    private readonly FakePdfPrinter _printer = new();
    private readonly RecordingExecutionJobs _jobs = new();
    private ExecutionTestKit Kit => new(fixture.Factory, _client);

    private static JsonNode EmailConfig(string subject = "Invoice {{invoice.number}}", string body = "Dear {{customer.name}},\n\nYour invoice is ready.", string url = "https://pay.example.test/{{invoice.number}}") =>
        JsonNode.Parse($$$"""
            { "layout": "invoice", "subject": {{{JsonValue.Create(subject)!.ToJsonString()}}}, "preheader": "", "body": {{{JsonValue.Create(body)!.ToJsonString()}}},
              "buttonLabel": "View invoice", "buttonUrl": {{{JsonValue.Create(url)!.ToJsonString()}}}, "footer": "", "fileName": "{{invoice.number}}.html", "darkMode": true }
            """)!;

    public void Dispose()
    {
        _store.Dispose();
        _client.Dispose();
    }

    [Fact]
    public async Task ValidData_ProducesAnArchivedHtmlEmail_WithoutATemplateOrChromium()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(emailConfig: EmailConfig());
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(0, _printer.Calls);
        Assert.False(done.GetProperty("templateVersions").TryGetProperty("pdf", out _)); // nothing is pinned for an email step
        var document = Assert.Single(done.GetProperty("documents").EnumerateArray());
        Assert.Equal("INV-2026-003901.html", document.GetProperty("fileName").GetString());
        Assert.Equal("text/html", document.GetProperty("contentType").GetString());
        Assert.Equal(JsonValueKind.Null, document.GetProperty("templateVersionId").ValueKind);
        Assert.Equal("Archived", document.GetProperty("status").GetString());

        var file = Assert.Single(_store.Files("archive"));
        Assert.EndsWith(".html", file, StringComparison.Ordinal);
        var bytes = await File.ReadAllBytesAsync(file);
        Assert.Equal(document.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var html = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("Invoice INV-2026-003901", html, StringComparison.Ordinal);
        Assert.Contains("https://pay.example.test/INV-2026-003901", html, StringComparison.Ordinal);
        Assert.Empty(_store.Files("staging"));

        // The step result is history: it records the file, never the message text.
        var step = done.GetProperty("steps")[2];
        Assert.Equal("generate-email", step.GetProperty("type").GetString());
        Assert.DoesNotContain("Dear", step.GetProperty("result").GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchivedEmail_IsListedAsHtml_WithoutATemplateVersion()
    {
        // The download headers (attachment, nosniff) are set for every document type by the content endpoint and covered by the PDF and DOCX tests;
        // a second test host here would leave Hangfire's static logger pointing at a disposed host for later test classes.
        var key = await Kit.PublishInvoiceWorkflowAsync(emailConfig: EmailConfig());
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);
        await Kit.RunEngineAsync(id, _printer, _store, _jobs);
        var documentId = (await Kit.GetExecutionAsync(id)).GetProperty("documents")[0].GetProperty("id").GetGuid();

        using var response = await _client.GetAsync(new Uri($"/api/v1/documents/{documentId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await ExecutionTestKit.ReadAsync(response);
        Assert.Equal("text/html", document.GetProperty("contentType").GetString());
        Assert.Equal("INV-2026-003901.html", document.GetProperty("fileName").GetString());
        Assert.Equal(JsonValueKind.Null, document.GetProperty("templateVersionId").ValueKind);
    }

    [Fact]
    public async Task DataThatCantFillTheEmail_IsRejected_AtTheEmailStep()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(JsonNode.Parse("""{ "required": ["invoice.number"], "types": [] }"""), emailConfig: EmailConfig());
        var data = ExecutionTestKit.Sample;
        data["customer"]!.AsObject().Remove("name");
        var id = await Kit.SubmitNewAsync(key, data);

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Rejected", e.GetProperty("status").GetString());
        Assert.Equal("document.data_incomplete", e.GetProperty("failureCode").GetString());
        var step = e.GetProperty("steps")[2];
        Assert.Equal("failed", step.GetProperty("outputPort").GetString());
        Assert.Contains(step.GetProperty("result").GetProperty("issues").EnumerateArray(), i => i.GetProperty("path").GetString() == "customer.name");
        Assert.Empty(e.GetProperty("documents").EnumerateArray());
    }

    [Fact]
    public async Task ALinkMadeFromDataThatIsNotHttp_IsRejected()
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(emailConfig: EmailConfig(url: "{{invoice.number}}"));
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample); // resolves to "INV-2026-003901", not an address

        await Kit.RunEngineAsync(id, _printer, _store, _jobs);

        var e = await Kit.GetExecutionAsync(id);
        Assert.Equal("Rejected", e.GetProperty("status").GetString());
        Assert.Contains(e.GetProperty("steps")[2].GetProperty("result").GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "email.url_invalid");
    }
}
