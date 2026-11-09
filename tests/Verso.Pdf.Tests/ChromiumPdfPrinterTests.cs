using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Verso.Rendering;
using Verso.Rendering.Pdf;
using static Verso.Pdf.Tests.PdfTestSupport;

namespace Verso.Pdf.Tests;

/// <summary>Shares one printer (one Chromium) across the tests that don't kill or recycle it.</summary>
public sealed class PrinterFixture : IAsyncLifetime
{
    public ChromiumPdfPrinter Printer { get; private set; } = null!;

    public Task InitializeAsync()
    {
        Printer = CreatePrinter(out _);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await Printer.DisposeAsync();
}

/// <summary>M5 acceptance criteria, with real Chromium (plan §9, M5).</summary>
public sealed partial class ChromiumPdfPrinterTests(PrinterFixture fixture) : IClassFixture<PrinterFixture>
{
    private readonly ChromiumPdfPrinter _printer = fixture.Printer;

    [Fact]
    public async Task Invoice_PdfText_ContainsNumberCustomerAllLinesAndTotals()
    {
        var output = await _printer.PrintAsync(InvoicePrint(Sample()), CancellationToken.None);

        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(output.Content, 0, 5), StringComparison.Ordinal);
        var pages = PageTexts(output.Content);
        var text = string.Join("\n", pages);
        Assert.Single(pages);
        Assert.Contains("INV-2026-003901", text, StringComparison.Ordinal);
        Assert.Contains("Kestrel Logistics Ltd", text, StringComparison.Ordinal);
        // A long description wraps inside its cell, so other columns' words sit between its lines: check word order.
        AssertWordsInOrder("Electricity supply, October 2026 (commercial tariff)", text);
        AssertWordsInOrder("Reactive power surcharge", text);
        AssertWordsInOrder("Meter rental", text);
        Assert.Contains("€4,635.87", text, StringComparison.Ordinal); // net
        Assert.Contains("€880.82", text, StringComparison.Ordinal);   // VAT
        Assert.Contains("€5,516.69", text, StringComparison.Ordinal); // total due
        Assert.Contains("Halcyon Energy Ltd", text, StringComparison.Ordinal); // header template (issuer) and footer
        Assert.Contains("billing@halcyon-energy.example", text, StringComparison.Ordinal); // footer template
    }

    [Fact]
    public async Task Invoice_EmbedsTheDocumentFonts_InBodyAndHeaderFooter()
    {
        var output = await _printer.PrintAsync(InvoicePrint(Sample()), CancellationToken.None);

        var fonts = FontNames(output.Content);
        Assert.Contains(fonts, f => f.Contains("SchibstedGrotesk", StringComparison.OrdinalIgnoreCase)); // body and templates
        Assert.Contains(fonts, f => f.Contains("SourceSerif4", StringComparison.OrdinalIgnoreCase));     // "Invoice" title (serif) in the header template
        Assert.DoesNotContain("Type3", fonts); // real embedded fonts, not glyph outlines
    }

