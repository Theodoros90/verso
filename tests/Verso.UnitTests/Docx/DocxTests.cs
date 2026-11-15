using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Verso.Rendering;
using Verso.Rendering.Docx;
using Verso.Rendering.Layout;
using static Verso.UnitTests.Rendering.TestFiles;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Verso.UnitTests.Docx;

public sealed class DocxTests
{
    private static ResolvedDocument InvoiceDocument(string? data = null) =>
        DocumentResolver.Resolve(ParseValid(Fixture("invoice/layout.json")), A4, Json(data ?? Fixture("invoice/sample.json")), "Invoice");

    private static WordprocessingDocument Open(byte[] bytes) => WordprocessingDocument.Open(new MemoryStream(bytes), false);

    private static string AllText(byte[] bytes)
    {
        using var doc = Open(bytes);
        var parts = new List<string> { doc.MainDocumentPart!.Document!.InnerText };
        parts.AddRange(doc.MainDocumentPart.HeaderParts.Select(h => h.Header!.InnerText));
        parts.AddRange(doc.MainDocumentPart.FooterParts.Select(f => f.Footer!.InnerText));
        return string.Join("\n", parts);
    }

    // ----- Resolver -----

    [Fact]
    public void Resolver_AppliesDataAndFilters()
    {
        var resolved = InvoiceDocument();

        Assert.DoesNotContain(resolved.Issues, i => i.Severity == IssueSeverity.Error);
        var table = Assert.Single(resolved.Body.OfType<TableBlock>());
        Assert.Equal(3, table.Rows.Count);
        var text = string.Join("\n", resolved.Header.Concat(resolved.Body).Concat(resolved.Footer).OfType<ParagraphBlock>().SelectMany(p => p.Runs).Select(r => r.Text));
        Assert.Contains("INV-2026-003901", text);
        Assert.Contains("Halcyon Energy Ltd", text);
    }

    [Fact]
    public void Resolver_ReportsMissingValues_LikeTheHtmlRenderer()
    {
        var layout = ParseValid(Fixture("invoice/layout.json"));
        var data = Json("""{ "invoice": { "number": "X" } }""");

        var resolved = DocumentResolver.Resolve(layout, A4, data, "Invoice");
        var html = Verso.Rendering.Html.HtmlRenderer.Render(layout, A4, data, RenderMode.Final, "Invoice");

        Assert.NotEmpty(resolved.Issues);
        Assert.Equal(
            html.Issues.Select(i => (i.Severity, i.Code, i.ElementId, i.Path)).OrderBy(i => i.ToString()),
            resolved.Issues.Select(i => (i.Severity, i.Code, i.ElementId, i.Path)).OrderBy(i => i.ToString()));
    }

    [Fact]
    public void Resolver_DoesNotReEvaluateDataThatLooksLikeABinding()
    {
        var layout = ParseValid("""{ "layoutSchemaVersion": 1, "body": { "blocks": [ { "id": "t", "type": "text", "props": { "text": "Hi {{name}}" } } ] } }""");

        var resolved = DocumentResolver.Resolve(layout, A4, Json("""{ "name": "{{secret.value}}" }"""), "T");

        Assert.Equal("Hi {{secret.value}}", Assert.IsType<ParagraphBlock>(Assert.Single(resolved.Body)).Runs.Single().Text);
        Assert.Empty(resolved.Issues);
    }

    // ----- Writer -----

    [Fact]
    public void Writer_ProducesValidWordDocument_WithHeaderFooterAndContent()
    {
        var bytes = new DocxWriter().Write(InvoiceDocument(), DocxProtection.AllowEdits);

        using var doc = Open(bytes);
        var errors = new OpenXmlValidator().Validate(doc).Select(e => $"{e.Path?.XPath}: {e.Description}").ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
        Assert.NotEmpty(doc.MainDocumentPart!.HeaderParts);
        Assert.NotEmpty(doc.MainDocumentPart.FooterParts);
        Assert.Equal("Invoice", doc.PackageProperties.Title);

        var text = AllText(bytes);
        Assert.Contains("INV-2026-003901", text);
        Assert.Contains("Electricity supply, October 2026", text);
        Assert.Contains("€5,516.69", text);
        Assert.Equal(3 + 1, doc.MainDocumentPart.Document!.Descendants<W.TableRow>().Count()); // headings + 3 lines
        Assert.NotNull(doc.MainDocumentPart.Document.Descendants<W.TableHeader>().FirstOrDefault()); // headings repeat on each page
    }

