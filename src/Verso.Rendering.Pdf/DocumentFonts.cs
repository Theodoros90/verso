using System.Text;

namespace Verso.Rendering.Pdf;

/// <summary>
/// The document fonts as <c>@font-face</c> rules with embedded <c>data:</c> URIs, so printing needs no network and
/// the PDF looks the same on every server. Family names match the renderer's CSS (DocumentCss), which falls back to
/// system fonts when these rules are absent (screen preview). Latin subsets (includes €). Licenses: Fonts/LICENSE-*.txt.
/// </summary>
/// <remarks>
/// Static weights, not variable fonts: Chromium embeds variable fonts as Type 3 glyph outlines (unhinted, larger,
/// unsuitable for PDF/A later), static fonts as proper subset TrueType. The renderer uses 400 (text) and 600
/// (bold and table headings), normal and italic.
/// <para>
/// Chromium draws the header/footer templates as separate documents just before printing. A face that the body
/// didn't load yet is still loading then, so the body preloads every face (<see cref="Preloader"/>) and the rules
/// use <c>font-display:swap</c>: the worst case is a fallback font, never invisible text.
/// </para>
/// </remarks>
internal static class DocumentFonts
{
    private static readonly (string Family, string Prefix)[] Families =
        [("Schibsted Grotesk", "schibsted-grotesk"), ("Source Serif 4", "source-serif-4")];

    private static readonly Lazy<string> LazyCss = new(Build);

    public static string Css => LazyCss.Value;

    /// <summary>Invisible text in every face, so the body document loads them all before printing.</summary>
    private static readonly string Preloader = BuildPreloader();

    /// <summary>Adds the font rules (before &lt;/head&gt;) and the preloader (after &lt;body&gt;) to a full HTML document.</summary>
    public static string IntoDocument(string html)
    {
        var style = $"<style>\n{Css}</style>\n";
        var head = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        html = head < 0 ? style + html : html.Insert(head, style);

        var body = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        var bodyEnd = body < 0 ? -1 : html.IndexOf('>', body);
        return bodyEnd < 0 ? html + Preloader : html.Insert(bodyEnd + 1, Preloader);
    }

    /// <summary>Adds the font rules to a header/footer template fragment.</summary>
    public static string IntoTemplate(string template) => $"<style>\n{Css}</style>" + template;

    private static string Build()
    {
        var sb = new StringBuilder();
        foreach (var (family, prefix) in Families)
        {
            foreach (var weight in new[] { "400", "600" })
            {
                Face(sb, family, weight, "normal", $"{prefix}-latin-{weight}-normal.woff2");
                Face(sb, family, weight, "italic", $"{prefix}-latin-{weight}-italic.woff2");
            }
        }

        Face(sb, "IBM Plex Mono", "400", "normal", "ibm-plex-mono-latin-400-normal.woff2");
        Face(sb, "IBM Plex Mono", "600", "normal", "ibm-plex-mono-latin-600-normal.woff2");
        return sb.ToString();
    }

    private static string BuildPreloader()
    {
        var sb = new StringBuilder("<div aria-hidden=\"true\" style=\"position:absolute;left:-10000px;top:0;height:0;overflow:hidden\">");
        foreach (var (family, _) in Families)
        {
            foreach (var weight in new[] { "400", "600" })
            {
                foreach (var style in new[] { "normal", "italic" })
                {
                    sb.Append("<span style=\"font-family:'").Append(family).Append("';font-weight:").Append(weight)
                      .Append(";font-style:").Append(style).Append("\">Aa€</span>");
                }
            }
        }

        sb.Append("<span style=\"font-family:'IBM Plex Mono';font-weight:400\">Aa</span><span style=\"font-family:'IBM Plex Mono';font-weight:600\">Aa</span></div>\n");
        return sb.ToString();
    }

    private static void Face(StringBuilder sb, string family, string weight, string style, string file)
    {
        using var stream = typeof(DocumentFonts).Assembly.GetManifestResourceStream($"Verso.Fonts.{file}")
                           ?? throw new InvalidOperationException($"Embedded font '{file}' is missing.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        sb.Append("@font-face{font-family:'").Append(family).Append("';font-weight:").Append(weight)
          .Append(";font-style:").Append(style).Append(";font-display:swap;src:url(data:font/woff2;base64,")
          .Append(Convert.ToBase64String(ms.ToArray())).Append(") format('woff2')}\n");
    }
}
