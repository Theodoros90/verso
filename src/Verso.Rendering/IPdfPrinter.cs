namespace Verso.Rendering;

/// <summary>
/// Prints a <see cref="PrintDocument"/> to PDF. The contract lives here (pure) so use cases can depend on it;
/// the implementation (Chromium via Playwright, in Verso.Rendering.Pdf) is wired only in the Worker.
/// </summary>
public interface IPdfPrinter
{
    Task<PdfOutput> PrintAsync(PrintDocument document, CancellationToken cancellationToken);
}

/// <param name="Content">The PDF bytes.</param>
/// <param name="BlockedRequests">Requests the page tried to make and that were blocked (none ever leave the machine).</param>
/// <param name="Attempts">How many browser attempts were needed (more than 1 when Chromium crashed and was restarted).</param>
public sealed record PdfOutput(byte[] Content, int BlockedRequests, int Attempts);
