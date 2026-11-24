using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Verso.E2E.Tests;

/// <summary>
/// ADR 0023, the File upload step in a real browser against the running system: an Operator uploads a synthetic CSV from the
/// workflow list, the Worker takes the staged file and runs the workflow, and history shows the run with the file's name (never
/// its content). A Viewer is not offered the upload. Synthetic data only (tests/fixtures/uploads).
/// </summary>
[Collection(E2EGroup.Name)]
public sealed partial class FileUploadTests(E2EEnvironment env)
{
    private const string CsvFixture = "uploads/customers.csv";
    private const string ContentMarker = "Synthetic Holder Upload-E2E"; // only the file's content contains it

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Operator_uploads_a_csv_and_sees_the_run_in_history()
    {
        var (_, name) = await PublishUploadWorkflowAsync();
        var page = await env.SignInAsync("Omar Operator");

        // Upload from the workflow list: the card's "⋯" menu, then the dialog.
        await page.GotoAsync("/workflows");
        await page.GetByRole(AriaRole.Button, new() { Name = $"Actions for {name}", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Upload file…" }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog.GetByText(new Regex(@"^Allowed file types: .*CSV.*, up to 1 MB\.$"))).ToBeVisibleAsync();
        await dialog.GetByLabel("Choose a file", new() { Exact = true }).SetInputFilesAsync(E2EEnvironment.Fixture(CsvFixture));
        await Assertions.Expect(dialog.GetByText(new Regex(@"^customers\.csv \("))).ToBeVisibleAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Upload", Exact = true }).ClickAsync();
        await Assertions.Expect(dialog.GetByRole(AriaRole.Status)).ToContainTextAsync("The file was uploaded and a new run has started.");
        var href = await dialog.GetByRole(AriaRole.Link, new() { Name = "Open the run" }).GetAttributeAsync("href");
        var runId = Guid.Parse(RunLink().Match(href ?? "").Groups[1].Value);

        // History: the run reaches Succeeded (the list refreshes itself).
        await page.GotoAsync("/executions");
        var table = page.GetByRole(AriaRole.Table, new() { Name = "Executions" });
        var row = table.GetByRole(AriaRole.Row).Filter(new() { Has = page.GetByRole(AriaRole.Link, new() { Name = $"Open run {ShortId(runId)}" }) });
        await Assertions.Expect(row).ToContainTextAsync("Succeeded", new() { Timeout = 60_000 });

        // The run: every step succeeded, the parsed CSV row reached If / else, and the File upload step names the file.
        await row.GetByRole(AriaRole.Link, new() { Name = $"Open run {ShortId(runId)}" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = $"Run {ShortId(runId)}" })).ToBeVisibleAsync();
        var steps = page.GetByRole(AriaRole.List, new() { Name = "Steps" });
        await Assertions.Expect(steps.Locator("li[data-status]")).ToHaveCountAsync(3);
        Assert.Equal(["Succeeded", "Succeeded", "Succeeded"], await steps.Locator("li[data-status]").EvaluateAllAsync<string[]>("els => els.map(e => e.dataset.status)"));
        var csv = new FileInfo(E2EEnvironment.Fixture(CsvFixture));
        await Assertions.Expect(steps.GetByText("Uploaded file:")).ToHaveTextAsync($"Uploaded file: customers.csv ({csv.Length} B)");
        await Assertions.Expect(steps).ToContainTextAsync("left by “yes”"); // If / else saw CustomerID C-90001 from the file
        await Assertions.Expect(steps).Not.ToContainTextAsync(ContentMarker);

        // The step result through the API: name, type, size and hash only, never the bytes.
        using var operatorApi = env.ApiClient("dev.operator");
        var detail = await ReadAsync(await operatorApi.GetAsync(new Uri($"/api/v1/executions/{runId}", UriKind.Relative)));
        Assert.Equal("Succeeded", detail.GetProperty("status").GetString());
        var upload = detail.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("nodeId").GetString() == "upload");
        var result = upload.GetProperty("result");
        Assert.Equal(["fileName", "contentType", "size", "sha256"], result.EnumerateObject().Select(p => p.Name));
        Assert.Equal("customers.csv", result.GetProperty("fileName").GetString());
        Assert.Equal(csv.Length, result.GetProperty("size").GetInt64());
        Assert.DoesNotContain(ContentMarker, detail.GetRawText(), StringComparison.Ordinal);

        await page.Context.CloseAsync();
    }