    [Theory]
    [InlineData(DocxProtection.AllowEdits, false, false)]
    [InlineData(DocxProtection.ReadOnly, true, false)]
    [InlineData(DocxProtection.TrackedChangesOnly, true, true)]
    public void Writer_AppliesEditingRestriction(DocxProtection protection, bool enforced, bool tracking)
    {
        using var doc = Open(new DocxWriter().Write(InvoiceDocument(), protection));

        var settings = doc.MainDocumentPart!.DocumentSettingsPart!.Settings!;
        var element = settings.GetFirstChild<W.DocumentProtection>();
        Assert.Equal(enforced, element is not null);
        if (element is not null)
        {
            Assert.True(element.Enforcement!.Value);
            Assert.Equal(protection == DocxProtection.ReadOnly ? W.DocumentProtectionValues.ReadOnly : W.DocumentProtectionValues.TrackedChanges, element.Edit!.Value);
        }

        Assert.Equal(tracking, settings.GetFirstChild<W.TrackRevisions>() is not null);
    }

    [Fact]
    public void Writer_EmbedsImages()
    {
        var png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
        var layout = ParseValid($$"""{ "layoutSchemaVersion": 1, "header": { "heightMm": 20, "elements": [ { "id": "h", "type": "image", "xMm": 0, "yMm": 0, "widthMm": 20, "heightMm": 10, "props": { "src": "{{png}}", "alt": "Logo" } } ] }, "body": { "blocks": [ { "id": "b", "type": "image", "props": { "src": "{{png}}", "alt": "Body" } } ] } }""");

        var bytes = new DocxWriter().Write(DocumentResolver.Resolve(layout, A4, Json("{}"), "T"), DocxProtection.AllowEdits);

        using var doc = Open(bytes);
        Assert.Empty(new OpenXmlValidator().Validate(doc));
        Assert.Single(doc.MainDocumentPart!.ImageParts);
        Assert.Single(doc.MainDocumentPart.HeaderParts.Single().ImageParts);
    }

    [Fact]
    public void Writer_KeepsMarkupLikeDataAsText_AndDropsControlCharacters()
    {
        var layout = ParseValid("""{ "layoutSchemaVersion": 1, "body": { "blocks": [ { "id": "f", "type": "field", "props": { "binding": "v" } } ] } }""");
        var resolved = DocumentResolver.Resolve(layout, A4, Json("""{ "v": "a\u0001b\u0008c <script>x</script> & \"q\"\nline2" }"""), "T <b>");

        var bytes = new DocxWriter().Write(resolved, DocxProtection.AllowEdits);

        using var doc = Open(bytes);
        Assert.Empty(new OpenXmlValidator().Validate(doc));
        Assert.Contains("abc <script>x</script> & \"q\"line2", AllText(bytes)); // the line break is a <w:br/>, which has no text
    }

    [Fact]
    public void Writer_EmptyList_ShowsEmptyText()
    {
        var layout = ParseValid("""{ "layoutSchemaVersion": 1, "body": { "blocks": [ { "id": "t", "type": "table", "props": { "binding": "l", "emptyText": "Nothing here", "columns": [ { "header": "A", "binding": "a" }, { "header": "B", "binding": "b" } ] } } ] } }""");
        var bytes = new DocxWriter().Write(DocumentResolver.Resolve(layout, A4, Json("""{ "l": [] }"""), "T"), DocxProtection.AllowEdits);

        using var doc = Open(bytes);
        Assert.Empty(new OpenXmlValidator().Validate(doc));
        Assert.Contains("Nothing here", AllText(bytes));
    }

