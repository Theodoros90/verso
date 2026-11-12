using System.Text.Json;
using System.Text.Json.Nodes;
using Verso.Application.Executions;

namespace Verso.UnitTests.Executions;

public sealed class IfElseTests
{
    private static JsonObject Condition(string field, string op, string value = "") => new() { ["field"] = field, ["op"] = op, ["value"] = value };

    private static JsonElement Config(string field, string op, string value = "") => Config(Condition(field, op, value));

    private static JsonElement Config(params JsonObject[] conditions) =>
        JsonSerializer.SerializeToElement(new JsonObject { ["conditions"] = new JsonArray([.. conditions.Select(c => c.DeepClone())]) });

    private static JsonObject Or(JsonObject condition)
    {
        var copy = (JsonObject)condition.DeepClone();
        copy["join"] = "or";
        return copy;
    }

    private static JsonObject WithField(string field, string op, string otherField) =>
        new() { ["field"] = field, ["op"] = op, ["compare"] = "field", ["value"] = otherField };

    private const string Invoice = """{ "totals": { "gross": 4797.28, "net": 4031.33 }, "customer": { "country": "CY" }, "limit": "5000", "name": "Kestrel Ltd", "short": "Kestrel" }""";

    [Fact]
    public void ConditionsAreAndedByDefault_AndTheStepNeedsAtLeastOne()
    {
        Assert.True(Run(Config(Condition("totals.gross", "greater-than", "1000"), Condition("customer.country", "equals", "cy")), Invoice));
        Assert.False(Run(Config(Condition("totals.gross", "greater-than", "1000"), Condition("customer.country", "equals", "DE")), Invoice));
        Assert.False(Run(Config(Condition("totals.gross", "greater-than", "9000"), Condition("customer.country", "equals", "CY")), Invoice));
        Assert.False(Run(Config(), Invoice));
    }

    [Fact]
    public void Or_JoinsAlternatives_AndAndBindsTighter()
    {
        var yes = Condition("customer.country", "equals", "CY");
        var no = Condition("customer.country", "equals", "DE");
        Assert.True(Run(Config(no, Or(yes)), Invoice)); // false OR true
        Assert.False(Run(Config(no, Or(Condition("totals.gross", "less-than", "1"))), Invoice));
        // A OR B AND C is A OR (B AND C): true OR (false AND false) holds, false OR (true AND false) does not.
        Assert.True(Run(Config(yes, Or(Condition("a", "is-not-empty")), Condition("b", "is-not-empty")), Invoice));
        Assert.False(Run(Config(no, Or(yes), Condition("b", "is-not-empty")), Invoice));
        // A AND B OR C: (A AND B) OR C
        Assert.True(Run(Config(yes, no, Or(yes)), Invoice));
        Assert.False(Run(Config(no, yes, Or(no)), Invoice));
    }

    [Fact]
    public void ComparingWithAnotherField_ReadsItFromTheData()
    {
        Assert.True(Run(Config(WithField("totals.gross", "less-than", "limit")), Invoice)); // "5000" is read as a number
        Assert.False(Run(Config(WithField("totals.gross", "greater-than", "totals.net")), """{ "totals": { "gross": 1, "net": 2 } }"""));
        Assert.True(Run(Config(WithField("name", "starts-with", "short")), Invoice));
        Assert.True(Run(Config(WithField("name", "not-equals", "short")), Invoice));
        // The other field is missing: nothing is equal to it, so only the negative comparisons hold.
        Assert.False(Run(Config(WithField("name", "equals", "nowhere")), Invoice));
        Assert.False(Run(Config(WithField("totals.gross", "greater-than", "nowhere")), Invoice));
        Assert.True(Run(Config(WithField("name", "not-equals", "nowhere")), Invoice));
        Assert.True(Run(Config(WithField("name", "not-contains", "nowhere")), Invoice));
    }

    [Theory]
    [InlineData("not-contains", "zzz", true)]
    [InlineData("not-contains", "str", false)]
    [InlineData("ends-with", "LTD", true)]
    [InlineData("ends-with", "Kes", false)]
    [InlineData("greater-or-equal", "4797.28", true)]
    [InlineData("greater-or-equal", "4797.29", false)]
    [InlineData("less-or-equal", "4797.28", true)]
    [InlineData("less-or-equal", "4797.27", false)]
    public void MoreOperators(string op, string value, bool expected) =>
        Assert.Equal(expected, Run(Config(Condition(op.Contains("or-equal", StringComparison.Ordinal) ? "totals.gross" : "name", op, value)), Invoice));

