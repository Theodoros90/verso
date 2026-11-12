using Verso.Rendering.Layout;

namespace Verso.Rendering.Docx;

// A layout with its data already applied: plain text, no bindings. The DOCX writer (Verso.Rendering.Docx, Worker only)
// maps this to Word; it never sees layout JSON or data, so it can't evaluate anything.

/// <summary>Word's editing restriction for the finished file. Advisory: it has no password, so a person can lift it in Word.</summary>
public enum DocxProtection
{
    AllowEdits,
    TrackedChangesOnly,
    ReadOnly,
}

public sealed record ResolvedRun(string Text, bool Bold);

public abstract record ResolvedBlock(decimal SpaceBeforeMm);

/// <summary>Runs share one style; a <c>\n</c> in a run's text is a line break.</summary>
public sealed record ParagraphBlock(IReadOnlyList<ResolvedRun> Runs, TextStyle Style, decimal SpaceBeforeMm) : ResolvedBlock(SpaceBeforeMm);

public sealed record ResolvedColumn(string Header, TextAlign Align, decimal? WidthPct);

public sealed record TableBlock(IReadOnlyList<ResolvedColumn> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, string? EmptyText, TextStyle Style, decimal SpaceBeforeMm) : ResolvedBlock(SpaceBeforeMm);

public sealed record DividerBlock(decimal ThicknessPt, string Color, decimal SpaceBeforeMm) : ResolvedBlock(SpaceBeforeMm);

/// <param name="WidthMm">The element's width (header/footer), or null in the body, where the image keeps its own size up to the content width.</param>
public sealed record ImageBlock(byte[] Bytes, bool IsPng, string Alt, decimal? WidthMm, decimal? HeightMm, TextAlign Align, decimal SpaceBeforeMm) : ResolvedBlock(SpaceBeforeMm);

public sealed record SignatureBlock(string Label, decimal SpaceBeforeMm) : ResolvedBlock(SpaceBeforeMm);

/// <summary>
/// Header and footer blocks repeat on every page (Word header/footer parts). Positioned elements become blocks in reading order
/// (top to bottom, then left to right): Word has no absolute positioning inside a header, so exact placement isn't kept.
/// </summary>
public sealed record ResolvedDocument(
    string Title,
    PageSetup Page,
    IReadOnlyList<ResolvedBlock> Header,
    IReadOnlyList<ResolvedBlock> Body,
    IReadOnlyList<ResolvedBlock> Footer,
    IReadOnlyList<RenderIssue> Issues);

/// <summary>Writes a <see cref="ResolvedDocument"/> as a .docx file. The contract lives here (pure); the implementation is wired only in the Worker.</summary>
public interface IDocxWriter
{
    byte[] Write(ResolvedDocument document, DocxProtection protection);
}