    [Fact]
    public async Task HeaderOnlyFont_IsPrinted_NotLeftInvisibleWhileLoading()
    {
        // Regression: the serif 24 pt title exists only in the header template. Before preloading, its font was
        // still loading when Chromium drew the template, and the title was missing from the PDF.
        var output = await _printer.PrintAsync(InvoicePrint(Sample()), CancellationToken.None);

        using var pdf = UglyToad.PdfPig.PdfDocument.Open(output.Content);
        var title = pdf.GetPage(1).GetWords().SingleOrDefault(w => w.Text == "Invoice" && w.Letters[0].PointSize > 20);
        Assert.NotNull(title);
        Assert.Contains("SourceSerif4", title.Letters[0].FontName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SixtyLines_Paginates_RepeatingTableHeadingsHeaderAndFooterOnEveryPage()
    {
        var output = await _printer.PrintAsync(InvoicePrint(SampleWithLines(60)), CancellationToken.None);

        var pages = PageTexts(output.Content);
        Assert.True(pages.Count >= 2, $"Expected several pages, got {pages.Count}.");
        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            Assert.True(page.Contains("Description", StringComparison.Ordinal) && page.Contains("Unit price", StringComparison.Ordinal),
                $"Page {i + 1} lacks the table headings: {page}");
            Assert.True(page.Contains("INV-2026-003901", StringComparison.Ordinal), $"Page {i + 1} lacks the header.");
            Assert.True(page.Contains("billing@halcyon-energy.example", StringComparison.Ordinal), $"Page {i + 1} lacks the footer.");
        }

        var all = string.Join("\n", pages);
        for (var n = 1; n <= 60; n++)
        {
            Assert.Contains($"Synthetic service line {n:00}", all, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ExternalUrls_MakeZeroNetworkRequests()
    {
        // A local listener plays "the internet": every external reference points at it, and it must see no connection.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var connections = 0;
        using var cts = new CancellationTokenSource();
        var accepting = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(cts.Token);
                    Interlocked.Increment(ref connections);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        var url = $"http://127.0.0.1:{port}";
        // Deliberately no CSP: the printer itself must block everything, independent of the renderer's safeguards.
        var html = $$"""
            <!doctype html><html><head>
            <link rel="stylesheet" href="{{url}}/style.css">
            <style>@import url("{{url}}/import.css"); body{background:url("{{url}}/bg.png")} @font-face{font-family:X;src:url("{{url}}/f.woff2")} p{font-family:X}</style>
            <script src="{{url}}/app.js"></script>
            </head><body><p>External references</p><img src="{{url}}/logo.png"><iframe src="{{url}}/frame.html"></iframe>
            <img src="https://example.com/tracker.png"></body></html>
            """;
        var header = $"<div style=\"font-size:10pt\"><img src=\"{url}/header.png\">Header</div>";
        var document = new PrintDocument(html, header, "<div>Footer</div>", 210, 297, 20, 15);

        var output = await _printer.PrintAsync(document, CancellationToken.None);
        await Task.Delay(300);
        await cts.CancelAsync();
        listener.Stop();
        await accepting;

        Assert.Equal(0, connections);
        Assert.True(output.BlockedRequests > 0, "The page should have tried (and been blocked from) loading the external resources.");
        Assert.Contains("External references", string.Join(" ", PageTexts(output.Content)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnePageInvoice_RendersUnderThreeSeconds_P95()
    {
        var document = InvoicePrint(Sample());
        await _printer.PrintAsync(document, CancellationToken.None); // warm-up: browser start is a one-time cost

        var timings = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var sw = Stopwatch.StartNew();
            await _printer.PrintAsync(document, CancellationToken.None);
            timings.Add(sw.Elapsed.TotalMilliseconds);
        }

        timings.Sort();
        var p95 = timings[(int)Math.Ceiling(0.95 * timings.Count) - 1];
        Assert.True(p95 < 3000, $"p95 {p95.ToString("0", CultureInfo.InvariantCulture)} ms (median {timings[timings.Count / 2].ToString("0", CultureInfo.InvariantCulture)} ms)");
    }

    [Fact]
    public async Task HostileData_IsPrintedAsText_NotMarkup()
    {
        var data = System.Text.Json.Nodes.JsonNode.Parse(Fixture("invoice/hostile.json"))!;
        var output = await _printer.PrintAsync(InvoicePrint(data), CancellationToken.None);

        var text = string.Join(" ", PageTexts(output.Content));
        Assert.Contains("<script>alert('x')</script>", WhitespaceRegex().Replace(text, ""), StringComparison.Ordinal);
    }

    private static void AssertWordsInOrder(string expected, string text)
    {
        var words = text.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var at = 0;
        foreach (var word in expected.Split(' '))
        {
            at = Array.IndexOf(words, word, at);
            Assert.True(at >= 0, $"'{expected}' not found in order (missing '{word}') in: {text}");
            at++;
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

/// <summary>Tests that kill or recycle Chromium get their own printer, so they can't disturb the shared one.</summary>
public sealed class ChromiumLifecycleTests
{
    [Fact]
    public async Task ChromiumKilledMidRender_IsRestarted_AndTheDocumentIsRetried()
    {
        await using var printer = CreatePrinter(out var marker);
        var killed = false;
        printer.BeforePrintHook = async () =>
        {
            if (killed)
            {
                return;
            }

            killed = true;
            await KillProcessesWithMarkerAsync(marker);
        };

        var output = await printer.PrintAsync(InvoicePrint(Sample()), CancellationToken.None);

        Assert.True(killed);
        Assert.Equal(2, output.Attempts);
        Assert.Equal(2, printer.BrowserLaunches);
        Assert.Contains("INV-2026-003901", string.Join(" ", PageTexts(output.Content)), StringComparison.Ordinal);

        // The restarted browser keeps working for later documents.
        var next = await printer.PrintAsync(InvoicePrint(Sample()), CancellationToken.None);
        Assert.Equal(1, next.Attempts);
    }

    [Fact]
    public async Task PlaywrightDriverKilledMidRender_IsReplaced_AndLaterDocumentsStillPrint()
    {
        // Regression (seen in the end-to-end check): once the driver process died, every later render failed.
        await using var printer = CreatePrinter(out _);
        await printer.WarmUpAsync(CancellationToken.None);
        var killed = false;
        printer.BeforePrintHook = async () =>
        {
            if (killed)
            {
                return;
            }

            killed = true;
            await KillPlaywrightDriverAsync();
        };

        var output = await printer.PrintAsync(InvoicePrint(Sample()), CancellationToken.None);

        Assert.True(killed);
        Assert.Equal(2, output.Attempts);
        Assert.Equal(1, printer.DriverResets);
        Assert.Contains("INV-2026-003901", string.Join(" ", PageTexts(output.Content)), StringComparison.Ordinal);
        Assert.Equal(1, (await printer.PrintAsync(InvoicePrint(Sample()), CancellationToken.None)).Attempts);
    }

    [Fact]
    public async Task WarmUp_StartsChromium_BeforeTheFirstDocument()
    {
        await using var printer = CreatePrinter(out _);

        await printer.WarmUpAsync(CancellationToken.None);
        Assert.Equal(1, printer.BrowserLaunches);

        var sw = Stopwatch.StartNew();
        await printer.PrintAsync(InvoicePrint(Sample()), CancellationToken.None);
        Assert.Equal(1, printer.BrowserLaunches); // reused, not launched again
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"First document after warm-up took {sw.Elapsed.TotalMilliseconds:0} ms");
    }

    [Fact]
    public async Task Browser_IsRecycled_AfterTheConfiguredNumberOfRenders()
    {
        await using var printer = CreatePrinter(out _, o => o.RecycleAfterRenders = 2);
        var document = InvoicePrint(Sample());

        for (var i = 0; i < 5; i++)
        {
            await printer.PrintAsync(document, CancellationToken.None);
        }

        Assert.Equal(3, printer.BrowserLaunches); // renders 1–2, 3–4, 5
    }

    [Fact]
    public async Task Render_ThatExceedsTheTimeout_FailsWithTimeout()
    {
        await using var printer = CreatePrinter(out _, o => o.RenderTimeout = TimeSpan.FromMilliseconds(1));
        await Assert.ThrowsAnyAsync<TimeoutException>(() => printer.PrintAsync(InvoicePrint(SampleWithLines(400)), CancellationToken.None));
    }
}
