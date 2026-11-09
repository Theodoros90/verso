using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using Verso.Rendering;
using Verso.Rendering.Html;
using Verso.Rendering.Layout;
using Verso.Rendering.Pdf;

namespace Verso.Pdf.Tests;

internal static class PdfTestSupport
{
    public static readonly PageSetup A4 = PageSetup.From("A4", "portrait", 18, 16, 18, 16);

    public static string Fixture(string relative) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", relative));

    /// <summary>A printer with a unique marker switch on its Chromium command line, so a test can find and kill that process.</summary>
    public static ChromiumPdfPrinter CreatePrinter(out string marker, Action<PdfPrinterOptions>? configure = null)
    {
        marker = "verso-test-" + Guid.NewGuid().ToString("N");
        var options = new PdfPrinterOptions();
        options.ExtraArgs.Add($"--{marker}");
        configure?.Invoke(options);
        return new ChromiumPdfPrinter(Options.Create(options), NullLogger<ChromiumPdfPrinter>.Instance);
    }

    /// <summary>Renders the invoice fixture layout with the given data (final mode, as generated documents are).</summary>
    public static PrintDocument InvoicePrint(JsonNode data)
    {
        var (layout, errors) = LayoutParser.Parse(Fixture("invoice/layout.json"));
        Assert.True(layout is not null, string.Join("; ", errors.Select(e => e.Message)));
        using var doc = JsonDocument.Parse(data.ToJsonString());
        var result = HtmlRenderer.Render(layout!, A4, doc.RootElement.Clone(), RenderMode.Final, "Invoice");
        return result.Print;
    }

    public static JsonNode Sample() => JsonNode.Parse(Fixture("invoice/sample.json"))!;

    /// <summary>The sample invoice with <paramref name="count"/> synthetic lines.</summary>
    public static JsonNode SampleWithLines(int count)
    {
        var data = Sample();
        var lines = new JsonArray();
        for (var i = 1; i <= count; i++)
        {
            lines.Add(new JsonObject
            {
                ["description"] = $"Synthetic service line {i:00}",
                ["qty"] = i,
                ["unit"] = "pcs",
                ["unitPrice"] = 10.5,
                ["vatRate"] = 19,
                ["amount"] = 10.5 * i,
            });
        }

        data["lines"] = lines;
        return data;
    }

    /// <summary>Text per page, words separated by single spaces (PdfPig).</summary>
    public static IReadOnlyList<string> PageTexts(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return document.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))).ToList();
    }

    public static IReadOnlySet<string> FontNames(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return document.GetPages().SelectMany(p => p.Letters).Select(l => l.FontName ?? "").ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Force-kills every process whose command line contains the marker (the test's Chromium).</summary>
    public static Task KillProcessesWithMarkerAsync(string marker) =>
        KillWhereAsync($"$_.CommandLine -like '*{marker}*'");

    /// <summary>Force-kills the Playwright driver (node.exe run-driver) started from this test assembly's folder.</summary>
    public static Task KillPlaywrightDriverAsync()
    {
        var folder = AppContext.BaseDirectory.TrimEnd('\\', '/').Replace("'", "''", StringComparison.Ordinal);
        return KillWhereAsync($"$_.Name -eq 'node.exe' -and $_.CommandLine -like '*run-driver*' -and $_.ExecutablePath -like '{folder}*'");
    }

    private static async Task KillWhereAsync(string condition)
    {
        var script = $"Get-CimInstance Win32_Process | Where-Object {{ {condition} }} | ForEach-Object {{ Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue; $_.ProcessId }}";
        var psi = new ProcessStartInfo("powershell", ["-NoProfile", "-NonInteractive", "-Command", script])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.False(string.IsNullOrWhiteSpace(output), $"No process matched {condition}.");
    }
}