    [Fact]
    public async Task Viewer_does_not_see_the_upload_action()
    {
        var (key, name) = await PublishUploadWorkflowAsync();

        // The Operator is offered the upload for this workflow (the control for the check below).
        var operatorPage = await env.SignInAsync("Omar Operator");
        await operatorPage.GotoAsync("/workflows");
        await operatorPage.GetByRole(AriaRole.Button, new() { Name = $"Actions for {name}", Exact = true }).ClickAsync();
        await Assertions.Expect(operatorPage.GetByRole(AriaRole.Menuitem, new() { Name = "Upload file…" })).ToBeVisibleAsync();
        await operatorPage.Context.CloseAsync();

        // The Viewer sees the workflow but no actions menu and no upload.
        var viewer = await env.SignInAsync("Vera Viewer");
        await viewer.GotoAsync("/workflows");
        await Assertions.Expect(viewer.GetByRole(AriaRole.List, new() { Name = "Workflows" }).GetByText(name, new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(viewer.GetByRole(AriaRole.Button, new() { Name = $"Actions for {name}", Exact = true })).ToHaveCountAsync(0);
        await Assertions.Expect(viewer.GetByText("Upload file…")).ToHaveCountAsync(0);
        await viewer.Context.CloseAsync();

        // And the API refuses a Viewer's upload outright.
        using var viewerApi = env.ApiClient("dev.viewer");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(E2EEnvironment.Fixture(CsvFixture)));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "customers.csv");
        using var refused = await viewerApi.PostAsync(new Uri($"/api/v1/workflows/{key}/uploads", UriKind.Relative), form);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    // ----- Helpers -----

    /// <summary>
    /// Publishes, through the API as the workflow author, File upload (CSV or XML, up to 1 MB, staff) → Parse the CSV → If / else
    /// on the parsed CustomerID (both paths end the run). Parse's Success output must be connected, so the smallest publishable
    /// workflow that reads the upload has three steps. Returns the key and the name shown on the list.
    /// </summary>
    private async Task<(string Key, string Name)> PublishUploadWorkflowAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var key = $"e2e-upload-{tag}";
        var name = $"E2E upload {tag}";
        using var author = env.ApiClient("dev.workflow-author");
        using var create = await author.PostAsJsonAsync(new Uri("/api/v1/workflows", UriKind.Relative), new { key, name });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var workflowId = (await ReadAsync(create)).GetProperty("id").GetGuid();

        var definition = JsonNode.Parse("""
            { "nodes": [
                { "id": "upload", "type": "file-upload", "title": "File upload", "position": { "x": 0, "y": 80 },
                  "config": { "accept": "CSV, XML", "maxSize": "1mb", "who": "staff" } },
                { "id": "parse", "type": "parse", "title": "Read the CSV", "position": { "x": 260, "y": 80 },
                  "config": { "source": "content", "format": "csv", "encoding": "utf-8", "recordPath": "", "numbers": false } },
                { "id": "check", "type": "if-else", "title": "Known customer?", "position": { "x": 520, "y": 80 },
                  "config": { "conditions": [ { "join": "and", "field": "CustomerID", "op": "starts-with", "compare": "value", "value": "C-" } ],
                              "yesLabel": "Yes", "noLabel": "No" } } ],
              "edges": [
                { "id": "e1", "source": "upload", "sourcePort": "out", "target": "parse", "targetPort": "in" },
                { "id": "e2", "source": "parse", "sourcePort": "success", "target": "check", "targetPort": "in" } ] }
            """)!;

        using var draft = await author.GetAsync(new Uri($"/api/v1/workflows/{workflowId}/draft", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
        using var save = await SendAsync(author, HttpMethod.Put, $"/api/v1/workflows/{workflowId}/draft", draft.Headers.ETag!.ToString(), new JsonObject { ["definition"] = definition });
        Assert.True(save.StatusCode == HttpStatusCode.OK, await save.Content.ReadAsStringAsync());
        using var publish = await SendAsync(author, HttpMethod.Post, $"/api/v1/workflows/{workflowId}/draft/publish", save.Headers.ETag!.ToString());
        Assert.True(publish.StatusCode == HttpStatusCode.OK, await publish.Content.ReadAsStringAsync());
        return (key, name);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string uri, string etag, JsonNode? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(uri, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return client.SendAsync(request);
    }

    private static string ShortId(Guid id) => id.ToString("N")[^8..];

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using (response)
        {
            return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), Json);
        }
    }

    [GeneratedRegex("/executions/([0-9a-f-]{36})$")]
    private static partial Regex RunLink();
}
