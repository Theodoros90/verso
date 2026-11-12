using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Verso.Rendering;
using Verso.Rendering.PdfCombine;

namespace Verso.UnitTests.PdfCombine;

public sealed class PdfCombineTests
{
    /// <summary>A PDF with one page per width (so pages can be told apart after combining).</summary>
    private static byte[] Pdf(params double[] pageWidths)
    {
        using var document = new PdfDocument();
        foreach (var width in pageWidths)
        {
            var page = document.AddPage();
            page.Width = PdfSharp.Drawing.XUnit.FromPoint(width);
            page.Height = PdfSharp.Drawing.XUnit.FromPoint(400);
        }

        using var stream = new MemoryStream();
        document.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    private static PdfDocument Open(byte[] bytes) => PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);

    [Fact]
    public void JoinsEveryPage_InTheOrderOfTheParts()
    {
        var result = new PdfSharpCombiner().Combine([new("a.pdf", Pdf(100, 110)), new("b.pdf", Pdf(200)), new("c.pdf", Pdf(300, 310))], bookmarks: false);

        Assert.Equal(5, result.PageCount);
        using var combined = Open(result.Content);
        Assert.Equal(5, combined.PageCount);
        Assert.Equal([100, 110, 200, 300, 310], Enumerable.Range(0, 5).Select(i => (int)Math.Round(combined.Pages[i].Width.Point)));
    }

    [Fact]
    public void Bookmarks_AreOnePerPart_PointingAtItsFirstPage()
    {
        var result = new PdfSharpCombiner().Combine([new("invoice.pdf", Pdf(100, 110)), new("terms.pdf", Pdf(200))], bookmarks: true);

        using var combined = Open(result.Content);
        Assert.Equal(["invoice.pdf", "terms.pdf"], combined.Outlines.Select(o => o.Title));
        Assert.Equal(100, (int)Math.Round(combined.Outlines[0].DestinationPage!.Width.Point));
        Assert.Equal(200, (int)Math.Round(combined.Outlines[1].DestinationPage!.Width.Point));
    }

    [Fact]
    public void WithoutBookmarks_NoOutlineIsWritten()
    {
        var result = new PdfSharpCombiner().Combine([new("a.pdf", Pdf(100)), new("b.pdf", Pdf(200))], bookmarks: false);

        using var combined = Open(result.Content);
        Assert.Empty(combined.Outlines);
    }

    [Fact]
    public void APartThatIsNotAPdf_IsNamedInTheError_WithoutItsContent()
    {
        var parts = new PdfPart[] { new("good.pdf", Pdf(100)), new("broken.pdf", "secret customer text"u8.ToArray()) };

        var error = Assert.Throws<PdfCombineException>(() => new PdfSharpCombiner().Combine(parts, bookmarks: true));

        Assert.Contains("broken.pdf", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
    }
}
