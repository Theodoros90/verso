using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Verso.Rendering.Layout;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Verso.Rendering.Docx;

/// <summary>
/// Writes a <see cref="ResolvedDocument"/> as a Word document: page setup, repeating header/footer, paragraphs, tables
/// (headings repeat on each page), dividers, images and signature lines. Fonts are Word's standard ones (Arial, Times New Roman,
/// Courier New) because the document's own fonts can't be assumed to be installed where it is opened.
/// </summary>
public sealed class DocxWriter : IDocxWriter
{
    private const decimal TwipsPerMm = 56.6929m;
    private const long EmuPerMm = 36000;
    private const decimal SignatureLineHeightMm = 12;

    public byte[] Write(ResolvedDocument document, DocxProtection protection)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var stream = new MemoryStream();
        using (var package = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            package.PackageProperties.Title = document.Title;
            package.PackageProperties.Creator = "Verso";

            var main = package.AddMainDocumentPart();
            var page = document.Page;
            var contentTwips = Tw(page.WidthMm - page.MarginLeftMm - page.MarginRightMm);
            var images = new ImageIds();

            var body = new W.Body();
            foreach (var block in document.Body)
            {
                body.Append(Render(block, contentTwips, (bytes, png) => AddImage(main, main.AddImagePart(png ? ImagePartType.Png : ImagePartType.Jpeg), bytes), images));
            }

            body.Append(new W.Paragraph()); // Word needs a paragraph after a final table.

            var section = new W.SectionProperties();
            if (document.Header.Count > 0)
            {
                var part = main.AddNewPart<HeaderPart>();
                part.Header = new W.Header(document.Header.SelectMany(b => Render(b, contentTwips, (bytes, png) => AddImage(part, part.AddImagePart(png ? ImagePartType.Png : ImagePartType.Jpeg), bytes), images)));
                section.Append(new W.HeaderReference { Type = W.HeaderFooterValues.Default, Id = main.GetIdOfPart(part) });
            }

            if (document.Footer.Count > 0)
            {
                var part = main.AddNewPart<FooterPart>();
                part.Footer = new W.Footer(document.Footer.SelectMany(b => Render(b, contentTwips, (bytes, png) => AddImage(part, part.AddImagePart(png ? ImagePartType.Png : ImagePartType.Jpeg), bytes), images)));
                section.Append(new W.FooterReference { Type = W.HeaderFooterValues.Default, Id = main.GetIdOfPart(part) });
            }

            section.Append(new W.PageSize
            {
                Width = (uint)Tw(page.WidthMm),
                Height = (uint)Tw(page.HeightMm),
                Orient = page.WidthMm > page.HeightMm ? W.PageOrientationValues.Landscape : W.PageOrientationValues.Portrait,
            });
            section.Append(new W.PageMargin
            {
                Top = Tw(page.MarginTopMm),
                Right = (uint)Tw(page.MarginRightMm),
                Bottom = Tw(page.MarginBottomMm),
                Left = (uint)Tw(page.MarginLeftMm),
                Header = (uint)Tw(page.MarginTopMm / 2),
                Footer = (uint)Tw(page.MarginBottomMm / 2),
                Gutter = 0,
            });
            body.Append(section);
            main.Document = new W.Document(body);

            var settings = main.AddNewPart<DocumentSettingsPart>();
            settings.Settings = new W.Settings();
            if (protection == DocxProtection.TrackedChangesOnly)
            {
                settings.Settings.Append(new W.TrackRevisions());
            }

            if (protection != DocxProtection.AllowEdits)
            {
                // No password: this tells Word the author's intent; it isn't a security control (docs/layout-model.md).
                settings.Settings.Append(new W.DocumentProtection
                {
                    Edit = protection == DocxProtection.ReadOnly ? W.DocumentProtectionValues.ReadOnly : W.DocumentProtectionValues.TrackedChanges,
                    Enforcement = true,
                });
            }
        }

