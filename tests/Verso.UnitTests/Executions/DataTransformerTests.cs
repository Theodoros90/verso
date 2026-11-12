using System.Text.Json;
using Verso.Application.Executions;

namespace Verso.UnitTests.Executions;

public sealed class DataTransformerTests
{
    private static TransformResult Run(string rules, string data)
    {
        using var config = JsonDocument.Parse($$"""{ "rules": {{rules}} }""");
        using var doc = JsonDocument.Parse(data);
        return DataTransformer.Apply(config.RootElement.Clone(), doc.RootElement.Clone());
    }

    private static string Rule(string target, string operation, string a = "", string b = "", string text = "") =>
        $$"""{ "target": "{{target}}", "operation": "{{operation}}", "a": "{{a}}", "b": "{{b}}", "text": "{{text}}" }""";

    private static string Json(TransformResult r) => r.Data!.Value.GetRawText();

    [Theory]
    [InlineData("add", "net", "vat", "4797.28")]
    [InlineData("subtract", "net", "vat", "3265.38")]
    [InlineData("multiply", "net", "2", "8062.66")]
    [InlineData("divide", "net", "2", "2015.665")]
    [InlineData("add", "net", "0.10", "4031.43")]
    [InlineData("add", "5", "6", "11")]
    public void Calculations(string op, string a, string b, string expected)
    {
        var r = Run($"[{Rule("out", op, a, b)}]", """{ "net": 4031.33, "vat": 765.95 }""");
        Assert.Empty(r.Issues);
        Assert.Equal(expected, r.Data!.Value.GetProperty("out").GetRawText());
    }

    [Fact]
    public void ARuleSeesWhatTheRulesAboveItWrote()
    {
        var r = Run($"[{Rule("totals.gross", "add", "totals.net", "totals.vat")}, {Rule("totals.gross", "round", "totals.gross", "1")}]", """{ "totals": { "net": 10.04, "vat": 1.91 } }""");
        Assert.Equal("""{"totals":{"net":10.04,"vat":1.91,"gross":12}}""", Json(r));
    }

    [Theory]
    [InlineData("2.675", "", "2.68")]
    [InlineData("2.675", "2", "2.68")]
    [InlineData("2.5", "0", "3")]
    [InlineData("\"4797.284\"", "2", "4797.28")]
    public void Round_DefaultsToTwoDecimals_AndAwayFromZero(string input, string places, string expected)
    {
        var r = Run($"[{Rule("out", "round", "v", places)}]", $$"""{ "v": {{input}} }""");
        Assert.Equal(expected, r.Data!.Value.GetProperty("out").GetRawText());
    }

    [Fact]
    public void Round_RefusesBadDecimals()
    {
        var r = Run($"[{Rule("out", "round", "v", "9")}]", """{ "v": 1.5 }""");
        Assert.Equal("transform.decimals", Assert.Single(r.Issues).Code);
    }

    [Theory]
    [InlineData("trim", "\"  a b \"", "\"a b\"")]
    [InlineData("uppercase", "\"cy99x\"", "\"CY99X\"")]
    [InlineData("lowercase", "\"AP@Kestrel.Example\"", "\"ap@kestrel.example\"")]
    [InlineData("uppercase", "5", "5")]
    [InlineData("copy", "[1,2]", "[1,2]")]
    [InlineData("date", "\"02/11/2026\"", "\"2026-11-02\"")]
    [InlineData("date", "\"2026-11-02T08:30:00Z\"", "\"2026-11-02\"")]
    public void Reformats(string op, string input, string expected)
    {
        var r = Run($"[{Rule("out", op, "v")}]", $$"""{ "v": {{input}} }""");
        Assert.Empty(r.Issues);
        Assert.Equal(expected, r.Data!.Value.GetProperty("out").GetRawText());
    }

    [Fact]
    public void Text_BuildsFromFields_WithFilters()
    {
        var r = Run($"[{Rule("customer.label", "text", text: "{{customer.name}} ({{invoice.number}}), {{amount|money}}")}]",
            """{ "customer": { "name": "Kestrel" }, "invoice": { "number": "INV-1" }, "amount": 4797.28 }""");
        Assert.Empty(r.Issues);
        Assert.StartsWith("Kestrel (INV-1), ", r.Data!.Value.GetProperty("customer").GetProperty("label").GetString());
    }

