using Verso.Rendering.Layout;
using static Verso.UnitTests.Rendering.TestFiles;

namespace Verso.UnitTests.Rendering;

public sealed class LayoutParserTests
{
    [Fact]
    public void InvoiceFixture_IsValid_AndParsesAllRegions()
    {
        var layout = ParseValid(Fixture("invoice/layout.json"));

        Assert.Equal(62, layout.Header.HeightMm);
        Assert.Equal(10, layout.Header.Elements.Count);
        Assert.Equal(7, layout.Body.Blocks.Count);
        Assert.Single(layout.Footer.Elements);
        var table = Assert.IsType<TableElement>(layout.Body.Blocks[0].Element);
        Assert.Equal(5, table.Columns.Count);
        Assert.Equal(TextAlign.Right, table.Columns[4].Align);
    }

    [Fact]
    public void MinimalLayout_IsValid_AndEmpty()
    {
        var layout = ParseValid("""{ "layoutSchemaVersion": 1 }""");

        Assert.Equal(TemplateLayout.Empty, layout with { });
        Assert.Empty(layout.Body.Blocks);
    }

    [Theory]
    [InlineData("""{ "layoutSchemaVersion": 2 }""", "layoutSchemaVersion")]
    [InlineData("""{ "layoutSchemaVersion": 1, "colour": "red" }""", "colour")]
    [InlineData("""{ "layoutSchemaVersion": 1, "header": { "heightMm": 500, "elements": [] } }""", "header.heightMm")]
    [InlineData("""{ "layoutSchemaVersion": 1, "body": { "blocks": [ { "id": "a", "type": "box", "props": {} } ] } }""", "body.blocks[0].type")]
    [InlineData("""{ "layoutSchemaVersion": 1, "header": { "heightMm": 20, "elements": [ { "id": "a", "type": "table", "xMm": 0, "yMm": 0, "widthMm": 10, "heightMm": 10, "props": {} } ] } }""", "header.elements[0].type")]
    [InlineData("""{ "layoutSchemaVersion": 1, "body": { "blocks": [ { "id": "a", "type": "text", "props": { "text": "x", "style": { "color": "red;background:url(x)" } } } ] } }""", "body.blocks[0].props.style.color")]
    [InlineData("""{ "layoutSchemaVersion": 1, "body": { "blocks": [ { "id": "a", "type": "text", "props": { "text": "x", "style": { "align": "Right" } } } ] } }""", "body.blocks[0].props.style.align")]
    [InlineData("""{ "layoutSchemaVersion": 1, "body": { "blocks": [ { "id": "a", "type": "image", "props": { "src": "https://evil.example/x.png" } } ] } }""", "body.blocks[0].props.src")]
    [InlineData("""{ "layoutSchemaVersion": 1, "body": { "blocks": [ { "id": "a", "type": "image", "props": { "src": "data:image/svg+xml;base64,PHN2Zz4=" } } ] } }""", "body.blocks[0].props.src")]
    [InlineData("""{ "layoutSchemaVersion": 1, "body": { "blocks": [ { "id": "a", "type": "field", "props": { "binding": "invoice[0].number" } } ] } }""", "body.blocks[0].props.binding")]
    [InlineData("""{ "layoutSchemaVersion": 1, "body": { "blocks": [ { "id": "a", "type": "field", "props": { "binding": "x", "filter": "eval" } } ] } }""", "body.blocks[0].props.filter")]
    [InlineData("""{ "layoutSchemaVersion": 1, "body": { "blocks": [ { "id": "a", "type": "table", "props": { "binding": "lines", "columns": [] } } ] } }""", "body.blocks[0].props.columns")]
    [InlineData("""{ "layoutSchemaVersion": 1, "body": { "blocks": [ { "id": "a", "type": "text", "props": { "text": "x" } }, { "id": "a", "type": "text", "props": { "text": "y" } } ] } }""", "body.blocks[1].id")]
    [InlineData("""{ "layoutSchemaVersion": 1, "body": { "blocks": [ { "id": "bad id!", "type": "text", "props": { "text": "x" } } ] } }""", "body.blocks[0].id")]
    [InlineData("""{ "layoutSchemaVersion": 1, "header": { "heightMm": 20, "elements": [ { "id": "a", "type": "text", "props": { "text": "x" } } ] } }""", "header.elements[0].xMm")]
    public void InvalidLayouts_AreRejected_WithThePathOfTheProblem(string json, string expectedPath)
    {
        var (layout, errors) = LayoutParser.Parse(json);

        Assert.Null(layout);
        Assert.Contains(errors, e => e.Path == expectedPath);
    }

    [Fact]
    public void NotJson_IsRejected()
    {
        var (layout, errors) = LayoutParser.Parse("{ nope");

        Assert.Null(layout);
        Assert.Equal("Not valid JSON.", Assert.Single(errors).Message);
    }

    [Fact]
    public void AllErrorsAreReported_NotJustTheFirst()
    {
        var (_, errors) = LayoutParser.Parse("""
            { "layoutSchemaVersion": 1, "body": { "blocks": [
              { "id": "a", "type": "divider", "props": { "color": "blue", "thicknessPt": 99 } } ] } }
            """);

        Assert.Contains(errors, e => e.Path == "body.blocks[0].props.color");
        Assert.Contains(errors, e => e.Path == "body.blocks[0].props.thicknessPt");
    }
}
