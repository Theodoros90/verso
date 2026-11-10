using System.Text.Json;
using Verso.Application.Executions;

namespace Verso.UnitTests.Executions;

public sealed class ValidationRulesTests
{
    private static readonly JsonElement StandardRules = Parse("""
        { "required": ["invoice.number", "invoice.date", "customer.name", "customer.address", "lines", "totals.gross"],
          "types": [
            { "field": "invoice.date", "type": "date" }, { "field": "invoice.dueDate", "type": "date" },
            { "field": "totals.gross", "type": "number" }, { "field": "customer.email", "type": "email" },
            { "field": "customer.vatNumber", "type": "eu-vat" }, { "field": "issuer.vatNumber", "type": "eu-vat" },
            { "field": "payment.iban", "type": "iban" } ] }
        """);

    [Fact]
    public void ValidFixture_PassesEveryRule()
    {
        Assert.Empty(ValidationRules.Check(StandardRules, Fixture("sample.json")));
    }

    [Fact]
    public void BrokenFixture_ListsEachFailedRule_WithoutEchoingValues()
    {
        var issues = ValidationRules.Check(StandardRules, Fixture("broken.json"));

        Assert.Equal(
            [
                ("customer.name", "required"),
                ("invoice.date", "type:date"),
                ("totals.gross", "type:number"),
                ("customer.email", "type:email"),
                ("customer.vatNumber", "type:eu-vat"),
                ("payment.iban", "type:iban"),
            ],
            issues.Select(i => (i.Path, i.Rule)));
        Assert.All(issues, i => Assert.DoesNotContain("02/11/2026", i.Message, StringComparison.Ordinal));
        Assert.Equal("expected YYYY-MM-DD", issues.Single(i => i.Path == "invoice.date").Message);
    }

    [Theory]
    [InlineData("""{ "a": "" }""")]
    [InlineData("""{ "a": "   " }""")]
    [InlineData("""{ "a": [] }""")]
    [InlineData("""{ "a": null }""")]
    [InlineData("""{ }""")]
    public void Required_TreatsMissingNullAndEmptyAsMissing(string data)
    {
        var issue = Assert.Single(ValidationRules.Check(Parse("""{ "required": ["a"], "types": [] }"""), Parse(data)));
        Assert.Equal("missing or empty", issue.Message);
    }

    [Theory]
    [InlineData("""{ "a": 0 }""")]
    [InlineData("""{ "a": false }""")]
    [InlineData("""{ "a": { "b": 1 } }""")]
    public void Required_AcceptsPresentValues(string data) =>
        Assert.Empty(ValidationRules.Check(Parse("""{ "required": ["a"], "types": [] }"""), Parse(data)));

    [Fact]
    public void TypeCheck_OnMissingField_Passes()
    {
        Assert.Empty(ValidationRules.Check(Parse("""{ "required": [], "types": [ { "field": "x", "type": "date" } ] }"""), Parse("{}")));
    }

    [Theory]
    [InlineData("text", "\"abc\"", true)]
    [InlineData("text", "12", false)]
    [InlineData("number", "12.5", true)]
    [InlineData("number", "\"12.5\"", false)]
    [InlineData("date", "\"2026-11-02\"", true)]
    [InlineData("date", "\"2026-02-30\"", false)]
    [InlineData("date", "\"2026-11-02T10:00:00Z\"", false)]
    [InlineData("email", "\"ap@kestrel-logistics.example\"", true)]
    [InlineData("email", "\"ap@localhost\"", false)]
    [InlineData("email", "\"a b@x.example\"", false)]
    [InlineData("eu-vat", "\"CY10293847X\"", true)]
    [InlineData("eu-vat", "\"DE 123 456 789\"", true)]
    [InlineData("eu-vat", "\"EL123456789\"", true)]
    [InlineData("eu-vat", "\"NL123456789B01\"", true)]
    [InlineData("eu-vat", "\"CY123\"", false)]
    [InlineData("eu-vat", "\"US123456789\"", false)]
    [InlineData("iban", "\"CY82 0000 0000 0000 0000 0000 0000\"", true)]
    [InlineData("iban", "\"DE89370400440532013000\"", true)]
    [InlineData("iban", "\"GB82 WEST 1234 5698 7654 32\"", true)]
    [InlineData("iban", "\"CY00 0000 0000 0000 0000 0000 0000\"", false)]
    [InlineData("iban", "\"DE8937040044053201300\"", false)]
    [InlineData("iban", "\"DE88370400440532013000\"", false)]
    public void TypeChecks(string type, string json, bool valid)
    {
        using var document = JsonDocument.Parse(json);
        var message = ValidationRules.CheckType(type, document.RootElement);
        Assert.Equal(valid, message is null);
    }

    private static JsonElement Fixture(string name) =>
        Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "invoice", name)));

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