    [Fact]
    public void Incomplete_PointsAtTheConditionThatNeedsAValue()
    {
        var problems = IfElse.Incomplete(Config(Condition("a", "equals", "x"), Condition("b", "equals", ""), Condition("c", "greater-than", "ten"), Condition("d", "is-empty")));
        Assert.Equal(["conditions[1].value", "conditions[2].value"], problems.Select(p => p.Field));
        Assert.Equal("conditions", Assert.Single(IfElse.Incomplete(Config())).Field);
        // Comparing with a field needs a field path, and no number.
        Assert.Empty(IfElse.Incomplete(Config(WithField("a", "greater-than", "totals.net"))));
        Assert.Equal(["conditions[0].value"], IfElse.Incomplete(Config(WithField("a", "equals", ""))).Select(p => p.Field));
        Assert.Equal(["conditions[0].value"], IfElse.Incomplete(Config(WithField("a", "equals", "not a path"))).Select(p => p.Field));
    }

    private static bool Run(JsonElement config, string data)
    {
        using var doc = JsonDocument.Parse(data);
        return IfElse.Evaluate(config, doc.RootElement.Clone());
    }

    [Theory]
    [InlineData("equals", "EUR", true)]
    [InlineData("equals", " eur ", true)]
    [InlineData("equals", "USD", false)]
    [InlineData("not-equals", "USD", true)]
    [InlineData("not-equals", "eur", false)]
    [InlineData("contains", "ur", true)]
    [InlineData("contains", "x", false)]
    [InlineData("starts-with", "EU", true)]
    [InlineData("starts-with", "UR", false)]
    public void TextComparisons_IgnoreCaseAndSurroundingSpaces(string op, string value, bool expected) =>
        Assert.Equal(expected, Run(Config("invoice.currency", op, value), """{ "invoice": { "currency": " Eur" } }"""));

    [Theory]
    [InlineData("greater-than", "10000", true)]
    [InlineData("greater-than", "12500.5", false)]
    [InlineData("less-than", "20000", true)]
    [InlineData("less-than", "12500.5", false)]
    public void SizeComparisons_UseNumbers_FromJsonNumbersAndText(string op, string value, bool expected)
    {
        Assert.Equal(expected, Run(Config("totals.gross", op, value), """{ "totals": { "gross": 12500.5 } }"""));
        Assert.Equal(expected, Run(Config("totals.gross", op, value), """{ "totals": { "gross": "12500.50" } }"""));
    }

    [Fact]
    public void SizeComparisons_AreFalseWhenEitherSideIsNotANumber()
    {
        Assert.False(Run(Config("totals.gross", "greater-than", "10"), """{ "totals": { "gross": "lots" } }"""));
        Assert.False(Run(Config("totals.gross", "less-than", "10"), """{ "totals": { "gross": "lots" } }"""));
        Assert.False(Run(Config("totals.gross", "greater-than", "10"), """{ "totals": {} }"""));
        Assert.False(Run(Config("totals.gross", "less-than", "ten"), """{ "totals": { "gross": 5 } }"""));
    }

    [Theory]
    [InlineData("""{ "x": "" }""", true)]
    [InlineData("""{ "x": "  " }""", true)]
    [InlineData("""{ "x": null }""", true)]
    [InlineData("""{ }""", true)]
    [InlineData("""{ "x": [] }""", true)]
    [InlineData("""{ "x": {} }""", true)]
    [InlineData("""{ "x": 0 }""", false)]
    [InlineData("""{ "x": false }""", false)]
    [InlineData("""{ "x": [1] }""", false)]
    [InlineData("""{ "x": "a" }""", false)]
    public void Emptiness_CoversMissingNullBlankAndEmptyContainers(string data, bool empty)
    {
        Assert.Equal(empty, Run(Config("x", "is-empty"), data));
        Assert.Equal(!empty, Run(Config("x", "is-not-empty"), data));
    }

    [Fact]
    public void MissingField_EqualsNothing_ButIsNotEqualToEverything()
    {
        Assert.False(Run(Config("a.b", "equals", "x"), """{ }"""));
        Assert.True(Run(Config("a.b", "not-equals", "x"), """{ }"""));
        Assert.False(Run(Config("a.b", "contains", "x"), """{ "a": 5 }"""));
    }

    [Fact]
    public void Booleans_CompareAsTheirWrittenForm() =>
        Assert.True(Run(Config("paid", "equals", "true"), """{ "paid": true }"""));
}