    [Fact]
    public void Writer_EmptyLayout_IsStillAValidDocument()
    {
        var resolved = DocumentResolver.Resolve(TemplateLayout.Empty, A4, Json("{}"), "Empty");

        using var doc = Open(new DocxWriter().Write(resolved, DocxProtection.AllowEdits));

        Assert.Empty(new OpenXmlValidator().Validate(doc));
        Assert.Empty(doc.MainDocumentPart!.HeaderParts);
    }

    [Fact]
    public void Writer_LandscapePage_SwapsOrientation()
    {
        var landscape = PageSetup.From("A4", "landscape", 10, 10, 10, 10);

        using var doc = Open(new DocxWriter().Write(DocumentResolver.Resolve(TemplateLayout.Empty, landscape, Json("{}"), "L"), DocxProtection.AllowEdits));

        var size = doc.MainDocumentPart!.Document!.Descendants<W.PageSize>().Single();
        Assert.Equal(W.PageOrientationValues.Landscape, size.Orient!.Value);
        Assert.True(size.Width!.Value > size.Height!.Value);
    }

    [Fact]
    public void ColumnWidths_UseGivenShares_AndSplitTheRest()
    {
        var widths = DocxWriter.ColumnWidths([new("A", TextAlign.Left, 50), new("B", TextAlign.Left, null), new("C", TextAlign.Left, null)], 10000);

        Assert.Equal([5000, 2500, 2500], widths);
    }

    [Fact]
    public void FooterPageTokens_BecomeLiveWordFields_WithTheTextAroundThem()
    {
        var layout = ParseValid("""{ "layoutSchemaVersion": 1, "footer": { "heightMm": 10, "elements": [ { "id": "f", "type": "text", "xMm": 0, "yMm": 0, "widthMm": 80, "heightMm": 6, "props": { "text": "Page {{page}} of {{pages}}" } } ] } }""");
        var resolved = DocumentResolver.Resolve(layout, A4, Json("{}"), "T");

        Assert.DoesNotContain(resolved.Issues, i => i.Severity == IssueSeverity.Error);
        var runs = Assert.IsType<ParagraphBlock>(Assert.Single(resolved.Footer)).Runs;
        Assert.Equal(["Page ", "", " of ", ""], runs.Select(r => r.Text));
        Assert.Equal([null, PageField.Page, null, PageField.Pages], runs.Select(r => r.Field));

        using var doc = Open(new DocxWriter().Write(resolved, DocxProtection.AllowEdits));
        var footer = doc.MainDocumentPart!.FooterParts.Single().Footer!;
        Assert.Equal(["PAGE", "NUMPAGES"], footer.Descendants<W.SimpleField>().Select(f => f.Instruction!.Value));
        Assert.Empty(new OpenXmlValidator().Validate(doc));
    }

    [Fact]
    public void ImageDimensions_ReadPngHeader()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

