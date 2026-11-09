namespace Verso.Domain.Templates;

/// <summary>Page size and margins for a template version. MVP supports A4 only.</summary>
public sealed record PageSettings(
    string Size,
    string Orientation,
    decimal MarginTopMm,
    decimal MarginRightMm,
    decimal MarginBottomMm,
    decimal MarginLeftMm)
{
    public static PageSettings DefaultA4 { get; } = new("A4", "portrait", 18, 16, 18, 16);
}

/// <summary>
/// The editable content of a template version. JSON parts are stored as text; their structure is
/// validated by the application layer (layout model details arrive with the renderer in M3).
/// </summary>
public sealed record TemplateContent(PageSettings PageSettings, string Layout, string DataSchema, string SampleData)
{
    // EF Core materialises this complex type through here (it can't bind the nested PageSettings parameter).
    private TemplateContent() : this(PageSettings.DefaultA4, "", "", "") { }

    public static TemplateContent Empty { get; } = new(
        PageSettings.DefaultA4,
        Layout: """{"layoutSchemaVersion":1}""",
        DataSchema: """{"type":"object"}""",
        SampleData: "{}");
}
