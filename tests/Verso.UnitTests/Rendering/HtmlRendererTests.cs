using Verso.Rendering;
using Verso.Rendering.Html;
using Verso.Rendering.Layout;
using static Verso.UnitTests.Rendering.TestFiles;

namespace Verso.UnitTests.Rendering;

public sealed class HtmlRendererTests
{
    private const string Data = """
        { "invoice": { "number": "INV-1", "date": "2026-11-02" },
          "totals": { "gross": 5516.69 },
          "lines": [ { "description": "Supply", "amount": 10.5 }, { "description": "Rental", "amount": 2 } ],
          "empty": [] }
        """;

    // ----- Golden HTML, one per element type -----

    public static TheoryData<string, string> BodyElements => new()
    {
        { "text-static", """{ "id": "t1", "type": "text", "props": { "text": "Thank you\nfor your business" } }""" },
        { "text-tokens", """{ "id": "t2", "type": "text", "props": { "text": "Invoice {{invoice.number}} dated {{invoice.date|date}}", "style": { "fontSizePt": 12, "bold": true, "italic": true, "align": "center", "color": "#0A6E8A", "font": "serif" } } }""" },
        { "field-label-filter", """{ "id": "f1", "type": "field", "spaceBeforeMm": 4, "props": { "label": "Total due", "binding": "totals.gross", "filter": "money", "style": { "align": "right" } } }""" },
        { "table-rows", """{ "id": "tb1", "type": "table", "props": { "binding": "lines", "columns": [ { "header": "Description", "binding": "description", "widthPct": 70 }, { "header": "Amount", "binding": "amount", "filter": "money", "align": "right" } ] } }""" },
        { "table-empty", """{ "id": "tb2", "type": "table", "props": { "binding": "empty", "emptyText": "No lines.", "columns": [ { "header": "Description", "binding": "description" } ] } }""" },
        { "divider", """{ "id": "d1", "type": "divider", "props": { "thicknessPt": 1.5, "color": "#C4CDD6" } }""" },
        { "image", """{ "id": "i1", "type": "image", "props": { "src": "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==", "alt": "Logo", "fit": "cover" } }""" },
        { "signature", """{ "id": "s1", "type": "signature", "props": { "label": "Approved by" } }""" },
    };

    [Theory]
    [MemberData(nameof(BodyElements))]
    public void Golden_BodyElement(string name, string blockJson)
    {
        var layout = ParseValid($$"""{ "layoutSchemaVersion": 1, "body": { "blocks": [ {{blockJson}} ] } }""");
        var ctx = new RenderContext(RenderMode.Preview);

        var html = HtmlRenderer.RenderBlock(layout.Body.Blocks[0], Json(Data), ctx);

        Assert.Empty(ctx.Issues);
        AssertGolden("element-" + name, html);
    }

    [Fact]
    public void Golden_PositionedBox()
    {
        var layout = ParseValid("""
            { "layoutSchemaVersion": 1, "header": { "heightMm": 40, "elements": [
              { "id": "b1", "type": "box", "xMm": 2.5, "yMm": 10, "widthMm": 90, "heightMm": 30, "props": { "borderColor": "#C4CDD6", "fill": "#F8FAFB", "radiusMm": 1.5 } } ] } }
            """);

        var html = HtmlRenderer.RenderPositioned(layout.Header.Elements[0], Json(Data), new RenderContext(RenderMode.Preview));

        AssertGolden("element-box-positioned", html);
    }

    [Fact]
    public void Golden_InvoiceDocument()
    {
        var result = RenderInvoice(Fixture("invoice/sample.json"), RenderMode.Final);

        Assert.Empty(result.Issues);
        AssertGolden("invoice-preview", result.Html);
        AssertGolden("invoice-print-body", result.Print.BodyHtml);
        AssertGolden("invoice-print-header", result.Print.HeaderTemplate);
        AssertGolden("invoice-print-footer", result.Print.FooterTemplate);
    }

    // ----- Invoice sample renders all fields -----