        return stream.ToArray();
    }

    /// <summary>Unique drawing ids across the package (Word wants them distinct).</summary>
    private sealed class ImageIds
    {
        private uint _next = 1;
        public uint Next() => _next++;
    }

    /// <summary>Stores the image in the part and returns the owner's relationship id for it.</summary>
    private static string AddImage(OpenXmlPartContainer owner, ImagePart image, byte[] bytes)
    {
        using var data = new MemoryStream(bytes);
        image.FeedData(data);
        return owner.GetIdOfPart(image);
    }

    private static IEnumerable<OpenXmlElement> Render(ResolvedBlock block, int contentTwips, Func<byte[], bool, string> addImage, ImageIds ids) => block switch
    {
        ParagraphBlock p => [Paragraph(p)],
        TableBlock t => [Table(t, contentTwips), new W.Paragraph(new W.ParagraphProperties(new W.SpacingBetweenLines { Before = "0", After = "0" }))],
        DividerBlock d => [Divider(d)],
        ImageBlock i => [ImageParagraph(i, contentTwips, addImage, ids)],
        SignatureBlock s => [Signature(s, contentTwips)],
        _ => throw new ArgumentOutOfRangeException(nameof(block), block.GetType().Name, "Unknown block type."),
    };

    private static W.Paragraph Paragraph(ParagraphBlock p)
    {
        var paragraph = new W.Paragraph(new W.ParagraphProperties(Spacing(p.SpaceBeforeMm), Align(p.Style.Align)));
        foreach (var run in p.Runs)
        {
            paragraph.Append(TextRun(run.Text, p.Style, run.Bold || p.Style.Bold));
        }

        return paragraph;
    }

    private static W.SpacingBetweenLines Spacing(decimal beforeMm) => new() { Before = Tw(beforeMm).ToString(CultureInfo.InvariantCulture), After = "0" };

    private static W.Justification Align(TextAlign align) => new()
    {
        Val = align switch { TextAlign.Center => W.JustificationValues.Center, TextAlign.Right => W.JustificationValues.Right, _ => W.JustificationValues.Left },
    };

    private static W.Run TextRun(string text, TextStyle style, bool bold)
    {
        var props = new W.RunProperties(new W.RunFonts { Ascii = Font(style.Font), HighAnsi = Font(style.Font), ComplexScript = Font(style.Font), EastAsia = Font(style.Font) });
        if (bold)
        {
            props.Append(new W.Bold());
        }

        if (style.Italic)
        {
            props.Append(new W.Italic());
        }

        props.Append(new W.Color { Val = style.Color.TrimStart('#') });
        props.Append(new W.FontSize { Val = HalfPoints(style.FontSizePt) });

        var run = new W.Run(props);
        var lines = Clean(text).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                run.Append(new W.Break());
            }

            run.Append(new W.Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
        }

        return run;
    }

    private static string Font(FontFamily font) => font switch { FontFamily.Serif => "Times New Roman", FontFamily.Mono => "Courier New", _ => "Arial" };

    private static string HalfPoints(decimal pt) => ((int)Math.Round(pt * 2, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);

    /// <summary>Drops characters XML can't hold (control characters other than tab and line breaks); a lone <c>\r</c> becomes a line break.</summary>
    private static string Clean(string text) =>
        string.Concat(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Where(c => c is '\n' or '\t' or >= ' ' && c != '￾' && c != '￿'));

    private static W.Table Table(TableBlock t, int contentTwips)
    {
        var widths = ColumnWidths(t.Columns, contentTwips);
        var table = new W.Table(new W.TableProperties(
            new W.TableWidth { Width = contentTwips.ToString(CultureInfo.InvariantCulture), Type = W.TableWidthUnitValues.Dxa },
            new W.TableBorders(
                new W.BottomBorder { Val = W.BorderValues.Single, Size = 4, Color = "D5DBE1" },
                new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4, Color = "D5DBE1" }),
            new W.TableLayout { Type = W.TableLayoutValues.Fixed }));
        table.Append(new W.TableGrid(widths.Select(w => new W.GridColumn { Width = w.ToString(CultureInfo.InvariantCulture) })));

        var headerRow = new W.TableRow(new W.TableRowProperties(new W.CantSplit(), new W.TableHeader()));
        for (var c = 0; c < t.Columns.Count; c++)
        {
            headerRow.Append(Cell(t.Columns[c].Header, t.Columns[c].Align, t.Style, widths[c], bold: true));
        }

        table.Append(headerRow);

        if (t.Rows.Count == 0)
        {
            if (!string.IsNullOrEmpty(t.EmptyText))
            {
                var row = new W.TableRow(new W.TableRowProperties(new W.CantSplit()));
                var cell = Cell(t.EmptyText, TextAlign.Left, t.Style with { Italic = true }, contentTwips, bold: false);
                cell.TableCellProperties!.Append(new W.GridSpan { Val = t.Columns.Count });
                row.Append(cell);
                table.Append(row);
            }

            return table;
        }

        foreach (var values in t.Rows)
        {
            var row = new W.TableRow(new W.TableRowProperties(new W.CantSplit()));
            for (var c = 0; c < t.Columns.Count; c++)
            {
                row.Append(Cell(values[c], t.Columns[c].Align, t.Style, widths[c], bold: false));
            }

            table.Append(row);
        }

        return table;
    }

    private static W.TableCell Cell(string text, TextAlign align, TextStyle style, int widthTwips, bool bold) =>
        new(new W.TableCellProperties(new W.TableCellWidth { Width = widthTwips.ToString(CultureInfo.InvariantCulture), Type = W.TableWidthUnitValues.Dxa }),
            new W.Paragraph(new W.ParagraphProperties(Spacing(0), Align(align)), TextRun(text, style, bold || style.Bold)));

    /// <summary>Columns with a width get that share of the table; the rest split what is left equally.</summary>
    internal static int[] ColumnWidths(IReadOnlyList<ResolvedColumn> columns, int totalTwips)
    {
        var fixedTwips = columns.Where(c => c.WidthPct is not null).Sum(c => (int)(totalTwips * c.WidthPct!.Value / 100m));
        var flexible = columns.Count(c => c.WidthPct is null);
        var each = flexible == 0 ? 0 : Math.Max(0, totalTwips - fixedTwips) / flexible;
        return [.. columns.Select(c => c.WidthPct is { } pct ? (int)(totalTwips * pct / 100m) : each)];
    }

    private static W.Paragraph Divider(DividerBlock d) =>
        new(new W.ParagraphProperties(
            new W.ParagraphBorders(new W.BottomBorder { Val = W.BorderValues.Single, Size = (uint)Math.Max(2, (int)Math.Round(d.ThicknessPt * 8)), Color = d.Color.TrimStart('#'), Space = 1 }),
            Spacing(d.SpaceBeforeMm)),
            new W.Run(new W.RunProperties(new W.FontSize { Val = "2" })));

    private static W.Paragraph Signature(SignatureBlock s, int contentTwips)
    {
        // The line is the paragraph's top border, so the space before it is outside the line, where the signature goes.
        var style = TextStyle.Default with { FontSizePt = 8 };
        return new W.Paragraph(
            new W.ParagraphProperties(
                new W.ParagraphBorders(new W.TopBorder { Val = W.BorderValues.Single, Size = 6, Color = "1A232C", Space = 2 }),
                Spacing(s.SpaceBeforeMm + SignatureLineHeightMm),
                new W.Indentation { Right = (contentTwips / 2).ToString(CultureInfo.InvariantCulture) }),
            TextRun(s.Label, style, false));
    }

    private static W.Paragraph ImageParagraph(ImageBlock image, int contentTwips, Func<byte[], bool, string> addImage, ImageIds ids)
    {
        var (widthMm, heightMm) = Size(image, contentTwips);
        var relationship = addImage(image.Bytes, image.IsPng);
        var id = ids.Next();
        var drawing = new W.Drawing(new DW.Inline(
            new DW.Extent { Cx = (long)(widthMm * EmuPerMm), Cy = (long)(heightMm * EmuPerMm) },
            new DW.DocProperties { Id = id, Name = $"Image {id}", Description = image.Alt },
            new A.Graphic(new A.GraphicData(
                new PIC.Picture(
                    new PIC.NonVisualPictureProperties(
                        new PIC.NonVisualDrawingProperties { Id = id, Name = $"Image {id}", Description = image.Alt },
                        new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(new A.Blip { Embed = relationship }, new A.Stretch(new A.FillRectangle())),
                    new PIC.ShapeProperties(
                        new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = (long)(widthMm * EmuPerMm), Cy = (long)(heightMm * EmuPerMm) }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        { DistanceFromTop = 0, DistanceFromBottom = 0, DistanceFromLeft = 0, DistanceFromRight = 0 });
        return new W.Paragraph(new W.ParagraphProperties(Spacing(image.SpaceBeforeMm), Align(image.Align)), new W.Run(drawing));
    }

    /// <summary>The element's box in a header/footer; in the body the image's own proportions, shrunk to fit the content width.</summary>
    private static (decimal WidthMm, decimal HeightMm) Size(ImageBlock image, int contentTwips)
    {
        if (image.WidthMm is { } w && image.HeightMm is { } h && w > 0 && h > 0)
        {
            return (w, h);
        }

        var (px, py) = ImageDimensions.Read(image.Bytes, image.IsPng) ?? (400, 200);
        var maxMm = contentTwips / TwipsPerMm;
        var widthMm = Math.Min(px * 25.4m / 96m, maxMm);
        return (widthMm, widthMm * py / px);
    }

    private static int Tw(decimal mm) => (int)Math.Round(mm * TwipsPerMm);
}

/// <summary>Pixel size of a PNG or JPEG, read from its header. Returns null when the header isn't understood.</summary>
internal static class ImageDimensions
{
    public static (int Width, int Height)? Read(byte[] bytes, bool isPng) => isPng ? Png(bytes) : Jpeg(bytes);

    private static (int, int)? Png(byte[] b)
    {
        // 8-byte signature, then the IHDR chunk: length (4), "IHDR" (4), width (4), height (4).
        if (b.Length < 24)
        {
            return null;
        }

        var w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
        var h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
        return w > 0 && h > 0 ? (w, h) : null;
    }

    private static (int, int)? Jpeg(byte[] b)
    {
        var i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF)
            {
                i++;
                continue;
            }

            var marker = b[i + 1];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                var h = (b[i + 5] << 8) | b[i + 6];
                var w = (b[i + 7] << 8) | b[i + 8];
                return w > 0 && h > 0 ? (w, h) : null;
            }

            i += 2 + ((b[i + 2] << 8) | b[i + 3]);
        }

        return null;
    }
}
