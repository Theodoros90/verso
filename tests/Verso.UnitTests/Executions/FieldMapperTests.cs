using System.Text.Json;
using Verso.Application.Executions;

namespace Verso.UnitTests.Executions;

public sealed class FieldMapperTests
{
    private static MapResult Run(string rows, string data)
    {
        using var config = JsonDocument.Parse($$"""{ "rows": {{rows}} }""");
        using var doc = JsonDocument.Parse(data);
        return FieldMapper.Apply(config.RootElement.Clone(), doc.RootElement.Clone());
    }

    private static string Row(string from, string to, string transform = "as-is") => $$"""{ "from": "{{from}}", "to": "{{to}}", "transform": "{{transform}}" }""";

    [Fact]
    public void Copies_Nested_AndKeepsUnmappedFields()
    {
        var r = Run($"[{Row("a.b", "x.y.z")}]", """{ "a": { "b": 1 }, "keep": true }""");
        Assert.Equal("""{"a":{"b":1},"keep":true,"x":{"y":{"z":1}}}""", r.Data!.Value.GetRawText());
    }

    [Fact]
    public void MappedValues_MergeIntoExistingObjects_AndReplaceScalarsInTheWay()
    {
        var r = Run($"[{Row("n", "customer.name")}, {Row("n", "lines.first")}]", """{ "n": "Ada", "customer": { "email": "a@b.example" }, "lines": [1] }""");
        Assert.Equal("""{"n":"Ada","customer":{"email":"a@b.example","name":"Ada"},"lines":{"first":"Ada"}}""", r.Data!.Value.GetRawText());
    }

    [Fact]
    public void SourcesAreReadFromTheOriginalData_SoRowOrderDoesntMatter()
    {
        var r = Run($"[{Row("a", "b")}, {Row("b", "c")}]", """{ "a": 1, "b": 2 }""");
        Assert.Equal("""{"a":1,"b":1,"c":2}""", r.Data!.Value.GetRawText());
    }

    [Fact]
    public void MissingSource_IsSkipped()
    {
        var r = Run($"[{Row("nope", "x")}, {Row("n", "m")}]", """{ "n": null }""");
        Assert.Empty(r.Issues);
        Assert.Equal("""{"n":null}""", r.Data!.Value.GetRawText());
    }

    [Theory]
    [InlineData("trim", "\"  a b \"", "\"a b\"")]
    [InlineData("uppercase", "\"cy99x\"", "\"CY99X\"")]
    [InlineData("uppercase", "5", "5")]
    [InlineData("number", "\"4797.28\"", "4797.28")]
    [InlineData("number", "\"12,5\"", "12.5")]
    [InlineData("number", "7", "7")]
    [InlineData("round2", "4797.284", "4797.28")]
    [InlineData("round2", "\"2.675\"", "2.68")]
    [InlineData("date", "\"02/11/2026\"", "\"2026-11-02\"")]
    [InlineData("date", "\"2.11.2026\"", "\"2026-11-02\"")]
    [InlineData("date", "\"2026-11-02T08:30:00Z\"", "\"2026-11-02\"")]
    public void Transforms(string transform, string input, string expected)
    {
        var r = Run($"[{Row("v", "out", transform)}]", $$"""{ "v": {{input}} }""");
        Assert.Empty(r.Issues);
        Assert.Equal(expected, r.Data!.Value.GetProperty("out").GetRawText());
    }

    [Theory]
    [InlineData("number", "\"1,234.50\"")]
    [InlineData("number", "\"abc\"")]
    [InlineData("round2", "true")]
    [InlineData("date", "\"31/02/2026\"")]
    [InlineData("date", "12")]
    public void UnreadableValues_FailTheRow_WithoutEchoingThem(string transform, string input)
    {
        var r = Run($"[{Row("ok", "x")}, {Row("v", "out", transform)}]", $$"""{ "ok": 1, "v": {{input}} }""");
        Assert.Null(r.Data);
        var issue = Assert.Single(r.Issues);
        Assert.Equal(("map.transform", "rows[1]"), (issue.Code, issue.Field));
        Assert.DoesNotContain("abc", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DataThatIsntAnObject_CantBeMapped()
    {
        var r = Run("[]", "[1]");
        Assert.Null(r.Data);
        Assert.Equal("map.not_record", Assert.Single(r.Issues).Code);
    }

    private const string Model = """
        { "type": "object", "properties": {
            "invoice": { "type": "object", "properties": { "number": { "type": "string" }, "date": { "type": "string", "format": "date" } } },
            "customer": { "type": "object", "properties": { "name": { "type": "string" }, "address": { "type": "object", "properties": { "city": { "type": "string" } } } } },
            "lines": { "type": "array", "items": { "type": "object", "properties": { "amount": { "type": "number" } } } },
            "note": { "type": "string" } } }
        """;

    [Fact]
    public void TemplateFields_AreTheScalarsThroughObjects_AndEachListOnce()
    {
        Assert.Equal(["invoice.number", "invoice.date", "customer.name", "customer.address.city", "lines", "note"], FieldMapper.TemplateFields(Model));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{ "type": "object" }""")]
    public void TemplateFields_OfNoUsableModel_AreEmpty(string? schema) => Assert.Empty(FieldMapper.TemplateFields(schema));

    [Fact]
    public void Unmapped_ListsTemplateFieldsNoCompleteRowCovers()
    {
        using var config = JsonDocument.Parse($"{{ \"rows\": [{Row("a", "invoice.number")}, {Row("b", "customer.name")}] }}");
        var unmapped = FieldMapper.Unmapped(config.RootElement, FieldMapper.TemplateFields(Model));
        Assert.Equal(["invoice.date", "customer.address.city", "lines", "note"], unmapped);
    }
}
