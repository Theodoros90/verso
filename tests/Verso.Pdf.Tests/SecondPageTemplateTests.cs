using System.Text.Json;
using System.Text.Json.Nodes;
using Verso.Rendering;
using Verso.Rendering.Html;
using Verso.Rendering.Layout;
using static Verso.Pdf.Tests.PdfTestSupport;

namespace Verso.Pdf.Tests;

/// <summary>
/// The two templates of the XML invoice batch sample (tests/fixtures/xml-batch) always print two pages in real Chromium:
/// the layout model has no page-break element, so a page-2 section follows two invisible full-height spacer blocks.
/// </summary>
public sealed class SecondPageTemplateTests(PrinterFixture fixture) : IClassFixture<PrinterFixture>
{
    private static JsonNode Data(int lines = 2)
    {
        var data = JsonNode.Parse(Fixture("xml-batch/sample-data.json"))!;
        if (lines != 2)
        {
            var rows = new JsonArray();
            for (var i = 1; i <= lines; i++)
            {
                rows.Add(new JsonObject { ["description"] = $"Synthetic service line {i:00}", ["qty"] = i, ["unit"] = "pcs", ["unitPrice"] = 10.5, ["vatRate"] = 19, ["amount"] = 10.5 * i });
            }

            data["lines"] = rows;
        }

        return data;
    }

    private async Task<IReadOnlyList<string>> PrintAsync(string layoutFixture, JsonNode data)
    {
        var (layout, errors) = LayoutParser.Parse(Fixture(layoutFixture));
        Assert.True(layout is not null, string.Join("; ", errors.Select(e => e.Message)));
        using var doc = JsonDocument.Parse(data.ToJsonString());
        var result = HtmlRenderer.Render(layout!, A4, doc.RootElement.Clone(), RenderMode.Final, "Sample");
        Assert.Empty(result.Issues);
        var output = await fixture.Printer.PrintAsync(result.Print, CancellationToken.None);
        return PageTexts(output.Content);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(8)]
    public async Task Invoice_PrintsTwoPages_TheInvoiceThenThePaymentInstructions(int lines)
    {
        var pages = await PrintAsync("xml-batch/invoice.layout.json", Data(lines));

        Assert.Equal(2, pages.Count);
        Assert.Contains("INV-2026-004102", pages[0], StringComparison.Ordinal);
        Assert.Contains("Total due", pages[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Payment instructions", pages[0], StringComparison.Ordinal);
        Assert.Contains("Payment instructions", pages[1], StringComparison.Ordinal);
        Assert.Contains("TESTCY2N", pages[1], StringComparison.Ordinal);
        Assert.Contains("Halcyon Energy Ltd", pages[1], StringComparison.Ordinal); // header and footer repeat
    }

    [Fact]
    public async Task VatNotice_PrintsTwoPages_TheNoticeThenTheCustomerDeclaration()
    {
        var pages = await PrintAsync("xml-batch/notice.layout.json", Data());

        Assert.Equal(2, pages.Count);
        Assert.Contains("Supply to a customer in another member state", pages[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Customer declaration", pages[0], StringComparison.Ordinal);
        Assert.Contains("Customer declaration", pages[1], StringComparison.Ordinal);
        Assert.Contains("DE811569869", pages[1], StringComparison.Ordinal);
    }
}
