using System.Text;
using System.Text.Json;
using Verso.Rendering.Binding;
using Verso.Rendering.Layout;

namespace Verso.Rendering.Docx;

/// <summary>
/// Applies data to a layout and returns plain text blocks for the DOCX writer. Same rules and issue codes as
/// <see cref="Html.HtmlRenderer"/> in Final mode: an unresolved value is empty and reported, so the engine can reject an incomplete document.
/// </summary>
public static class DocumentResolver
{
    public static ResolvedDocument Resolve(TemplateLayout layout, PageSetup page, JsonElement data, string title)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(page);

        var ctx = new Html.RenderContext(RenderMode.Final);
        var header = Positioned(layout.Header, data, ctx);
        var body = layout.Body.Blocks.Select(b => Element(b.Element, data, ctx, b.SpaceBeforeMm, null, null)).Where(b => b is not null).Select(b => b!).ToList();
        var footer = Positioned(layout.Footer, data, ctx);
        // Word can't show a band on every page except the last (only "different first page" exists), so it stays on every page.
        foreach (var (band, show, content) in new[] { ("header", layout.Header.ShowOn, header), ("footer", layout.Footer.ShowOn, footer) })
        {
            if (show == PageVisibility.NotLast && content.Count > 0)
            {
                ctx.Report(IssueSeverity.Warning, "band.page_rule_unsupported", $"A Word document can't leave the {band} off the last page only; it is shown on every page.", band, null);
            }
        }

        return new ResolvedDocument(title, page, header, body, footer, ctx.Issues, layout.Header.ShowOn, layout.Footer.ShowOn);
    }

    private static List<ResolvedBlock> Positioned(FixedRegion region, JsonElement data, Html.RenderContext ctx) =>
        region.Elements
            .OrderBy(p => p.YMm).ThenBy(p => p.XMm)
            .Select(p => Element(p.Element, data, ctx, 0, p.WidthMm, p.HeightMm, inBand: true))
            .Where(b => b is not null).Select(b => b!)
            .ToList();

    private static ResolvedBlock? Element(LayoutElement element, JsonElement data, Html.RenderContext ctx, decimal space, decimal? widthMm, decimal? heightMm, bool inBand = false) => element switch
    {
        TextElement t => new ParagraphBlock(TextRuns(t, data, ctx, inBand), t.Style, space),
        FieldElement f => new ParagraphBlock(FieldRuns(f, data, ctx), f.Style, space),
        TableElement t => Table(t, data, ctx, space),
        DividerElement d => new DividerBlock(d.ThicknessPt, d.Color, space),
        BoxElement => null, // decoration only: a bordered rectangle has no content in Word's flow
        ImageElement i => Image(i, widthMm, heightMm, space),
        SignatureElement s => new SignatureBlock(s.Label, space),
        _ => throw new ArgumentOutOfRangeException(nameof(element), element.GetType().Name, "Unknown element type."),
    };

    private static List<ResolvedRun> FieldRuns(FieldElement f, JsonElement data, Html.RenderContext ctx)
    {
        var runs = new List<ResolvedRun>();
        if (!string.IsNullOrEmpty(f.Label))
        {
            runs.Add(new ResolvedRun(f.Label + " ", false));
        }

        runs.Add(new ResolvedRun(Value(data, f.Binding, f.Filter, f.Id, f.Binding, ctx), false));
        return runs;
    }

    /// <summary>
    /// The text of a text element as runs. In a header or footer, <c>{{page}}</c> and <c>{{pages}}</c> become Word page fields
    /// of their own; everywhere else the whole text is one run of plain text.
    /// </summary>
    private static List<ResolvedRun> TextRuns(TextElement t, JsonElement data, Html.RenderContext ctx, bool inBand)
    {
        var runs = new List<ResolvedRun>();
        var sb = new StringBuilder();
        void Flush()
        {
            if (sb.Length > 0)
            {
                runs.Add(new ResolvedRun(sb.ToString(), false));
                sb.Clear();
            }
        }

        foreach (var part in TextTemplate.Parse(t.Text))
        {
            switch (part)
            {
                case LiteralPart literal:
                    sb.Append(literal.Text);
                    break;
                case TokenPart token when inBand && PageTokens.Is(token):
                    Flush();
                    runs.Add(new ResolvedRun("", false, token.Path == PageTokens.Page ? PageField.Page : PageField.Pages));
                    break;
                case TokenPart token:
                    sb.Append(Value(data, token.Path, token.Filter, t.Id, token.Path, ctx));
                    break;
                case MalformedTokenPart bad:
                    ctx.Report(IssueSeverity.Error, "token.malformed", $"'{bad.Source}' isn't a valid binding. Use {{{{path}}}} or {{{{path|filter}}}}.", t.Id, null);
                    sb.Append(bad.Source);
                    break;
            }
        }

        Flush();
        if (runs.Count == 0)
        {
            runs.Add(new ResolvedRun("", false));
        }

        return runs;
    }

    private static TableBlock Table(TableElement t, JsonElement data, Html.RenderContext ctx, decimal space)
    {
        var columns = t.Columns.Select(c => new ResolvedColumn(c.Header, c.Align, c.WidthPct)).ToList();
        var rows = new List<IReadOnlyList<string>>();
        if (!DataPath.TryResolve(data, t.Binding, out var list))
        {
            ctx.Report(IssueSeverity.Error, "binding.missing", $"No value for '{t.Binding}'.", t.Id, t.Binding);
        }
        else if (list.ValueKind != JsonValueKind.Array)
        {
            ctx.Report(IssueSeverity.Error, "table.not_array", $"'{t.Binding}' must be a list for a repeating table.", t.Id, t.Binding);
        }
        else
        {
            var i = 0;
            foreach (var row in list.EnumerateArray())
            {
                rows.Add(t.Columns.Select(c => Value(row, c.Binding, c.Filter, t.Id, $"{t.Binding}[{i}].{c.Binding}", ctx)).ToList());
                i++;
            }
        }

        return new TableBlock(columns, rows, t.EmptyText, t.Style, space);
    }

    private static ImageBlock Image(ImageElement i, decimal? widthMm, decimal? heightMm, decimal space)
    {
        // The parser guarantees "data:image/png|jpeg;base64,<valid base64>".
        var comma = i.DataUri.IndexOf(',', StringComparison.Ordinal);
        var isPng = i.DataUri.StartsWith("data:image/png", StringComparison.Ordinal);
        return new ImageBlock(Convert.FromBase64String(i.DataUri[(comma + 1)..]), isPng, i.Alt, widthMm, heightMm, TextAlign.Left, space);
    }

    private static string Value(JsonElement scope, string path, string? filter, string elementId, string issuePath, Html.RenderContext ctx)
    {
        if (!DataPath.TryResolve(scope, path, out var value))
        {
            ctx.Report(IssueSeverity.Error, "binding.missing", $"No value for '{issuePath}'.", elementId, issuePath);
            return "";
        }

        if (!Filters.TryGetScalarText(value, out var text))
        {
            ctx.Report(IssueSeverity.Error, "binding.not_scalar", $"'{issuePath}' is a {value.ValueKind.ToString().ToLowerInvariant()}, not a single value.", elementId, issuePath);
            return "";
        }

        if (filter is not null)
        {
            if (Filters.TryApply(filter, value, out var formatted))
            {
                return formatted;
            }

            ctx.Report(IssueSeverity.Warning, "filter.invalid_input", $"Filter '{filter}' can't format '{text}' at '{issuePath}'; showing it as is.", elementId, issuePath);
        }

        return text;
    }
}
