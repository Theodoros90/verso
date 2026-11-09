namespace Verso.Rendering;

public enum RenderMode
{
    /// <summary>Designer/preview: unresolved bindings show as visible <c>{{path}}</c> placeholders.</summary>
    Preview,

    /// <summary>Generated documents: unresolved bindings render empty. Issues are still reported so callers can reject the document.</summary>
    Final,
}

public enum IssueSeverity
{
    /// <summary>The document would be wrong or incomplete (e.g. a missing value).</summary>
    Error,

    /// <summary>The document renders, but something looks off (e.g. a value that a filter couldn't format).</summary>
    Warning,
}

/// <summary>A problem found while rendering. Stable <see cref="Code"/>s are listed in docs/layout-model.md.</summary>
public sealed record RenderIssue(IssueSeverity Severity, string Code, string Message, string ElementId, string? Path);

/// <summary>
/// The parts Chromium needs to print a paginated document: the flowing body, plus header and footer templates
/// that Chromium repeats in the top/bottom page margins of every page (Page.printToPDF <c>headerTemplate</c> /
/// <c>footerTemplate</c> with <c>displayHeaderFooter</c>). Left/right margins are 0; the side margins are padding
/// inside the parts. Verified with Chromium in M3; used by the PDF step (M5).
/// </summary>
public sealed record PrintDocument(
    string BodyHtml,
    string HeaderTemplate,
    string FooterTemplate,
    decimal PaperWidthMm,
    decimal PaperHeightMm,
    decimal MarginTopMm,
    decimal MarginBottomMm);

/// <param name="Html">Self-contained preview: one continuous sheet with header, body and footer, for screens.</param>
/// <param name="Print">The same content split for paginated printing.</param>
public sealed record RenderResult(string Html, PrintDocument Print, IReadOnlyList<RenderIssue> Issues);

/// <summary>Physical page: size and margins in millimetres.</summary>
public sealed record PageSetup(decimal WidthMm, decimal HeightMm, decimal MarginTopMm, decimal MarginRightMm, decimal MarginBottomMm, decimal MarginLeftMm)
{
    public static PageSetup From(string size, string orientation, decimal top, decimal right, decimal bottom, decimal left)
    {
        var (w, h) = size switch
        {
            "A4" => (210m, 297m),
            _ => throw new ArgumentOutOfRangeException(nameof(size), size, "Unsupported page size."),
        };
        return orientation == "landscape" ? new(h, w, top, right, bottom, left) : new(w, h, top, right, bottom, left);
    }
}