    [Fact]
    public void InvoiceSample_RendersEveryField_WithoutIssues()
    {
        var result = RenderInvoice(Fixture("invoice/sample.json"), RenderMode.Preview);

        Assert.Empty(result.Issues);
        string[] expected =
        [
            "Halcyon Energy Ltd", "14 Archbishop Makarios III Ave, 1065 Nicosia, Cyprus", "CY10293847X", "billing@halcyon-energy.example",
            "INV-2026-003901", "02 Nov 2026", "02 Dec 2026", "PO-55120",
            "Kestrel Logistics Ltd", "88 Franklin Roosevelt Ave, 3011 Limassol, Cyprus", "CY99887766K",
            "Electricity supply, October 2026 (commercial tariff)", "18,450", "€0.22", "€4,031.33",
            "Reactive power surcharge", "€592.04", "Meter rental", "€12.50",
            "€4,635.87", "€880.82", "€5,516.69",
            "CY00 0000 0000 0000 0000 0000 0000", "TESTCY2N", "Payment due within 30 days.",
        ];
        Assert.All(expected, value => Assert.Contains(value, result.Html, StringComparison.Ordinal));
        Assert.DoesNotContain("class=\"v-missing\"", result.Html, StringComparison.Ordinal);
        Assert.Contains("Content-Security-Policy", result.Html, StringComparison.Ordinal);
    }

    // ----- Missing bindings -----

    [Fact]
    public void MissingBinding_InPreview_ShowsPlaceholder_AndReportsIssue()
    {
        var result = RenderInvoice("""{ "invoice": { "number": "INV-9" }, "lines": [] }""", RenderMode.Preview);

        Assert.Contains("<span class=\"v-missing\">{{customer.name}}</span>", result.Html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"v-missing\">{{totals.gross|money}}</span>", result.Html, StringComparison.Ordinal);
        Assert.Contains(result.Issues, i => i is { Code: "binding.missing", ElementId: "billto", Path: "customer.name", Severity: IssueSeverity.Error });
        Assert.Contains(result.Issues, i => i is { Code: "binding.missing", ElementId: "gross", Path: "totals.gross" });
        Assert.DoesNotContain(result.Issues, i => i.Path == "invoice.number");
        Assert.Contains("No invoice lines.", result.Html, StringComparison.Ordinal); // empty array → emptyText, not an issue
    }

