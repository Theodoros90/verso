using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Verso.E2E.Tests;

/// <summary>
/// Plan §8 / M9: the 7 MVP steps in a real browser against the running system.
/// 1 edit a template · 2 publish it · 3 build a workflow · 4 publish it · 5 submit a valid invoice → Succeeded, PDF
/// downloadable and identical to the archive · 6 submit a broken invoice → Rejected with its issues · 7 history shows
/// both. Then: roles in the UI, request limits on Kestrel, and that no payload value reached the services' logs.
/// </summary>
[Collection(E2EGroup.Name)]
public sealed partial class MvpScenarioTests(E2EEnvironment env)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task TheSevenMvpSteps_InTheBrowser()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var marker = $"E2E-MARKER-{tag}"; // only the submitted payloads contain it
        var page = await env.SignInAsync("Alex Admin");

        // 1–2. Template: create, import the sample data, bind fields, save, publish.
        var templateKey = $"E2E-{tag}";
        await page.GotoAsync("/templates");
        await page.GetByRole(AriaRole.Button, new() { Name = "New template" }).ClickAsync();
        await page.GetByLabel("Key").FillAsync(templateKey);
        await page.GetByLabel("Name").FillAsync($"E2E invoice {tag}");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(new Regex("/templates/[0-9a-f-]{36}$"));
        await page.GetByLabel("Sample data file").SetInputFilesAsync(E2EEnvironment.Fixture("invoice/sample.json"));
        foreach (var field in new[] { "invoice.number", "invoice.date", "customer.name", "totals.gross" })
        {
            await page.GetByRole(AriaRole.Button, new() { Name = $"Add field {field}", Exact = true }).ClickAsync();
        }

        await page.GetByRole(AriaRole.Button, new() { Name = "Save draft" }).ClickAsync();
        await ExpectPublishableAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Publish" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Published v1")).ToBeVisibleAsync();

        // 3–4. Workflow: trigger → validate → generate PDF → archive, built in the designer, then published.
        var workflowKey = $"e2e-{tag.ToLowerInvariant()}";
        await page.GotoAsync("/workflows");
        await page.GetByRole(AriaRole.Button, new() { Name = "New workflow" }).ClickAsync();
        await page.GetByLabel("Key").FillAsync(workflowKey);
        await page.GetByLabel("Name").FillAsync($"E2E invoice standard {tag}");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(new Regex("/workflows/[0-9a-f-]{36}$"));
        var canvas = page.GetByLabel("Workflow canvas");
        await canvas.GetByText("API / webhook", new() { Exact = true }).First.ClickAsync(); // card title (the type label below says the same)
        await page.GetByRole(AriaRole.Button, new() { Name = "Expand all" }).ClickAsync(); // the palette's categories start collapsed
        foreach (var step in new[] { "Validate data", "Generate PDF", "Archive" })
        {
            await page.GetByRole(AriaRole.Button, new() { Name = $"Add {step}", Exact = true }).ClickAsync(); // placed right of the selected step
        }

        await ConnectAsync(page, "API / webhook: output Out", "Validate data: input");
        await ConnectAsync(page, "Validate data: output Valid", "Generate PDF: input");
        await ConnectAsync(page, "Generate PDF: output Success", "Archive: input");

        await canvas.GetByText("Validate data", new() { Exact = true }).First.ClickAsync(); // card title (the type label below says the same)
        var settings = page.GetByRole(AriaRole.Region, new() { Name = "Step settings" });
        await settings.GetByRole(AriaRole.Button, new() { Name = "Add", Exact = true }).First.ClickAsync();
        await settings.GetByLabel("Required fields 1", new() { Exact = true }).FillAsync("customer.name");
        await settings.GetByRole(AriaRole.Button, new() { Name = "Add", Exact = true }).Last.ClickAsync();
        await settings.GetByLabel("Field type checks 1: Field", new() { Exact = true }).FillAsync("invoice.date");
        await settings.GetByLabel("Field type checks 1: Expected type", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Date (YYYY-MM-DD)" });

        await canvas.GetByText("Generate PDF", new() { Exact = true }).First.ClickAsync(); // card title (the type label below says the same)
        await settings.GetByLabel("Template *", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = $"E2E invoice {tag} ({templateKey})" });

        await page.GetByRole(AriaRole.Button, new() { Name = "Save draft" }).ClickAsync();
        await ExpectPublishableAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Publish" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Published v1")).ToBeVisibleAsync();

        // 5. A valid invoice, submitted through the API by an operator, followed live in the run view.
        using var operatorApi = env.ApiClient("dev.operator");
        var correlationId = $"e2e-{tag}";
        var valid = Fixture("invoice/sample.json");
        valid["customer"]!["name"] = marker;
        var ok = await SubmitAsync(operatorApi, workflowKey, valid, correlationId);
        await page.GotoAsync($"/executions/{ok}");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = $"Run {ShortId(ok)}" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Succeeded", new() { Exact = true }).First).ToBeVisibleAsync(new() { Timeout = 60_000 });
        var steps = page.GetByRole(AriaRole.List, new() { Name = "Steps" }).Locator("li");
        Assert.Equal(["Succeeded", "Succeeded", "Succeeded", "Succeeded"], await steps.EvaluateAllAsync<string[]>("els => els.map(e => e.dataset.status)"));

        var fileName = "INV-2026-003901.pdf";
        var download = await page.RunAndWaitForDownloadAsync(() => page.GetByRole(AriaRole.Button, new() { Name = $"Download {fileName}" }).ClickAsync());
        var saved = Path.Combine(env.WorkDir, "downloaded.pdf");
        await download.SaveAsAsync(saved);
        var bytes = await File.ReadAllBytesAsync(saved);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5), StringComparison.Ordinal);
        var detail = await ReadAsync(await operatorApi.GetAsync(new Uri($"/api/v1/executions/{ok}", UriKind.Relative)));
        var document = detail.GetProperty("documents")[0];
        Assert.Equal(document.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes))); // the archived bytes
        Assert.Equal(fileName, download.SuggestedFilename);

        // 6. A broken invoice: Rejected, every failed rule shown, no document.
        var broken = Fixture("invoice/broken.json");
        broken["payment"]!["reference"] = marker;
        var rejected = await SubmitAsync(operatorApi, workflowKey, broken, $"{correlationId}-b");
        await page.GotoAsync($"/executions/{rejected}");
        await Assertions.Expect(page.GetByText("Rejected: the data needs correcting.")).ToBeVisibleAsync(new() { Timeout = 60_000 });
        var problems = page.GetByRole(AriaRole.List, new() { Name = "Problems found in Validate data" });
        await Assertions.Expect(problems.GetByRole(AriaRole.Listitem)).ToHaveTextAsync(["customer.name [required]: missing or empty", "invoice.date [type:date]: expected YYYY-MM-DD"]);
        await Assertions.Expect(page.GetByText("This run produced no document.")).ToBeVisibleAsync();

        // 7. History: both runs, with their outcome, and the status filter works.
        await page.GotoAsync("/executions");
        var table = page.GetByRole(AriaRole.Table, new() { Name = "Executions" });
        await Assertions.Expect(table.GetByRole(AriaRole.Row).Filter(new() { Has = page.GetByRole(AriaRole.Link, new() { Name = $"Open run {ShortId(ok)}" }) })).ToContainTextAsync("Succeeded");
        await Assertions.Expect(table.GetByRole(AriaRole.Row).Filter(new() { Has = page.GetByRole(AriaRole.Link, new() { Name = $"Open run {ShortId(rejected)}" }) })).ToContainTextAsync("Rejected");
        await page.GetByLabel("Status", new() { Exact = true }).SelectOptionAsync("Rejected");
        await page.GetByLabel("Workflow", new() { Exact = true }).SelectOptionAsync(workflowKey);
        await Assertions.Expect(table.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("^Open run ") })).ToHaveCountAsync(1);

        // Roles in the UI: a Viewer sees the run but not the payload, and has no audit trail.
        var viewer = await env.SignInAsync("Vera Viewer");
        await viewer.GotoAsync($"/executions/{ok}");
        await Assertions.Expect(viewer.GetByText("Submitted data is restricted to the Auditor and Admin roles.")).ToBeVisibleAsync();
        await Assertions.Expect(viewer.GetByRole(AriaRole.Button, new() { Name = "Show submitted data" })).ToHaveCountAsync(0);
        await Assertions.Expect(viewer.GetByRole(AriaRole.Navigation, new() { Name = "Main" }).GetByRole(AriaRole.Link, new() { Name = "Audit trail" })).ToHaveCountAsync(0);
        await viewer.Context.CloseAsync();

        // The admin can read it (audited), and the audit trail shows the download and the read.
        await page.GotoAsync($"/executions/{ok}");
        await page.GetByRole(AriaRole.Button, new() { Name = "Show submitted data" }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("Submitted JSON")).ToContainTextAsync(marker);
        await page.GotoAsync("/audit");
        var audit = page.GetByRole(AriaRole.Table, new() { Name = "Audit trail" });
        await Assertions.Expect(audit.GetByText("DocumentDownloaded").First).ToBeVisibleAsync();
        await Assertions.Expect(audit.GetByText("ExecutionInputViewed").First).ToBeVisibleAsync();

        // Limits enforced by Kestrel (the in-memory test server doesn't): body caps.
        using var admin = env.ApiClient("dev.admin");
        using var huge = new StringContent("{\"data\":{\"blob\":\"" + new string('x', 1_500_000) + "\"}}", Encoding.UTF8, "application/json");
        using var submitTooLarge = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/workflows/{workflowKey}/executions", UriKind.Relative)) { Content = huge };
        submitTooLarge.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var tooLarge = await admin.SendAsync(submitTooLarge);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        using var anonymous = new HttpClient { BaseAddress = new Uri(env.ApiUrl) };
        using var noToken = await anonymous.GetAsync(new Uri("/api/v1/executions", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);

        // Logs of both services: no payload value on any of these paths, and the run is traceable by its correlation id.
        await Task.Delay(1500); // let the processes flush
        var apiLog = ReadShared(env.ApiLog);
        var workerLog = ReadShared(env.WorkerLog);
        Assert.DoesNotContain(marker, apiLog, StringComparison.Ordinal);
        Assert.DoesNotContain(marker, workerLog, StringComparison.Ordinal);
        Assert.Contains(correlationId, apiLog, StringComparison.Ordinal);
        Assert.Contains(workerLog.Split('\n'), line => line.Contains(correlationId, StringComparison.Ordinal) && line.Contains("finished: Succeeded", StringComparison.Ordinal));

        await page.Context.CloseAsync();
    }

    // ----- Helpers -----

    /// <summary>Waits until Publish is enabled; otherwise fails with the designer's own list of problems (and a screenshot).</summary>
    private async Task ExpectPublishableAsync(IPage page)
    {
        try
        {
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Publish" })).ToBeEnabledAsync(new() { Timeout = 15_000 });
        }
        catch (PlaywrightException)
        {
            var shot = Path.Combine(env.WorkDir, $"not-publishable-{Guid.NewGuid():N}.png");
            await page.ScreenshotAsync(new() { Path = shot, FullPage = true });
            var issues = page.GetByRole(AriaRole.Region, new() { Name = "Issues" });
            var text = await issues.CountAsync() > 0 ? await issues.InnerTextAsync() : await page.Locator("body").InnerTextAsync();
            throw new Xunit.Sdk.XunitException($"Publish stayed disabled. Problems shown:\n{text}\nScreenshot: {shot}");
        }
    }

    /// <summary>Drags from an output port to an input port, as a user connects two steps.</summary>
    /// <remarks>
    /// The canvas may still be settling after a step was added (positions read a moment earlier can be stale), so the
    /// connection count in the status bar confirms the drag took effect; a drag that didn't register is repeated.
    /// </remarks>
    private static async Task ConnectAsync(IPage page, string from, string to)
    {
        var before = await ConnectionCountAsync(page);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var sourceHandle = page.GetByLabel(from, new() { Exact = true });
            var targetHandle = page.GetByLabel(to, new() { Exact = true });
            await targetHandle.ScrollIntoViewIfNeededAsync();
            await targetHandle.HoverAsync(); // waits until it is visible and stable
            var source = await sourceHandle.BoundingBoxAsync() ?? throw new InvalidOperationException($"'{from}' isn't visible.");
            var target = await targetHandle.BoundingBoxAsync() ?? throw new InvalidOperationException($"'{to}' isn't visible.");
            await page.Mouse.MoveAsync(source.X + source.Width / 2, source.Y + source.Height / 2);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(target.X + target.Width / 2, target.Y + target.Height / 2, new() { Steps = 12 });
            await page.Mouse.UpAsync();
            for (var wait = 0; wait < 10 && await ConnectionCountAsync(page) == before; wait++)
            {
                await page.WaitForTimeoutAsync(100);
            }

            if (await ConnectionCountAsync(page) == before + 1)
            {
                return;
            }
        }

        throw new Xunit.Sdk.XunitException($"Could not connect '{from}' to '{to}'.");
    }

    private static async Task<int> ConnectionCountAsync(IPage page)
    {
        var text = await page.GetByText(ConnectionsText()).First.InnerTextAsync(); // status bar: "4 steps, 2 connections"
        return int.Parse(ConnectionsText().Match(text).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"(\d+) connections?")]
    private static partial Regex ConnectionsText();

    private static async Task<Guid> SubmitAsync(HttpClient api, string workflowKey, JsonNode data, string correlationId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/workflows/{workflowKey}/executions", UriKind.Relative))
        {
            Content = JsonContent.Create(new JsonObject { ["data"] = data }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add("X-Correlation-Id", correlationId);
        using var response = await api.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("executionId").GetGuid();
    }

    private static JsonNode Fixture(string relative) => JsonNode.Parse(File.ReadAllText(E2EEnvironment.Fixture(relative)))!;

    private static string ShortId(Guid id) => id.ToString("N")[^8..];

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using (response)
        {
            return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), Json);
        }
    }

    /// <summary>Reads a log file the service is still writing.</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
