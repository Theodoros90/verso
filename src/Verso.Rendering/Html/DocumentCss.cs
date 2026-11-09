using Verso.Rendering.Layout;
using static Verso.Rendering.Html.HtmlRenderer;

namespace Verso.Rendering.Html;

/// <summary>
/// CSS for the three outputs. Element styles are shared; each output adds its own frame:
/// the screen preview (one continuous sheet), the print body (flows across pages), and the header/footer templates
/// (rendered by Chromium in each page's top/bottom margin, so they must carry their own styles).
/// </summary>
internal static class DocumentCss
{
    private const string Fonts =
        ":root{--v-sans:'Schibsted Grotesk','Segoe UI',Arial,sans-serif;--v-serif:'Source Serif 4',Georgia,'Times New Roman',serif;--v-mono:'IBM Plex Mono',Consolas,'Courier New',monospace}\n";

    /// <summary>Styles for elements, valid in every output.</summary>
    private const string Elements = """
        .v-area{position:relative;overflow:hidden}
        .v-abs{position:absolute;overflow:hidden}
        .v-abs>*{width:100%;height:100%}
        .v-text,.v-field{white-space:pre-line;overflow-wrap:anywhere}
        .v-label{color:#66727E;margin-right:2mm}
        .v-divider{height:0}
        .v-block>.v-image{display:block;max-width:100%}
        .v-table{width:100%;border-collapse:collapse}
        .v-table thead{display:table-header-group}
        .v-table th{font-weight:600;border-bottom:1pt solid #1A232C;padding:1.2mm 1mm}
        .v-table td{border-bottom:0.5pt solid #E3E7EB;padding:1.2mm 1mm;vertical-align:top}
        .v-table tr{break-inside:avoid}
        .v-empty{color:#66727E;font-style:italic}
        .v-signature{display:flex;flex-direction:column;justify-content:flex-end}
        .v-sigline{border-top:0.75pt solid #1A232C}
        .v-siglabel{font-size:8pt;color:#66727E;padding-top:1mm}
        .v-missing{border:1px dashed #A82E6B;color:#A82E6B;background:#F6E5EE;border-radius:2px;padding:0 2px;font-family:'IBM Plex Mono',Consolas,monospace;font-size:0.9em;white-space:nowrap}

        """;

    private const string Base =
        "*{box-sizing:border-box}\nhtml,body{margin:0;padding:0}\n" +
        "body{font-family:var(--v-sans);font-size:10pt;line-height:1.35;color:#1A232C;-webkit-print-color-adjust:exact;print-color-adjust:exact}\n";

    /// <summary>Screen preview: header at the top, footer at the bottom of a sheet at least one page tall.</summary>
    public static string Preview(TemplateLayout layout, PageSetup page) => Fonts + Base + Elements + $$"""
        body{background:#E3E7EB;padding:8mm 0}
        .v-page{width:{{Mm(page.WidthMm)}};min-height:{{Mm(page.HeightMm)}};margin:0 auto;background:#fff;display:flex;flex-direction:column;box-shadow:0 1px 2px rgba(19,32,43,.1),0 18px 40px -16px rgba(19,32,43,.3)}
        .v-header{padding:{{Mm(page.MarginTopMm)}} {{Mm(page.MarginRightMm)}} 0 {{Mm(page.MarginLeftMm)}}}
        .v-header>.v-area{height:{{Mm(layout.Header.HeightMm)}}}
        .v-body{padding:0 {{Mm(page.MarginRightMm)}};padding-left:{{Mm(page.MarginLeftMm)}};flex:1}
        .v-footer{padding:0 {{Mm(page.MarginRightMm)}} {{Mm(page.MarginBottomMm)}} {{Mm(page.MarginLeftMm)}}}
        .v-footer>.v-area{height:{{Mm(layout.Footer.HeightMm)}}}

        """;

    /// <summary>Print body: page size only; Chromium applies the top/bottom margins that hold the header/footer.</summary>
    public static string PrintBody(PageSetup page) => Fonts + Base + Elements + $$"""
        @page{size:{{Mm(page.WidthMm)}} {{Mm(page.HeightMm)}}}
        .v-body{padding:0 {{Mm(page.MarginRightMm)}};padding-left:{{Mm(page.MarginLeftMm)}}}

        """;

    /// <summary>Header/footer template styles. Templates are isolated from the body document, so everything is repeated here.</summary>
    public static string Template() => Fonts + Elements + "*{box-sizing:border-box}\n" +
        // Chromium wraps templates in #header/#footer with default padding, which shifts content ~5 mm; remove it.
        "#header,#footer{padding:0 !important}\n" +
        ".v-tpl{width:100%;font-family:'Schibsted Grotesk','Segoe UI',Arial,sans-serif;font-size:10pt;line-height:1.35;color:#1A232C;-webkit-print-color-adjust:exact;print-color-adjust:exact}\n";
}
