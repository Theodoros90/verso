namespace Verso.Rendering.Layout;

// Layout model v1 (plan decision D4): fixed-height header and footer regions with absolutely positioned
// elements, and a flowing body whose blocks stack and may break across pages. Units are millimetres
// for geometry and points for type. JSON shape and rules: docs/layout-model.md.

public sealed record TemplateLayout(FixedRegion Header, FlowRegion Body, FixedRegion Footer)
{
    public static TemplateLayout Empty { get; } = new(FixedRegion.Empty, FlowRegion.Empty, FixedRegion.Empty);
}

public sealed record FixedRegion(decimal HeightMm, IReadOnlyList<PositionedElement> Elements)
{
    public static FixedRegion Empty { get; } = new(0, []);
}

public sealed record FlowRegion(IReadOnlyList<FlowBlock> Blocks)
{
    public static FlowRegion Empty { get; } = new([]);
}

public sealed record PositionedElement(LayoutElement Element, decimal XMm, decimal YMm, decimal WidthMm, decimal HeightMm);

public sealed record FlowBlock(LayoutElement Element, decimal SpaceBeforeMm);

public enum TextAlign { Left, Center, Right }

public enum FontFamily { Sans, Serif, Mono }

public enum ImageFit { Contain, Cover }

/// <summary>Typography for text-bearing elements. Color is always a validated <c>#RRGGBB</c>.</summary>
public sealed record TextStyle(decimal FontSizePt, bool Bold, bool Italic, TextAlign Align, string Color, FontFamily Font)
{
    public static TextStyle Default { get; } = new(10, false, false, TextAlign.Left, "#1A232C", FontFamily.Sans);
}

public abstract record LayoutElement(string Id);

/// <summary>Static text that may contain <c>{{path}}</c> / <c>{{path|filter}}</c> tokens.</summary>
public sealed record TextElement(string Id, string Text, TextStyle Style) : LayoutElement(Id);

/// <summary>One bound value, with an optional label in front of it.</summary>
public sealed record FieldElement(string Id, string Binding, string? Filter, string? Label, TextStyle Style) : LayoutElement(Id);

/// <summary>A table with one row per item of an array; column bindings are relative to the row.</summary>
public sealed record TableElement(string Id, string Binding, IReadOnlyList<TableColumn> Columns, string? EmptyText, TextStyle Style) : LayoutElement(Id);

public sealed record TableColumn(string Header, string Binding, string? Filter, TextAlign Align, decimal? WidthPct);

public sealed record DividerElement(string Id, decimal ThicknessPt, string Color) : LayoutElement(Id);

public sealed record BoxElement(string Id, string BorderColor, string? Fill, decimal RadiusMm) : LayoutElement(Id);

/// <summary>An embedded PNG or JPEG (<c>data:</c> URI). External URLs are never allowed.</summary>
public sealed record ImageElement(string Id, string DataUri, string Alt, ImageFit Fit) : LayoutElement(Id);

public sealed record SignatureElement(string Id, string Label) : LayoutElement(Id);