    [Fact]
    public void Text_WithAMissingField_Fails_NamingThePathOnly()
    {
        var r = Run($"[{Rule("x", "text", text: "Hi {{customer.name}}")}]", """{ "other": "Top Secret" }""");
        var issue = Assert.Single(r.Issues);
        Assert.Equal("transform.missing", issue.Code);
        Assert.Equal("rules[0]", issue.Field);
        Assert.Contains("customer.name", issue.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Top Secret", issue.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ \"v\": \"\" }", "\"n/a\"")]
    [InlineData("{ \"v\": \"   \" }", "\"n/a\"")]
    [InlineData("{ \"v\": null }", "\"n/a\"")]
    [InlineData("{ }", "\"n/a\"")]
    [InlineData("{ \"v\": \"set\" }", "\"set\"")]
    [InlineData("{ \"v\": 0 }", "0")]
    public void Default_UsesTheFallbackOnlyWhenEmpty(string data, string expected)
    {
        var r = Run($"[{Rule("out", "default", "v", text: "n/a")}]", data);
        Assert.Empty(r.Issues);
        Assert.Equal(expected, r.Data!.Value.GetProperty("out").GetRawText());
    }

    [Fact]
    public void Default_ANumericFallbackIsANumber()
    {
        var r = Run($"[{Rule("vat", "default", "vat", text: "0")}]", "{ }");
        Assert.Equal("""{"vat":0}""", Json(r));
    }

    [Fact]
    public void Remove_DeletesTheField_AndIgnoresAMissingOne()
    {
        var r = Run($"[{Rule("secret.token", "remove")}, {Rule("nope.nothing", "remove")}]", """{ "secret": { "token": "x", "keep": 1 }, "a": 1 }""");
        Assert.Equal("""{"secret":{"keep":1},"a":1}""", Json(r));
    }

    [Fact]
    public void Targets_MergeIntoObjects_AndReplaceScalarsInTheWay()
    {
        var r = Run($"[{Rule("customer.label", "copy", "n")}, {Rule("lines.first", "copy", "n")}]", """{ "n": "Ada", "customer": { "email": "a@b.example" }, "lines": [1] }""");
        Assert.Equal("""{"n":"Ada","customer":{"email":"a@b.example","label":"Ada"},"lines":{"first":"Ada"}}""", Json(r));
    }

    [Theory]
    [InlineData("divide", "a", "z", "transform.divide_by_zero")]
    [InlineData("add", "a", "nope", "transform.missing")]
    [InlineData("add", "nope", "a", "transform.missing")]
    [InlineData("add", "word", "a", "transform.not_number")]
    [InlineData("add", "a", "word", "transform.not_number")]
    [InlineData("add", "a", "", "transform.missing")]
    [InlineData("copy", "nope", "", "transform.missing")]
    [InlineData("date", "word", "", "transform.not_date")]
    public void Failures_AreCoded_AndNeverEchoValues(string op, string a, string b, string code)
    {
        var r = Run($"[{Rule("out", op, a, b)}]", """{ "a": 4, "z": 0, "word": "Top Secret" }""");
        Assert.Null(r.Data);
        var issue = Assert.Single(r.Issues);
        Assert.Equal(code, issue.Code);
        Assert.StartsWith("Rule 1: ", issue.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Top Secret", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryFailingRuleIsReported_WithItsIndex()
    {
        var r = Run($"[{Rule("x", "copy", "nope")}, {Rule("y", "copy", "a")}, {Rule("z", "divide", "a", "0")}]", """{ "a": 1 }""");
        Assert.Equal(["rules[0]", "rules[2]"], r.Issues.Select(i => i.Field));
    }

    [Fact]
    public void DataThatIsNotAnObject_Fails()
    {
        using var config = JsonDocument.Parse("""{ "rules": [] }""");
        using var doc = JsonDocument.Parse("[1]");
        Assert.Equal("transform.not_record", Assert.Single(DataTransformer.Apply(config.RootElement, doc.RootElement).Issues).Code);
    }

    [Theory]
    [InlineData("add", "t.x", "a", "b", "", null)]
    [InlineData("add", "t.x", "a", "", "", "The second value must be a field path or a number.")]
    [InlineData("add", "t.x", "a b", "1", "", "The first value must be a field path or a number.")]
    [InlineData("round", "t.x", "a", "", "", null)]
    [InlineData("round", "t.x", "a", "x y", "", "The second value must be a field path or a number.")]
    [InlineData("copy", "t.x", "", "", "", "The first value must be a field path or a number.")]
    [InlineData("text", "t.x", "", "", "", "Enter the text to build, such as {{customer.name}} ({{invoice.number}}).")]
    [InlineData("remove", "t.x", "", "", "", null)]
    [InlineData("remove", "t x", "", "", "", "The target must be a field path such as totals.gross.")]
    [InlineData("eval", "t.x", "a", "", "", "Unknown operation 'eval'.")]
    public void Incomplete_ChecksWhatEachOperationNeeds(string op, string target, string a, string b, string text, string? expected) =>
        Assert.Equal(expected, DataTransformer.Incomplete(op, target, a, b, text));
}
