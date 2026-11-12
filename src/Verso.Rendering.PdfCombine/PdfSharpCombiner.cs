using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Verso.Rendering.PdfCombine;

/// <summary>Copies the pages of each part, in order, into a new PDF; optionally adds one bookmark per part pointing at its first page.</summary>
public sealed class PdfSharpCombiner : IPdfCombiner
{
    public CombinedPdf Combine(IReadOnlyList<PdfPart> parts, bool bookmarks)
    {
        ArgumentNullException.ThrowIfNull(parts);
        using var output = new PdfDocument();
        output.Info.Title = "Combined document";

        foreach (var part in parts)
        {
            PdfDocument input;
            try
            {
                input = PdfReader.Open(new MemoryStream(part.Content, writable: false), PdfDocumentOpenMode.Import);
            }
            catch (Exception ex) when (ex is PdfReaderException or InvalidOperationException or NotSupportedException or ArgumentException or IOException)
            {
                throw new PdfCombineException($"'{part.Title}' can't be read as a PDF.");
            }

            using (input)
            {
                if (input.PageCount == 0)
                {
                    throw new PdfCombineException($"'{part.Title}' has no pages.");
                }

                PdfPage? first = null;
                for (var i = 0; i < input.PageCount; i++)
                {
                    var added = output.AddPage(input.Pages[i]);
                    first ??= added;
                }

                if (bookmarks)
                {
                    output.Outlines.Add(part.Title, first!, opened: false);
                }
            }
        }

        var pageCount = output.PageCount; // PDFsharp refuses to answer once the document has been saved
        using var stream = new MemoryStream();
        output.Save(stream, closeStream: false);
        return new CombinedPdf(stream.ToArray(), pageCount);
    }
}
