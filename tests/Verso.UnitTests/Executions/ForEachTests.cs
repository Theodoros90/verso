using System.Text.Json;
using Verso.Application.Executions;

namespace Verso.UnitTests.Executions;

public sealed class ForEachTests
{
    private static JsonElement J(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static JsonElement Config(string mode = "each", string item = "item", string field = "lines") =>
        J($$"""{ "field": "{{field}}", "mode": "{{mode}}", "item": "{{item}}" }""");

    [Fact]
    public void Items_OfAList_AreItsElements() =>
        Assert.Equal(3, ForEach.Items(Config(), J("""{ "lines": [1, 2, 3] }""")).Count);

    [Theory]
    [InlineData("""{ }""")]
    [InlineData("""{ "lines": null }""")]
    [InlineData("""{ "lines": "" }""")]
    [InlineData("""{ "lines": "   " }""")]
    [InlineData("""{ "lines": [] }""")]
    public void Items_MissingNullBlankOrEmpty_AreNone(string data) =>
        Assert.Empty(ForEach.Items(Config(), J(data)));

    [Theory]
    [InlineData("""{ "lines": "one" }""")]
    [InlineData("""{ "lines": { "a": 1 } }""")]
    [InlineData("""{ "lines": 5 }""")]
    public void Items_ASingleValue_IsAListOfOne(string data) =>
        Assert.Single(ForEach.Items(Config(), J(data)));

    [Fact]
    public void Items_ReadNestedPaths() =>
        Assert.Equal(2, ForEach.Items(Config(field: "invoice.rows"), J("""{ "invoice": { "rows": ["a", "b"] } }""")).Count);

    [Fact]
    public void EachMode_KeepsTheRecord_AndAddsTheItemUnderItsName()
    {
        var data = J("""{ "invoice": { "number": "A1" }, "lines": [ { "description": "x" } ] }""");
        var item = ForEach.Items(Config(item: "line"), data)[0];

        var result = ForEach.IterationData(Config(item: "line"), data, item);

        Assert.Equal("A1", result.GetProperty("invoice").GetProperty("number").GetString());
        Assert.Equal("x", result.GetProperty("line").GetProperty("description").GetString());
        Assert.Equal(1, result.GetProperty("lines").GetArrayLength());
    }

    [Fact]
    public void EachMode_ReplacesAFieldWithTheItemName()
    {
        var data = J("""{ "item": "old", "lines": [ 7 ] }""");

        var result = ForEach.IterationData(Config(), data, ForEach.Items(Config(), data)[0]);

        Assert.Equal(7, result.GetProperty("item").GetInt32());
    }

    [Fact]
    public void SplitMode_AnObjectItem_IsTheRecord()
    {
        var data = J("""{ "invoice": { "number": "A1" }, "lines": [ { "description": "x" } ] }""");

        var result = ForEach.IterationData(Config("split"), data, ForEach.Items(Config("split"), data)[0]);

        Assert.Equal("x", result.GetProperty("description").GetString());
        Assert.False(result.TryGetProperty("invoice", out _));
    }

    [Fact]
    public void SplitMode_AScalarItem_BecomesAFieldNamedByTheItemName()
    {
        var data = J("""{ "lines": [ "x" ] }""");

        var result = ForEach.IterationData(Config("split", "value"), data, ForEach.Items(Config(), data)[0]);

        Assert.Equal("""{"value":"x"}""", result.GetRawText());
    }

    [Fact]
    public void IterationData_OutlivesTheDocumentItWasBuiltFrom()
    {
        JsonElement result;
        using (var doc = JsonDocument.Parse("""{ "lines": [ { "a": 1 } ] }"""))
        {
            result = ForEach.IterationData(Config(), doc.RootElement, doc.RootElement.GetProperty("lines")[0]);
        }

        Assert.Equal(1, result.GetProperty("item").GetProperty("a").GetInt32());
    }

    [Fact]
    public void Ports_AreNamedBySettings_WithDefaults()
    {
        var named = ForEach.Ports(J("""{ "eachLabel": "Per line", "emptyLabel": "Nothing" }"""));
        var defaults = ForEach.Ports(J("""{ "eachLabel": " ", "emptyLabel": "" }"""));

        Assert.Equal([("each", "Per line", true), ("empty", "Nothing", false)], named.Select(p => (p.Id, p.Label, p.Required)));
        Assert.Equal([("each", "For each item", true), ("empty", "No items", false)], defaults.Select(p => (p.Id, p.Label, p.Required)));
    }
}
