using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verso.Application.Templates;
using Verso.Infrastructure.Persistence;
using Verso.Rendering;

namespace Verso.Api.Tests;

/// <summary>
/// PDF preview pipeline through the real API and database: queue (API) → job (run here with a fake printer, as the
/// Worker would with Chromium; real printing is covered by Verso.Pdf.Tests) → status, content, ready notification.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class PdfPreviewApiTests(MigratedDatabaseFixture fixture)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client = fixture.Factory.CreateClient();

    [Fact]
    public async Task Queue_Returns202WithLocation_AndEnqueuesAHangfireJob()
    {
        var templateId = await CreateInvoiceTemplateAsync();

        using var response = await _client.PostAsync(PdfUri(templateId), null);
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var previewId = body.GetProperty("previewId").GetGuid();
        Assert.Equal($"/api/v1/previews/{previewId}", response.Headers.Location!.OriginalString);
        Assert.Equal("Queued", body.GetProperty("status").GetString());

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VersoDbContext>();
        var jobs = await db.Database.SqlQuery<int>(
            $"SELECT COUNT(*) AS [Value] FROM [hangfire].[Job] WHERE [InvocationData] LIKE '%PdfPreviewJob%' AND [Arguments] LIKE {"%" + previewId + "%"}").SingleAsync();
        Assert.Equal(1, jobs);
    }

    [Fact]
    public async Task RunningTheJob_StoresThePdf_ServedInlineAndUncached()
    {
        var templateId = await CreateInvoiceTemplateAsync();
        var previewId = await QueueAsync(templateId);

        await RunJobAsync(previewId, new FakePrinter());

        using var status = await _client.GetAsync(new Uri($"/api/v1/previews/{previewId}", UriKind.Relative));
        var body = await ReadAsync(status);
        Assert.Equal("Succeeded", body.GetProperty("status").GetString());
        Assert.Equal(FakePrinter.Pdf.Length, body.GetProperty("sizeBytes").GetInt32());
        Assert.Empty(body.GetProperty("issues").EnumerateArray());

        using var content = await _client.GetAsync(new Uri($"/api/v1/previews/{previewId}/content", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal("application/pdf", content.Content.Headers.ContentType!.MediaType);
        Assert.Equal(FakePrinter.Pdf, await content.Content.ReadAsByteArrayAsync());
        Assert.Equal("inline", content.Content.Headers.ContentDisposition!.DispositionType);
        Assert.True(content.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", content.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Content_BeforeTheJobRan_Is409NotReady()
    {
        var previewId = await QueueAsync(await CreateInvoiceTemplateAsync());

        using var response = await _client.GetAsync(new Uri($"/api/v1/previews/{previewId}/content", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("preview.not_ready", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Job_RendersTheSnapshotTakenAtRequestTime_NotLaterDraftEdits()
    {
        var templateId = await CreateInvoiceTemplateAsync();
        using var queue = await _client.PostAsJsonAsync(PdfUri(templateId), new { data = new { invoice = new { number = "INV-SNAPSHOT" } } });
        var previewId = (await ReadAsync(queue)).GetProperty("previewId").GetGuid();

        // Edit the draft after queueing: the title changes, but the preview must still show the old one.
        var layout = JsonNode.Parse(Fixture("invoice/layout.json"))!;
        layout["header"]!["elements"]![2]!["props"]!["text"] = "Changed later";
        using (var save = await PutDraftAsync(templateId, await DraftETagAsync(templateId), DraftBody(layout, JsonNode.Parse(Fixture("invoice/sample.json"))!)))
        {
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        }

        var printer = new FakePrinter();
        await RunJobAsync(previewId, printer);

        var printed = Assert.Single(printer.Printed);
        Assert.Contains("INV-SNAPSHOT", printed.HeaderTemplate, StringComparison.Ordinal);
        Assert.Contains(">Invoice<", printed.HeaderTemplate, StringComparison.Ordinal);
        Assert.DoesNotContain("Changed later", printed.HeaderTemplate, StringComparison.Ordinal);

        // Preview mode: missing values (the supplied data has no customer) are listed as issues.
        using var status = await _client.GetAsync(new Uri($"/api/v1/previews/{previewId}", UriKind.Relative));
        Assert.Contains((await ReadAsync(status)).GetProperty("issues").EnumerateArray(), i => i.GetProperty("path").GetString() == "customer.name");
    }

    [Fact]
    public async Task TechnicalFailures_AreRetried_ThenMarkedFailedOnTheLastAttempt()
    {
        var previewId = await QueueAsync(await CreateInvoiceTemplateAsync());
        var printer = new FakePrinter { FailuresBeforeSuccess = int.MaxValue };

        // Attempts 1 and 2 throw, so Hangfire schedules a retry; the preview goes back to Queued.
        for (var attempt = 1; attempt < PdfPreviewRunner.MaxAttempts; attempt++)
        {
            await Assert.ThrowsAsync<TimeoutException>(() => RunJobAsync(previewId, printer));
            var queued = await GetStatusAsync(previewId);
            Assert.Equal("Queued", queued.GetProperty("status").GetString());
        }

        // The last attempt records the failure instead of throwing.
        await RunJobAsync(previewId, printer);
        var failed = await GetStatusAsync(previewId);
        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Contains($"attempt {PdfPreviewRunner.MaxAttempts} of {PdfPreviewRunner.MaxAttempts}", failed.GetProperty("error").GetString(), StringComparison.Ordinal);

        using var content = await _client.GetAsync(new Uri($"/api/v1/previews/{previewId}/content", UriKind.Relative));
        Assert.Equal("preview.failed", (await ReadAsync(content)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task FailureFollowedBySuccess_Succeeds_OnRetry()
    {
        var previewId = await QueueAsync(await CreateInvoiceTemplateAsync());
        var printer = new FakePrinter { FailuresBeforeSuccess = 1 };

        await Assert.ThrowsAsync<TimeoutException>(() => RunJobAsync(previewId, printer));
        await RunJobAsync(previewId, printer);

        Assert.Equal("Succeeded", (await GetStatusAsync(previewId)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task FinishedPreview_IsRelayedToSubscribers_ExactlyOnce()
    {
        var previewId = await QueueAsync(await CreateInvoiceTemplateAsync());
        await RunJobAsync(previewId, new FakePrinter());

        // The API's relay polls every 500 ms.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!fixture.Factory.Notifications.Sent.Any(n => n.PreviewId == previewId) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        await Task.Delay(1200); // a few more relay cycles must not repeat it
        var sent = fixture.Factory.Notifications.Sent.Where(n => n.PreviewId == previewId).ToList();
        var notice = Assert.Single(sent);
        Assert.Equal("Succeeded", notice.Status);
    }

    [Fact]
    public async Task Hub_AcceptsSignalRNegotiation()
    {
        using var response = await _client.PostAsync(new Uri("/hubs/executions/negotiate?negotiateVersion=1", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await ReadAsync(response)).TryGetProperty("connectionToken", out _));
    }

    [Fact]
    public async Task Cleanup_DeletesPreviewsOlderThanADay_AndKeepsNewerOnes()
    {
        var templateId = await CreateInvoiceTemplateAsync();
        var old = await QueueAsync(templateId);
        var recent = await QueueAsync(templateId);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VersoDbContext>();
        await db.PdfPreviews.Where(p => p.Id == old).ExecuteUpdateAsync(s => s.SetProperty(p => p.CreatedAt, DateTimeOffset.UtcNow.AddHours(-25)));

        var deleted = await new PdfPreviewCleanup(db, TimeProvider.System).RunAsync(CancellationToken.None);

        Assert.True(deleted >= 1);
        Assert.False(await db.PdfPreviews.AnyAsync(p => p.Id == old));
        Assert.True(await db.PdfPreviews.AnyAsync(p => p.Id == recent));
    }

    [Fact]
    public async Task Queue_ValidatesLikeTheHtmlPreview()
    {
        using var unknown = await _client.PostAsync(PdfUri(Guid.NewGuid()), null);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        using var notObject = await _client.PostAsJsonAsync(PdfUri(await CreateInvoiceTemplateAsync()), new { data = "text" });
        Assert.Equal(HttpStatusCode.BadRequest, notObject.StatusCode);

        using var missing = await _client.GetAsync(new Uri($"/api/v1/previews/{Guid.NewGuid()}", UriKind.Relative));
        Assert.Equal("preview.not_found", (await ReadAsync(missing)).GetProperty("code").GetString());
    }

    // ----- Helpers -----

    private async Task RunJobAsync(Guid previewId, IPdfPrinter printer)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VersoDbContext>();
        await new PdfPreviewRunner(db, printer, TimeProvider.System).RunAsync(previewId, CancellationToken.None);
    }

    private async Task<Guid> QueueAsync(Guid templateId)
    {
        using var response = await _client.PostAsync(PdfUri(templateId), null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("previewId").GetGuid();
    }

    private async Task<JsonElement> GetStatusAsync(Guid previewId)
    {
        using var response = await _client.GetAsync(new Uri($"/api/v1/previews/{previewId}", UriKind.Relative));
        return await ReadAsync(response);
    }

    private static Uri PdfUri(Guid id) => new($"/api/v1/templates/{id}/draft/preview/pdf", UriKind.Relative);

    private static string Fixture(string relative) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", relative));

    private async Task<Guid> CreateInvoiceTemplateAsync()
    {
        var key = "F" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        using var create = await _client.PostAsJsonAsync(new Uri("/api/v1/templates", UriKind.Relative), new { key, name = "PDF preview test" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = (await ReadAsync(create)).GetProperty("id").GetGuid();
        using var save = await PutDraftAsync(id, await DraftETagAsync(id), DraftBody(JsonNode.Parse(Fixture("invoice/layout.json"))!, JsonNode.Parse(Fixture("invoice/sample.json"))!));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        return id;
    }

    private async Task<string> DraftETagAsync(Guid id)
    {
        using var response = await _client.GetAsync(new Uri($"/api/v1/templates/{id}/draft", UriKind.Relative));
        return response.Headers.ETag!.ToString();
    }

    private Task<HttpResponseMessage> PutDraftAsync(Guid id, string etag, JsonObject body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"/api/v1/templates/{id}/draft", UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return _client.SendAsync(request);
    }

    private static JsonObject DraftBody(JsonNode layout, JsonNode sampleData) => new()
    {
        ["pageSettings"] = new JsonObject
        {
            ["size"] = "A4",
            ["orientation"] = "portrait",
            ["marginsMm"] = new JsonObject { ["top"] = 18, ["right"] = 16, ["bottom"] = 18, ["left"] = 16 },
        },
        ["layout"] = layout,
        ["dataSchema"] = new JsonObject { ["type"] = "object" },
        ["sampleData"] = sampleData,
    };

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), Json);

    private sealed class FakePrinter : IPdfPrinter
    {
        public static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n% fake preview for tests\n%%EOF\n");

        public int FailuresBeforeSuccess { get; init; }
        public List<PrintDocument> Printed { get; } = [];
        private int _calls;

        public Task<PdfOutput> PrintAsync(PrintDocument document, CancellationToken cancellationToken)
        {
            if (_calls++ < FailuresBeforeSuccess)
            {
                throw new TimeoutException("Simulated Chromium timeout.");
            }

            Printed.Add(document);
            return Task.FromResult(new PdfOutput(Pdf, 0, 1));
        }
    }
}
