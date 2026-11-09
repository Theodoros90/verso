using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Verso.Rendering.Binding;
using Verso.Rendering.Layout;

namespace Verso.Rendering.Html;

/// <summary>
/// Renders a parsed layout plus data to HTML: a screen preview and the parts for paginated printing.
/// Safety: every author or data string is HTML-encoded; CSS values come only from validated numbers, enums and
/// <c>#RRGGBB</c> colors; images are validated <c>data:</c> URIs; documents carry a CSP that blocks scripts and all network access.
/// </summary>
public static class HtmlRenderer
{
    // Encodes &lt; &gt; &amp; " ' and control characters, but leaves letters such as € or Greek readable.
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

    private const string Csp =
        "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; font-src data:; style-src 'unsafe-inline'\">\n";

    public static RenderResult Render(TemplateLayout layout, PageSetup page, JsonElement data, RenderMode mode, string title)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(page);

        // Each region is rendered once; the fragments feed both outputs, so issues are collected once.
        var ctx = new RenderContext(mode);
        var header = string.Concat(layout.Header.Elements.Select(p => RenderPositioned(p, data, ctx) + "\n"));
        var body = string.Concat(layout.Body.Blocks.Select(b => RenderBlock(b, data, ctx) + "\n"));
        var footer = string.Concat(layout.Footer.Elements.Select(p => RenderPositioned(p, data, ctx) + "\n"));

        var preview = Document(title, DocumentCss.Preview(layout, page),
            "<div class=\"v-page\">\n" +
            $"<header class=\"v-header\"><div class=\"v-area\">\n{header}</div></header>\n" +
            $"<main class=\"v-body\">\n{body}</main>\n" +
            $"<footer class=\"v-footer\"><div class=\"v-area\">\n{footer}</div></footer>\n" +
            "</div>\n");

        var topBand = page.MarginTopMm + layout.Header.HeightMm;
        var bottomBand = page.MarginBottomMm + layout.Footer.HeightMm;
        var print = new PrintDocument(
            BodyHtml: Document(title, DocumentCss.PrintBody(page), $"<main class=\"v-body\">\n{body}</main>\n"),
            HeaderTemplate: Template(topBand, $"{Mm(page.MarginTopMm)} {Mm(page.MarginRightMm)} 0 {Mm(page.MarginLeftMm)}", layout.Header.HeightMm, header),
            FooterTemplate: Template(bottomBand, $"0 {Mm(page.MarginRightMm)} {Mm(page.MarginBottomMm)} {Mm(page.MarginLeftMm)}", layout.Footer.HeightMm, footer),
            PaperWidthMm: page.WidthMm,
            PaperHeightMm: page.HeightMm,
            MarginTopMm: topBand,
            MarginBottomMm: bottomBand);

