using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Verso.Rendering;
using Verso.Rendering.Html;
using Verso.Rendering.Layout;
using Verso.Rendering.PdfCombine;
using static Verso.Pdf.Tests.PdfTestSupport;

namespace Verso.Pdf.Tests;

/// <summary><c>{{page}}</c> and <c>{{pages}}</c> in a footer, printed by real Chromium: every page shows its own number.</summary>
[Collection("Chromium")]
public sealed partial class PageNumberTests(PrinterFixture fixture) : IClassFixture<PrinterFixture>
{
    private readonly PageRulePdfPrinter _printer = new(fixture.Printer, new PdfSharpCombiner());

    [GeneratedRegex(@"Page (\d+) of (\d+)")]
    private static partial Regex Numbers();

    private static PrintDocument Document(string footerText, string? footerShow, int lines)
    {
        var blocks = new JsonArray();
        for (var i = 1; i <= lines; i++)
        {
            blocks.Add(new JsonObject { ["id"] = $"b{i}", ["type"] = "text", ["props"] = new JsonObject { ["text"] = $"Body line {i:000}" } });
        }

        var footer = new JsonObject
        {
            ["heightMm"] = 12,
            ["elements"] = new JsonArray(new JsonObject
            {
                ["id"] = "pn", ["type"] = "text", ["xMm"] = 0, ["yMm"] = 0, ["widthMm"] = 100, ["heightMm"] = 8,
                ["props"] = new JsonObject { ["text"] = footerText },
            }),
        };
        if (footerShow is not null)
        {
            footer["showOn"] = footerShow;
        }

        var layout = new JsonObject { ["layoutSchemaVersion"] = 1, ["body"] = new JsonObject { ["blocks"] = blocks }, ["footer"] = footer };
        var (parsed, errors) = LayoutParser.Parse(layout.ToJsonString());
        Assert.True(parsed is not null, string.Join("; ", errors.Select(e => e.Message)));
        using var data = JsonDocument.Parse("{}");
        var result = HtmlRenderer.Render(parsed!, A4, data.RootElement.Clone(), RenderMode.Final, "Numbers");
        Assert.DoesNotContain(result.Issues, i => i.Severity == IssueSeverity.Error); // the page tokens are not missing data
        return result.Print;
    }

    private static string[] Footers(IReadOnlyList<string> pages) =>
        [.. pages.Select(p => Numbers().Match(p) is { Success: true } m ? $"{m.Groups[1].Value}/{m.Groups[2].Value}" : "")];

    [Fact]
    public async Task EveryPage_ShowsItsOwnNumberAndTheTotal()
    {
        var output = await _printer.PrintAsync(Document("Page {{page}} of {{pages}}", null, 120), CancellationToken.None);
        var pages = PageTexts(output.Content);

        Assert.True(pages.Count >= 3);
        Assert.Equal([.. Enumerable.Range(1, pages.Count).Select(n => $"{n}/{pages.Count}")], Footers(pages));
    }

    [Fact]
    public async Task WithAPageRule_TheNumbersStillCountTheWholeDocument()
    {
        // not-first prints pages 2.. in a separate run: they must still read "Page 2 of N", not restart at 1.
        var output = await _printer.PrintAsync(Document("Page {{page}} of {{pages}}", "not-first", 120), CancellationToken.None);
        var pages = PageTexts(output.Content);

        Assert.Equal(["", .. Enumerable.Range(2, pages.Count - 1).Select(n => $"{n}/{pages.Count}")], Footers(pages));
    }

    [Fact]
    public async Task LastPageOnly_ShowsTheTotalAsItsOwnNumber()
    {
        var output = await _printer.PrintAsync(Document("Page {{page}} of {{pages}}", "last", 120), CancellationToken.None);
        var pages = PageTexts(output.Content);

        Assert.Equal([.. Enumerable.Repeat("", pages.Count - 1), $"{pages.Count}/{pages.Count}"], Footers(pages));
    }
}
