namespace Verso.Rendering;

/// <summary>
/// Gives headers and footers a page rule ("first page only", "last page only", "all but the first"…).
/// Chromium repeats a header or footer template on every page and has no per-page condition, so when a rule is set
/// this prints the document once without bands to learn the page count, then prints each run of pages that share
/// the same bands (<see cref="PrintDocument.PageRanges"/>) and joins the runs into one PDF. The margins stay the same in
/// every print, so the body breaks onto pages exactly as it would with the bands, and a page whose band is off simply
/// leaves that space empty. Documents with no rule take the original single print.
/// </summary>
public sealed class PageRulePdfPrinter(IPdfPrinter inner, IPdfCombiner combiner) : IPdfPrinter
{
    /// <summary>A template that prints nothing. (An empty string would make Chromium fall back to its default date and title.)</summary>
    internal const string Blank = "<span></span>";

    public async Task<PdfOutput> PrintAsync(PrintDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.HeaderShow == PageVisibility.All && document.FooterShow == PageVisibility.All)
        {
            return await inner.PrintAsync(document, cancellationToken).ConfigureAwait(false);
        }

        var probe = await inner.PrintAsync(document with { HeaderTemplate = Blank, FooterTemplate = Blank, PageRanges = null }, cancellationToken).ConfigureAwait(false);
        var total = combiner.Combine([new PdfPart("Document", probe.Content)], bookmarks: false).PageCount;

        var runs = Runs(document, total);
        if (runs.Count == 1 && !runs[0].Header && !runs[0].Footer)
        {
            return probe; // the rule hides both bands on every page
        }

        var parts = new List<PdfPart>();
        var blocked = probe.BlockedRequests;
        var attempts = probe.Attempts;
        foreach (var run in runs)
        {
            var printed = await inner.PrintAsync(
                document with
                {
                    HeaderTemplate = run.Header ? document.HeaderTemplate : Blank,
                    FooterTemplate = run.Footer ? document.FooterTemplate : Blank,
                    PageRanges = run.From == run.To ? $"{run.From}" : $"{run.From}-{run.To}",
                },
                cancellationToken).ConfigureAwait(false);
            parts.Add(new PdfPart($"Pages {run.From}-{run.To}", printed.Content));
            blocked += printed.BlockedRequests;
            attempts = Math.Max(attempts, printed.Attempts);
        }

        var combined = combiner.Combine(parts, bookmarks: false);
        if (combined.PageCount != total)
        {
            // The print changed the pagination between runs (it shouldn't: same body, same margins). Better to fail than ship a wrong page count.
            throw new InvalidOperationException($"The document printed as {combined.PageCount} pages in parts but {total} pages whole.");
        }

        return new PdfOutput(combined.Content, blocked, attempts);
    }

    /// <summary>Consecutive pages that show the same bands, as page ranges (1-based, inclusive).</summary>
    internal static List<(int From, int To, bool Header, bool Footer)> Runs(PrintDocument document, int total)
    {
        var runs = new List<(int From, int To, bool Header, bool Footer)>();
        for (var page = 1; page <= total; page++)
        {
            var header = document.HeaderShow.IsVisible(page, total);
            var footer = document.FooterShow.IsVisible(page, total);
            if (runs.Count > 0 && runs[^1].Header == header && runs[^1].Footer == footer)
            {
                runs[^1] = runs[^1] with { To = page };
            }
            else
            {
                runs.Add((page, page, header, footer));
            }
        }

        return runs;
    }
}