    [Fact]
    public void MissingBinding_InFinal_RendersNothing_ButStillReportsIssue()
    {
        var result = RenderInvoice("""{ "invoice": { "number": "INV-9" }, "lines": [] }""", RenderMode.Final);

        Assert.DoesNotContain("v-missing\">", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("{{customer.name}}", result.Html, StringComparison.Ordinal);
        Assert.Contains(result.Issues, i => i is { Code: "binding.missing", Path: "customer.name" });
    }

    [Fact]
    public void TableCells_ReportRowSpecificPaths_AndNonListBindings()
    {
        var layout = ParseValid("""
            { "layoutSchemaVersion": 1, "body": { "blocks": [
              { "id": "tb", "type": "table", "props": { "binding": "lines", "columns": [ { "header": "Qty", "binding": "qty" } ] } },
              { "id": "tb2", "type": "table", "props": { "binding": "invoice", "columns": [ { "header": "X", "binding": "x" } ] } } ] } }
            """);

        var result = HtmlRenderer.Render(layout, A4, Json(Data), RenderMode.Preview, "T");

        Assert.Contains(result.Issues, i => i is { Code: "binding.missing", Path: "lines[0].qty" });
        Assert.Contains(result.Issues, i => i is { Code: "binding.missing", Path: "lines[1].qty" });
        Assert.Contains(result.Issues, i => i is { Code: "table.not_array", ElementId: "tb2" });
    }

    [Fact]
    public void ObjectValues_FilterMismatches_AndMalformedTokens_AreReported()
    {
        var layout = ParseValid("""
            { "layoutSchemaVersion": 1, "body": { "blocks": [
              { "id": "a", "type": "field", "props": { "binding": "invoice" } },
              { "id": "b", "type": "field", "props": { "binding": "invoice.number", "filter": "money" } },
              { "id": "c", "type": "text", "props": { "text": "Hello {{ not valid! }} and {{invoice.number|nope}}" } } ] } }
            """);

        var result = HtmlRenderer.Render(layout, A4, Json(Data), RenderMode.Preview, "T");

        Assert.Contains(result.Issues, i => i is { Code: "binding.not_scalar", ElementId: "a" });
        Assert.Contains(result.Issues, i => i is { Code: "filter.invalid_input", ElementId: "b", Severity: IssueSeverity.Warning });
        Assert.Contains("INV-1", result.Html, StringComparison.Ordinal); // shown as is when the filter can't format it
        Assert.Contains(result.Issues, i => i is { Code: "token.malformed", ElementId: "c" });
    }

    // ----- Injection -----

    [Fact]
    public void HostileData_IsEscaped_AndNeverReEvaluated()
    {
        var result = RenderInvoice(Fixture("invoice/hostile.json"), RenderMode.Preview);
        var html = result.Html;

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img src=x", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<svg", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<b>bold</b>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<style>body{display:none}", html, StringComparison.Ordinal);
        Assert.DoesNotContain("\" onmouseover=", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(&#x27;x&#x27;)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("Tom &amp; Jerry &lt;b&gt;bold&lt;/b&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&amp;lt;already-encoded&amp;gt;", html, StringComparison.Ordinal);
        // A data value that looks like a token is printed literally, not resolved.
        Assert.Contains("{{issuer.name}}", html, StringComparison.Ordinal);
    }

    [Fact]
    public void HostileLayoutText_IsEscaped()
    {
        var layout = ParseValid("""
            { "layoutSchemaVersion": 1, "body": { "blocks": [
              { "id": "x", "type": "text", "props": { "text": "</div><script>alert(1)</script>" } },
              { "id": "y", "type": "signature", "props": { "label": "\"><img src=x onerror=alert(2)>" } } ] } }
            """);

        var html = HtmlRenderer.Render(layout, A4, Json("{}"), RenderMode.Preview, "<title>x</title>").Html;

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img src=x", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<title>&lt;title&gt;x&lt;/title&gt;</title>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Landscape_SwapsPageSize()
    {
        var print = HtmlRenderer.Render(TemplateLayout.Empty, PageSetup.From("A4", "landscape", 10, 10, 10, 10), Json("{}"), RenderMode.Final, "T").Print;

        Assert.Equal((297m, 210m), (print.PaperWidthMm, print.PaperHeightMm));
        Assert.Contains("@page{size:297mm 210mm}", print.BodyHtml, StringComparison.Ordinal);
    }

    // ----- Print parts (header/footer repeated by Chromium in the page margins) -----

    [Fact]
    public void Print_SplitsHeaderBodyFooter_AndReservesTheirHeightAsPageMargins()
    {
        var result = RenderInvoice(Fixture("invoice/sample.json"), RenderMode.Final);
        var print = result.Print;

        // Top margin = page margin 18 + header 62; bottom = 18 + footer 10.
        Assert.Equal(80m, print.MarginTopMm);
        Assert.Equal(28m, print.MarginBottomMm);
        Assert.Equal((210m, 297m), (print.PaperWidthMm, print.PaperHeightMm));

        Assert.Contains("Kestrel Logistics Ltd", print.HeaderTemplate, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,", print.HeaderTemplate, StringComparison.Ordinal);
        Assert.Contains("<style>", print.HeaderTemplate, StringComparison.Ordinal); // templates carry their own styles
        Assert.Contains("billing@halcyon-energy.example", print.FooterTemplate, StringComparison.Ordinal);

        Assert.Contains("Reactive power surcharge", print.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Kestrel Logistics Ltd", print.BodyHtml, StringComparison.Ordinal); // header content isn't in the body
        Assert.Contains("Content-Security-Policy", print.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", print.HeaderTemplate + print.FooterTemplate, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HostileData_IsEscaped_InPrintTemplatesToo()
    {
        var print = RenderInvoice(Fixture("invoice/hostile.json"), RenderMode.Final).Print;

        Assert.DoesNotContain("<script", print.HeaderTemplate + print.FooterTemplate + print.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img src=x", print.FooterTemplate, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", print.HeaderTemplate, StringComparison.Ordinal);
    }

    private static RenderResult RenderInvoice(string dataJson, RenderMode mode) =>
        HtmlRenderer.Render(ParseValid(Fixture("invoice/layout.json")), A4, Json(dataJson), mode, "Commercial invoice");
}
