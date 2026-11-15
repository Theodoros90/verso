using Verso.Rendering;
using Verso.Rendering.Layout;

namespace Verso.UnitTests.Rendering;

/// <summary>Header and footer page rules: which pages show which band, and how the printer arranges the prints.</summary>
public sealed class PageRulePdfPrinterTests
{
    private static PrintDocument Doc(PageVisibility header, PageVisibility footer) =>
        new("<body/>", "HEADER", "FOOTER", 210, 297, 40, 30, header, footer);

    /// <summary>Records every print. A "page" is one byte; the PDF of a print is the pages it was asked for.</summary>
    private sealed class FakePrinter(int totalPages) : IPdfPrinter
    {
        public List<PrintDocument> Prints { get; } = [];

        public Task<PdfOutput> PrintAsync(PrintDocument document, CancellationToken cancellationToken)
        {
            Prints.Add(document);
            var (from, to) = document.PageRanges switch
            {
                null => (1, totalPages),
                var r when r.Contains('-', StringComparison.Ordinal) => (Page(r.Split('-')[0]), Page(r.Split('-')[1])),
                var r => (Page(r), Page(r)),
            };
            return Task.FromResult(new PdfOutput(Enumerable.Range(from, to - from + 1).Select(p => (byte)p).ToArray(), 1, 1));
        }
    }

    private static int Page(string text) => int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"Combines" by concatenating, and reads a part's page count as its length.</summary>
    private sealed class FakeCombiner : IPdfCombiner
    {
        public CombinedPdf Combine(IReadOnlyList<PdfPart> parts, bool bookmarks)
        {
            var all = parts.SelectMany(p => p.Content).ToArray();
            return new CombinedPdf(all, all.Length);
        }
    }

    [Fact]
    public async Task NoRule_IsOneUntouchedPrint()
    {
        var inner = new FakePrinter(5);
        var doc = Doc(PageVisibility.All, PageVisibility.All);

        var output = await new PageRulePdfPrinter(inner, new FakeCombiner()).PrintAsync(doc, CancellationToken.None);

        Assert.Equal([doc], inner.Prints);
        Assert.Equal(5, output.Content.Length);
    }

    [Fact]
    public async Task FooterOnLastPage_PrintsTheEarlierPagesWithoutIt_AndTheLastWithIt()
    {
        var inner = new FakePrinter(4);

        var output = await new PageRulePdfPrinter(inner, new FakeCombiner()).PrintAsync(Doc(PageVisibility.All, PageVisibility.Last), CancellationToken.None);

        // probe (no bands), pages 1-3 (header only), page 4 (header and footer)
        Assert.Collection(
            inner.Prints,
            p => Assert.Equal((PageRulePdfPrinter_Blank, PageRulePdfPrinter_Blank, null), (p.HeaderTemplate, p.FooterTemplate, p.PageRanges)),
            p => Assert.Equal(("HEADER", PageRulePdfPrinter_Blank, "1-3"), (p.HeaderTemplate, p.FooterTemplate, p.PageRanges)),
            p => Assert.Equal(("HEADER", "FOOTER", "4"), (p.HeaderTemplate, p.FooterTemplate, p.PageRanges)));
        Assert.Equal([1, 2, 3, 4], output.Content.Select(b => (int)b));
    }

    [Fact]
    public async Task HeaderOnFirstAndFooterOnLast_NeedThreeRuns()
    {
        var inner = new FakePrinter(5);

        await new PageRulePdfPrinter(inner, new FakeCombiner()).PrintAsync(Doc(PageVisibility.First, PageVisibility.Last), CancellationToken.None);

        Assert.Equal(["1", "2-4", "5"], inner.Prints.Skip(1).Select(p => p.PageRanges));
        Assert.Equal(["HEADER", PageRulePdfPrinter_Blank, PageRulePdfPrinter_Blank], inner.Prints.Skip(1).Select(p => p.HeaderTemplate));
        Assert.Equal([PageRulePdfPrinter_Blank, PageRulePdfPrinter_Blank, "FOOTER"], inner.Prints.Skip(1).Select(p => p.FooterTemplate));
    }

    [Fact]
    public async Task FooterOnAllButTheFirstPage_IsTwoRuns()
    {
        var inner = new FakePrinter(3);

        await new PageRulePdfPrinter(inner, new FakeCombiner()).PrintAsync(Doc(PageVisibility.All, PageVisibility.NotFirst), CancellationToken.None);

        Assert.Equal(["1", "2-3"], inner.Prints.Skip(1).Select(p => p.PageRanges));
        Assert.Equal([PageRulePdfPrinter_Blank, "FOOTER"], inner.Prints.Skip(1).Select(p => p.FooterTemplate));
    }

    [Fact]
    public async Task OnASinglePageDocument_FirstAndLastBothShow_AndNotFirstHides()
    {
        var shown = new FakePrinter(1);
        await new PageRulePdfPrinter(shown, new FakeCombiner()).PrintAsync(Doc(PageVisibility.First, PageVisibility.Last), CancellationToken.None);
        Assert.Equal(("HEADER", "FOOTER", "1"), (shown.Prints[1].HeaderTemplate, shown.Prints[1].FooterTemplate, shown.Prints[1].PageRanges));

        var hidden = new FakePrinter(1);
        var output = await new PageRulePdfPrinter(hidden, new FakeCombiner()).PrintAsync(Doc(PageVisibility.NotFirst, PageVisibility.NotLast), CancellationToken.None);
        Assert.Single(hidden.Prints); // both bands are off on the only page: the probe is the result
        Assert.Equal([1], output.Content.Select(b => (int)b));
    }

    [Theory]
    [InlineData(PageVisibility.All, 1, 3, true)]
    [InlineData(PageVisibility.First, 1, 3, true)]
    [InlineData(PageVisibility.First, 2, 3, false)]
    [InlineData(PageVisibility.Last, 3, 3, true)]
    [InlineData(PageVisibility.Last, 2, 3, false)]
    [InlineData(PageVisibility.NotFirst, 1, 3, false)]
    [InlineData(PageVisibility.NotFirst, 3, 3, true)]
    [InlineData(PageVisibility.NotLast, 3, 3, false)]
    [InlineData(PageVisibility.NotLast, 1, 3, true)]
    public void IsVisible_FollowsTheRule(PageVisibility rule, int page, int total, bool expected) =>
        Assert.Equal(expected, rule.IsVisible(page, total));

    [Fact]
    public void ShowOn_IsParsedFromLayoutJson_AndBadValuesAreErrors()
    {
        var (layout, errors) = LayoutParser.Parse("""{"layoutSchemaVersion":1,"header":{"heightMm":10,"showOn":"first","elements":[]},"footer":{"heightMm":10,"showOn":"not-first","elements":[]}}""");
        Assert.Empty(errors);
        Assert.Equal((PageVisibility.First, PageVisibility.NotFirst), (layout!.Header.ShowOn, layout.Footer.ShowOn));

        var (none, _) = LayoutParser.Parse("""{"layoutSchemaVersion":1,"footer":{"heightMm":10,"elements":[]}}""");
        Assert.Equal(PageVisibility.All, none!.Footer.ShowOn);

        var (bad, badErrors) = LayoutParser.Parse("""{"layoutSchemaVersion":1,"footer":{"heightMm":10,"showOn":"sometimes","elements":[]}}""");
        Assert.Null(bad);
        Assert.Contains(badErrors, e => e.Path == "footer.showOn");
    }

    private const string PageRulePdfPrinter_Blank = "<span></span>";
}
