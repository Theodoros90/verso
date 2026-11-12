namespace Verso.Rendering;

/// <summary>
/// Joins PDFs into one. The contract lives here (pure) so use cases can depend on it; the implementation (PDFsharp, in
/// Verso.Rendering.PdfCombine) is wired only in the Worker. It only ever receives PDFs Verso generated itself.
/// </summary>
public interface IPdfCombiner
{
    /// <param name="parts">In output order. Each part's title is the bookmark text when <paramref name="bookmarks"/> is on.</param>
    /// <exception cref="PdfCombineException">A part can't be read as a PDF.</exception>
    CombinedPdf Combine(IReadOnlyList<PdfPart> parts, bool bookmarks);
}

public sealed record PdfPart(string Title, byte[] Content);

/// <param name="Content">The combined PDF bytes.</param>
/// <param name="PageCount">Pages in the result.</param>
public sealed record CombinedPdf(byte[] Content, int PageCount);

/// <summary>A part could not be read as a PDF. The message names the part's title only, never its content.</summary>
public sealed class PdfCombineException(string message) : Exception(message);