        return new RenderResult(preview, print, ctx.Issues);
    }

    private static string Document(string title, string css, string bodyContent) =>
        "<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n" + Csp +
        $"<title>{Encode(title)}</title>\n<style>\n{css}</style>\n</head>\n<body>\n{bodyContent}</body>\n</html>\n";

    /// <summary>A header/footer template for Chromium: one band of the given height with its own styles.</summary>
    private static string Template(decimal bandMm, string padding, decimal areaMm, string content) =>
        $"<div class=\"v-tpl\" style=\"height:{Mm(bandMm)};padding:{padding}\"><style>\n{DocumentCss.Template()}</style>" +
        $"<div class=\"v-area\" style=\"height:{Mm(areaMm)}\">\n{content}</div></div>";

    internal static string RenderPositioned(PositionedElement p, JsonElement data, RenderContext ctx) =>
        $"<div class=\"v-abs\" style=\"left:{Mm(p.XMm)};top:{Mm(p.YMm)};width:{Mm(p.WidthMm)};height:{Mm(p.HeightMm)}\">{RenderElement(p.Element, data, ctx)}</div>";

    internal static string RenderBlock(FlowBlock block, JsonElement data, RenderContext ctx) =>
        block.SpaceBeforeMm > 0
            ? $"<div class=\"v-block\" style=\"margin-top:{Mm(block.SpaceBeforeMm)}\">{RenderElement(block.Element, data, ctx)}</div>"
            : $"<div class=\"v-block\">{RenderElement(block.Element, data, ctx)}</div>";

    internal static string RenderElement(LayoutElement element, JsonElement data, RenderContext ctx) => element switch
    {
        TextElement t => $"<div class=\"v-text\" data-id=\"{Encode(t.Id)}\" style=\"{StyleCss(t.Style)}\">{RenderText(t, data, ctx)}</div>",
        FieldElement f => RenderField(f, data, ctx),
        TableElement t => RenderTable(t, data, ctx),
        DividerElement d => $"<div class=\"v-divider\" data-id=\"{Encode(d.Id)}\" style=\"border-top:{Pt(d.ThicknessPt)} solid {d.Color}\"></div>",
        BoxElement b => $"<div class=\"v-box\" data-id=\"{Encode(b.Id)}\" style=\"border:0.75pt solid {b.BorderColor};background:{b.Fill ?? "transparent"};border-radius:{Mm(b.RadiusMm)}\"></div>",
        ImageElement i => $"<img class=\"v-image\" data-id=\"{Encode(i.Id)}\" alt=\"{Encode(i.Alt)}\" style=\"object-fit:{(i.Fit == ImageFit.Cover ? "cover" : "contain")}\" src=\"{i.DataUri}\">",
        SignatureElement s => $"<div class=\"v-signature\" data-id=\"{Encode(s.Id)}\"><div class=\"v-sigline\"></div><div class=\"v-siglabel\">{Encode(s.Label)}</div></div>",
        _ => throw new ArgumentOutOfRangeException(nameof(element), element.GetType().Name, "Unknown element type."),
    };

    private static string RenderText(TextElement t, JsonElement data, RenderContext ctx)
    {
        var sb = new StringBuilder();
        foreach (var part in TextTemplate.Parse(t.Text))
        {
            switch (part)
            {
                case LiteralPart literal:
                    sb.Append(Encode(literal.Text));
                    break;
                case TokenPart token:
                    sb.Append(ValueHtml(data, token.Path, token.Filter, t.Id, token.Path, ctx));
                    break;
                case MalformedTokenPart bad:
                    ctx.Report(IssueSeverity.Error, "token.malformed", $"'{bad.Source}' isn't a valid binding. Use {{{{path}}}} or {{{{path|filter}}}}.", t.Id, null);
                    sb.Append(Encode(bad.Source));
                    break;
            }
        }

        return sb.ToString();
    }

    private static string RenderField(FieldElement f, JsonElement data, RenderContext ctx)
    {
        var label = string.IsNullOrEmpty(f.Label) ? "" : $"<span class=\"v-label\">{Encode(f.Label)}</span>";
        return $"<div class=\"v-field\" data-id=\"{Encode(f.Id)}\" style=\"{StyleCss(f.Style)}\">{label}<span class=\"v-value\">{ValueHtml(data, f.Binding, f.Filter, f.Id, f.Binding, ctx)}</span></div>";
    }

    private static string RenderTable(TableElement t, JsonElement data, RenderContext ctx)
    {
        var sb = new StringBuilder();
        sb.Append("<table class=\"v-table\" data-id=\"").Append(Encode(t.Id)).Append("\" style=\"").Append(StyleCss(t.Style)).Append("\"><thead><tr>");
        foreach (var c in t.Columns)
        {
            sb.Append("<th style=\"").Append(AlignCss(c)).Append("\">").Append(Encode(c.Header)).Append("</th>");
        }

        sb.Append("</tr></thead><tbody>");

        if (!DataPath.TryResolve(data, t.Binding, out var rows))
        {
            ctx.Report(IssueSeverity.Error, "binding.missing", $"No value for '{t.Binding}'.", t.Id, t.Binding);
            sb.Append(PlaceholderRow(t, $"{{{{{t.Binding}}}}}", ctx));
        }
        else if (rows.ValueKind != JsonValueKind.Array)
        {
            ctx.Report(IssueSeverity.Error, "table.not_array", $"'{t.Binding}' must be a list for a repeating table.", t.Id, t.Binding);
            sb.Append(PlaceholderRow(t, $"{{{{{t.Binding}}}}}", ctx));
        }
        else if (rows.GetArrayLength() == 0)
        {
            if (!string.IsNullOrEmpty(t.EmptyText))
            {
                sb.Append("<tr><td class=\"v-empty\" colspan=\"").Append(t.Columns.Count.ToString(CultureInfo.InvariantCulture)).Append("\">")
                  .Append(Encode(t.EmptyText)).Append("</td></tr>");
            }
        }
        else
        {
            var i = 0;
            foreach (var row in rows.EnumerateArray())
            {
                sb.Append("<tr>");
                foreach (var c in t.Columns)
                {
                    sb.Append("<td style=\"").Append(AlignCss(c)).Append("\">")
                      .Append(ValueHtml(row, c.Binding, c.Filter, t.Id, $"{t.Binding}[{i}].{c.Binding}", ctx))
                      .Append("</td>");
                }

                sb.Append("</tr>");
                i++;
            }
        }

        sb.Append("</tbody></table>");
        return sb.ToString();
    }

    private static string PlaceholderRow(TableElement t, string token, RenderContext ctx) =>
        ctx.Mode == RenderMode.Preview
            ? $"<tr><td colspan=\"{t.Columns.Count.ToString(CultureInfo.InvariantCulture)}\">{Placeholder(token)}</td></tr>"
            : "";

    /// <summary>Resolves, formats and encodes one binding; reports issues and returns a placeholder (preview) or nothing (final) when unresolved.</summary>
    private static string ValueHtml(JsonElement scope, string path, string? filter, string elementId, string issuePath, RenderContext ctx)
    {
        var token = filter is null ? $"{{{{{path}}}}}" : $"{{{{{path}|{filter}}}}}";
        if (!DataPath.TryResolve(scope, path, out var value))
        {
            ctx.Report(IssueSeverity.Error, "binding.missing", $"No value for '{issuePath}'.", elementId, issuePath);
            return ctx.Mode == RenderMode.Preview ? Placeholder(token) : "";
        }

        if (!Filters.TryGetScalarText(value, out var text))
        {
            ctx.Report(IssueSeverity.Error, "binding.not_scalar", $"'{issuePath}' is a {value.ValueKind.ToString().ToLowerInvariant()}, not a single value.", elementId, issuePath);
            return ctx.Mode == RenderMode.Preview ? Placeholder(token) : "";
        }

        if (filter is not null)
        {
            if (Filters.TryApply(filter, value, out var formatted))
            {
                return Encode(formatted);
            }

            ctx.Report(IssueSeverity.Warning, "filter.invalid_input", $"Filter '{filter}' can't format '{text}' at '{issuePath}'; showing it as is.", elementId, issuePath);
        }

        return Encode(text);
    }

    private static string Placeholder(string token) => $"<span class=\"v-missing\">{Encode(token)}</span>";

    private static string StyleCss(TextStyle s)
    {
        var css = new StringBuilder();
        css.Append("font-size:").Append(Pt(s.FontSizePt));
        if (s.Bold)
        {
            css.Append(";font-weight:600");
        }

        if (s.Italic)
        {
            css.Append(";font-style:italic");
        }

        if (s.Align != TextAlign.Left)
        {
            css.Append(";text-align:").Append(s.Align == TextAlign.Right ? "right" : "center");
        }

        css.Append(";color:").Append(s.Color);
        if (s.Font != FontFamily.Sans)
        {
            css.Append(";font-family:var(--v-").Append(s.Font == FontFamily.Serif ? "serif" : "mono").Append(')');
        }

        return css.ToString();
    }

    private static string AlignCss(TableColumn c)
    {
        var align = c.Align switch { TextAlign.Right => "right", TextAlign.Center => "center", _ => "left" };
        return c.WidthPct is { } w
            ? $"text-align:{align};width:{w.ToString("0.##", CultureInfo.InvariantCulture)}%"
            : $"text-align:{align}";
    }

    internal static string Mm(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "mm";

    private static string Pt(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "pt";

    private static string Encode(string value) => Encoder.Encode(value);
}

/// <summary>Mode plus the issues collected during one render (each distinct issue once).</summary>
internal sealed class RenderContext(RenderMode mode)
{
    private readonly HashSet<(string, string, string?)> _seen = [];

    public RenderMode Mode { get; } = mode;
    public List<RenderIssue> Issues { get; } = [];

    public void Report(IssueSeverity severity, string code, string message, string elementId, string? path)
    {
        if (_seen.Add((code, elementId, path)))
        {
            Issues.Add(new RenderIssue(severity, code, message, elementId, path));
        }
    }
}
