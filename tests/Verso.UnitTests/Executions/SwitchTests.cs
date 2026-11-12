using System.Text.Json;
using System.Text.Json.Nodes;
using Verso.Application.Executions;

namespace Verso.UnitTests.Executions;

public sealed class SwitchTests
{
    private static JsonObject Branch(string id, string label, string value, string compare = "value") =>
        new() { ["id"] = id, ["label"] = label, ["compare"] = compare, ["value"] = value };

    private static JsonElement Config(string op, bool other, params JsonObject[] branches) =>
        JsonSerializer.SerializeToElement(new JsonObject
        {
            ["field"] = "customer.deliveryPreference", ["op"] = op, ["other"] = other, ["branches"] = new JsonArray([.. branches.Select(b => b.DeepClone())]),
        });

    private static string? Pick(JsonElement config, string data)
    {
        using var doc = JsonDocument.Parse(data);
        return Switch.Pick(config, doc.RootElement.Clone());
    }

    private static readonly JsonElement Delivery = Config("equals", true, Branch("email", "Email", "email"), Branch("print", "Print", "print"));

    [Theory]
    [InlineData("""{ "customer": { "deliveryPreference": "email" } }""", "email")]
    [InlineData("""{ "customer": { "deliveryPreference": " PRINT " } }""", "print")]
    [InlineData("""{ "customer": { "deliveryPreference": "portal" } }""", "other")]
    [InlineData("""{ "customer": {} }""", "other")]
    public void TheMatchingBranchDecides_ElseOther(string data, string expected) => Assert.Equal(expected, Pick(Delivery, data));

    [Fact]
    public void WithoutOther_NoMatchLeavesNowhere() =>
        Assert.Null(Pick(Config("equals", false, Branch("email", "Email", "email")), """{ "customer": { "deliveryPreference": "portal" } }"""));

    [Fact]
    public void TheFirstMatchWins()
    {
        var config = Config("starts-with", false, Branch("a", "A", "em"), Branch("b", "B", "email"));
        Assert.Equal("a", Pick(config, """{ "customer": { "deliveryPreference": "email" } }"""));
    }

    [Fact]
    public void BranchesCanCompareWithAnotherField() =>
        Assert.Equal("same", Pick(Config("equals", true, Branch("same", "Same as billing", "billing.pref", "field")),
            """{ "customer": { "deliveryPreference": "Email" }, "billing": { "pref": "email" } }"""));

    [Fact]
    public void NumberOperators_RouteBySize()
    {
        var config = Config("greater-or-equal", true, Branch("big", "Big", "10000"), Branch("mid", "Mid", "1000"));
        Assert.Equal("big", Pick(config, """{ "customer": { "deliveryPreference": 12000 } }"""));
        Assert.Equal("mid", Pick(config, """{ "customer": { "deliveryPreference": "1000.5" } }"""));
        Assert.Equal("other", Pick(config, """{ "customer": { "deliveryPreference": 5 } }"""));
    }

    [Fact]
    public void Ports_AreTheBranchesInOrder_ThenOther()
    {
        Assert.Equal([("email", "Email"), ("print", "Print"), ("other", "Other")], Switch.Ports(Delivery).Select(p => (p.Id, p.Label)));
        Assert.All(Switch.Ports(Delivery), p => Assert.False(p.Required));
        Assert.Equal(["a"], Switch.Ports(Config("equals", false, Branch("a", "A", "x"))).Select(p => p.Id));
        Assert.Equal("Untitled", Switch.Ports(Config("equals", false, Branch("a", "", "x")))[0].Label);
        // A repeated or reserved id gives no second output (the settings are reported when validated).
        Assert.Equal(["a"], Switch.Ports(Config("equals", false, Branch("a", "A", "x"), Branch("a", "A2", "y"), Branch("other", "O", "z"))).Select(p => p.Id));
    }

    [Fact]
    public void EmptinessOperators_AreNotOffered() =>
        Assert.DoesNotContain(Switch.Operators.Keys, k => k.StartsWith("is-", StringComparison.Ordinal));

    [Fact]
    public void Incomplete_PointsAtTheBranchThatNeedsFixing()
    {
        Assert.Empty(Switch.Incomplete(Delivery));
        Assert.Equal(["branches"], Switch.Incomplete(Config("equals", false)).Select(p => p.Field));
        Assert.Empty(Switch.Incomplete(Config("equals", true)));

        var problems = Switch.Incomplete(Config("greater-than", true,
            Branch("a", "A", "1"), Branch("a", "B", "2"), Branch("other", "C", "3"), Branch("d", "", "4"), Branch("e", "E", ""), Branch("f", "F", "ten"), Branch("g", "G", "totals.net", "field")));
        Assert.Equal(["branches[1].id", "branches[2].id", "branches[3].label", "branches[4].value", "branches[5].value"], problems.Select(p => p.Field));
    }
}