        Assert.Equal((1, 1), ImageDimensions.Read(png, true));
        Assert.Null(ImageDimensions.Read([1, 2, 3], true));
        Assert.Null(ImageDimensions.Read([0xFF, 0xD8, 0xFF], false));
    }

    // ----- Header and footer page rules -----

    private static byte[] WithRules(string header, string footer)
    {
        var layout = ParseValid($$"""
            { "layoutSchemaVersion": 1,
              "header": { "heightMm": 10, "showOn": "{{header}}", "elements": [ { "id": "h", "type": "text", "xMm": 0, "yMm": 0, "widthMm": 50, "heightMm": 6, "props": { "text": "HEAD" } } ] },
              "body": { "blocks": [ { "id": "b", "type": "text", "props": { "text": "BODY" } } ] },
              "footer": { "heightMm": 10, "showOn": "{{footer}}", "elements": [ { "id": "f", "type": "text", "xMm": 0, "yMm": 0, "widthMm": 50, "heightMm": 6, "props": { "text": "FOOT" } } ] } }
            """);
        return new DocxWriter().Write(DocumentResolver.Resolve(layout, A4, Json("{}"), "T"), DocxProtection.AllowEdits);
    }

    private static (bool TitlePage, string Default, string First) HeaderParts(byte[] bytes)
    {
        using var doc = Open(bytes);
        var main = doc.MainDocumentPart!;
        var section = main.Document!.Body!.Elements<W.SectionProperties>().Single();
        string Text(W.HeaderFooterValues type)
        {
            var id = section.Elements<W.HeaderReference>().SingleOrDefault(r => r.Type?.Value == type)?.Id?.Value;
            return id is null ? "(none)" : ((HeaderPart)main.GetPartById(id)).Header!.InnerText;
        }

        return (section.Elements<W.TitlePage>().Any(), Text(W.HeaderFooterValues.Default), Text(W.HeaderFooterValues.First));
    }

    private static (bool TitlePage, string Default, string First) FooterParts(byte[] bytes)
    {
        using var doc = Open(bytes);
        var main = doc.MainDocumentPart!;
        var section = main.Document!.Body!.Elements<W.SectionProperties>().Single();
        string Text(W.HeaderFooterValues type)
        {
            var id = section.Elements<W.FooterReference>().SingleOrDefault(r => r.Type?.Value == type)?.Id?.Value;
            return id is null ? "(none)" : ((FooterPart)main.GetPartById(id)).Footer!.InnerText;
        }

        return (section.Elements<W.TitlePage>().Any(), Text(W.HeaderFooterValues.Default), Text(W.HeaderFooterValues.First));
    }

    [Fact]
    public void Header_OnTheFirstPageOnly_UsesWordsDifferentFirstPage_WithAnEmptyHeaderAfterwards()
    {
        var (titlePage, defaultText, firstText) = HeaderParts(WithRules("first", "all"));

        Assert.True(titlePage);
        Assert.Equal("", defaultText);
        Assert.Equal("HEAD", firstText);
    }

    [Fact]
    public void DifferentFirstPage_ProducesAFileWordAccepts_SchemaValid()
    {
        using var doc = Open(WithRules("first", "not-first"));

        Assert.Empty(new OpenXmlValidator().Validate(doc));
    }

    [Fact]
    public void Footer_OnAllButTheFirstPage_IsEmptyOnPageOne()
    {
        var (titlePage, defaultText, firstText) = FooterParts(WithRules("all", "not-first"));

        Assert.True(titlePage);
        Assert.Equal("FOOT", defaultText);
        Assert.Equal("", firstText);
    }

    [Fact]
    public void NoRule_IsOneHeaderAndFooterForEveryPage_WithNoDifferentFirstPage()
    {
        var bytes = WithRules("all", "all");

        Assert.Equal((false, "HEAD", "(none)"), HeaderParts(bytes));
        Assert.Equal((false, "FOOT", "(none)"), FooterParts(bytes));
    }

    [Fact]
    public void Footer_OnTheLastPageOnly_IsWrittenAtTheEndOfTheDocument()
    {
        var bytes = WithRules("all", "last");
        using var doc = Open(bytes);

        Assert.Equal((false, "(none)", "(none)"), FooterParts(bytes)); // no repeating footer at all
        var body = doc.MainDocumentPart!.Document!.Body!.InnerText;
        Assert.True(body.IndexOf("BODY", StringComparison.Ordinal) < body.IndexOf("FOOT", StringComparison.Ordinal), "the footer text follows the body");
    }

    [Fact]
    public void Header_OnAllButTheLastPage_StaysOnEveryPage_WithAWarning()
    {
        var layout = ParseValid("""{ "layoutSchemaVersion": 1, "header": { "heightMm": 10, "showOn": "not-last", "elements": [ { "id": "h", "type": "text", "xMm": 0, "yMm": 0, "widthMm": 50, "heightMm": 6, "props": { "text": "HEAD" } } ] } }""");
        var resolved = DocumentResolver.Resolve(layout, A4, Json("{}"), "T");

        Assert.Contains(resolved.Issues, i => i is { Severity: IssueSeverity.Warning, Code: "band.page_rule_unsupported", ElementId: "header" });
        Assert.DoesNotContain(resolved.Issues, i => i.Severity == IssueSeverity.Error);
        Assert.Equal((false, "HEAD", "(none)"), HeaderParts(new DocxWriter().Write(resolved, DocxProtection.AllowEdits)));
    }
}
