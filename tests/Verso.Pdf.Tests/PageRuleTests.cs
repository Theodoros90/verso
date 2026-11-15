using System.Text.Json;
using System.Text.Json.Nodes;
using Verso.Rendering;
using Verso.Rendering.Html;
using Verso.Rendering.Layout;
using Verso.Rendering.PdfCombine;
using static Verso.Pdf.Tests.PdfTestSupport;

namespace Verso.Pdf.Tests;

/// <summary>Header and footer page rules, printed by real Chromium and joined by PDFsharp.</summary>
[Collection("Chromium")]
public sealed class PageRuleTests(PrinterFixture fixture) : IClassFixture<PrinterFixture>
{
    private readonly PageRulePdfPrinter _printer = new(fixture.Printer, new PdfSharpCombiner());

    /// <summary>A header and a footer with their own words, over a body of <paramref name="lines"/> text blocks (about 3 pages for 120).</summary>
    private static PrintDocument Document(string? headerShow, string? footerShow, int lines)
    {
        JsonObject Band(string id, string text, string? show, decimal height)
        {
            var band = new JsonObject
            {
                ["heightMm"] = height,
                ["elements"] = new JsonArray(new JsonObject
                {
                    ["id"] = id, ["type"] = "text", ["xMm"] = 0, ["yMm"] = 0, ["widthMm"] = 100, ["heightMm"] = 8,
                    ["props"] = new JsonObject { ["text"] = text },
                }),
            };
            if (show is not null)
            {
                band["showOn"] = show;
            }

            return band;
        }

        var blocks = new JsonArray();
        for (var i = 1; i <= lines; i++)
        {
            blocks.Add(new JsonObject { ["id"] = $"b{i}", ["type"] = "text", ["props"] = new JsonObject { ["text"] = $"Body line {i:000}" } });
        }

        var layout = new JsonObject
        {
            ["layoutSchemaVersion"] = 1,
            ["header"] = Band("hd", "HEADERMARK", headerShow, 14),
            ["body"] = new JsonObject { ["blocks"] = blocks },
            ["footer"] = Band("ft", "FOOTERMARK", footerShow, 14),
        };
        var (parsed, errors) = LayoutParser.Parse(layout.ToJsonString());
        Assert.True(parsed is not null, string.Join("; ", errors.Select(e => e.Message)));
        using var data = JsonDocument.Parse("{}");
        return HtmlRenderer.Render(parsed!, A4, data.RootElement.Clone(), RenderMode.Final, "Rules").Print;
    }

    private static bool[] Has(IReadOnlyList<string> pages, string mark) => [.. pages.Select(p => p.Contains(mark, StringComparison.Ordinal))];

    [Fact]
    public async Task HeaderOnFirstPage_FooterOnLastPage_AreOnlyThere_AndTheBodyIsComplete()
    {
        var output = await _printer.PrintAsync(Document("first", "last", 120), CancellationToken.None);
        var pages = PageTexts(output.Content);

        Assert.True(pages.Count >= 3, $"expected a multi-page document, got {pages.Count} page(s)");
        Assert.Equal([true, .. Enumerable.Repeat(false, pages.Count - 1)], Has(pages, "HEADERMARK"));
        Assert.Equal([.. Enumerable.Repeat(false, pages.Count - 1), true], Has(pages, "FOOTERMARK"));
        var text = string.Join(" ", pages);
        Assert.Contains("Body line 001", text, StringComparison.Ordinal);
        Assert.Contains("Body line 120", text, StringComparison.Ordinal); // no body text lost or repeated between the joined runs
        Assert.Equal(120, System.Text.RegularExpressions.Regex.Count(text, "Body line"));
    }

    [Fact]
    public async Task FooterOnAllButTheFirstPage_AndHeaderOnAllButTheLast()
    {
        var output = await _printer.PrintAsync(Document("not-last", "not-first", 120), CancellationToken.None);
        var pages = PageTexts(output.Content);

        Assert.Equal([.. Enumerable.Repeat(true, pages.Count - 1), false], Has(pages, "HEADERMARK"));
        Assert.Equal([false, .. Enumerable.Repeat(true, pages.Count - 1)], Has(pages, "FOOTERMARK"));
    }

    [Fact]
    public async Task NoRule_RepeatsBothOnEveryPage_AsBefore()
    {
        var output = await _printer.PrintAsync(Document(null, null, 120), CancellationToken.None);
        var pages = PageTexts(output.Content);

        Assert.All(Has(pages, "HEADERMARK"), Assert.True);
        Assert.All(Has(pages, "FOOTERMARK"), Assert.True);
    }

    [Fact]
    public async Task OnASinglePage_FirstAndLastBothApply()
    {
        var output = await _printer.PrintAsync(Document("first", "last", 5), CancellationToken.None);
        var pages = PageTexts(output.Content);

        Assert.Single(pages);
        Assert.Contains("HEADERMARK", pages[0], StringComparison.Ordinal);
        Assert.Contains("FOOTERMARK", pages[0], StringComparison.Ordinal);
    }
}
