using System.Text.Json;
using System.Text.Json.Nodes;
using Verso.Application.Executions;

namespace Verso.UnitTests.Executions;

public sealed class DataMergerTests
{
    private const string Customers = """[ { "id": "C-1", "name": "Kestrel Logistics Ltd", "address": { "city": "Limassol" } }, { "id": 2, "name": "Numeric Key Ltd" } ]""";

    private static JsonElement Config(string key = "customer.id", string lookupKey = "id", string lookup = Customers, string source = "", string into = "customer", bool overwrite = false, string strategy = "keep") =>
        JsonSerializer.SerializeToElement(new JsonObject
        {
            ["key"] = key, ["lookupKey"] = lookupKey, ["lookup"] = lookup, ["source"] = source, ["into"] = into, ["overwrite"] = overwrite, ["strategy"] = strategy,
        });

    private static MergeResult Run(JsonElement config, string data)
    {
        using var doc = JsonDocument.Parse(data);
        return DataMerger.Apply(config, doc.RootElement.Clone());
    }

    [Fact]
    public void MatchedRow_IsWrittenIntoTheRecord()
    {
        var r = Run(Config(), """{ "customer": { "id": "C-1" } }""");
        Assert.Empty(r.Issues);
        Assert.Equal("""{"customer":{"id":"C-1","name":"Kestrel Logistics Ltd","address":{"city":"Limassol"}}}""", r.Data!.Value.GetRawText());
        Assert.Equal((1, 0), (r.Matched, r.Unmatched));
    }

    [Fact]
    public void KeysCompareAsText_NumbersAndTrimmedStrings()
    {
        Assert.Equal(1, Run(Config(), """{ "customer": { "id": 2 } }""").Matched);
        Assert.Equal(1, Run(Config(), """{ "customer": { "id": "  C-1 " } }""").Matched);
        Assert.Equal(0, Run(Config(), """{ "customer": { "id": "c-1" } }""").Matched);
    }

    [Fact]
    public void ExistingValuesWin_UnlessOverwriteIsOn()
    {
        var data = """{ "customer": { "id": "C-1", "name": "Typed Name", "address": { "street": "1 Main St" } } }""";
        var kept = Run(Config(), data).Data!.Value;
        Assert.Equal("Typed Name", kept.GetProperty("customer").GetProperty("name").GetString());
        Assert.Equal("Limassol", kept.GetProperty("customer").GetProperty("address").GetProperty("city").GetString());
        Assert.Equal("1 Main St", kept.GetProperty("customer").GetProperty("address").GetProperty("street").GetString());

        var replaced = Run(Config(overwrite: true), data).Data!.Value;
        Assert.Equal("Kestrel Logistics Ltd", replaced.GetProperty("customer").GetProperty("name").GetString());
    }

    [Fact]
    public void EmptyInto_WritesAtTheTopLevel()
    {
        var r = Run(Config(key: "cid", into: ""), """{ "cid": "C-1" }""");
        Assert.Equal("Kestrel Logistics Ltd", r.Data!.Value.GetProperty("name").GetString());
    }

    [Fact]
    public void NoMatch_Keep_LeavesTheRecordAsItIs()
    {
        var r = Run(Config(), """{ "customer": { "id": "zzz" } }""");
        Assert.Empty(r.Issues);
        Assert.Equal("""{"customer":{"id":"zzz"}}""", r.Data!.Value.GetRawText());
        Assert.Equal((0, 1), (r.Matched, r.Unmatched));
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("drop")]
    public void NoMatch_OnASingleRecord_StopAndDropFail(string strategy)
    {
        var r = Run(Config(strategy: strategy), """{ "customer": { "id": "zzz" } }""");
        Assert.Null(r.Data);
        Assert.Equal("merge.unmatched", Assert.Single(r.Issues).Code);
        Assert.DoesNotContain("zzz", r.Issues[0].Message, StringComparison.Ordinal);
    }

    private const string Lines = """{ "lines": [ { "sku": "C-1", "qty": 1 }, { "sku": "nope", "qty": 2 }, { "sku": 2, "qty": 3 } ] }""";

    [Fact]
    public void List_EachEntryIsMatchedOnItsOwn()
    {
        var r = Run(Config(key: "sku", source: "lines", into: "product"), Lines);
        var lines = r.Data!.Value.GetProperty("lines");
        Assert.Equal(3, lines.GetArrayLength());
        Assert.Equal("Kestrel Logistics Ltd", lines[0].GetProperty("product").GetProperty("name").GetString());
        Assert.False(lines[1].TryGetProperty("product", out _));
        Assert.Equal("Numeric Key Ltd", lines[2].GetProperty("product").GetProperty("name").GetString());
        Assert.Equal((2, 1, 0), (r.Matched, r.Unmatched, r.Dropped));
    }

    [Fact]
    public void List_Drop_RemovesUnmatchedEntries()
    {
        var r = Run(Config(key: "sku", source: "lines", into: "product", strategy: "drop"), Lines);
        var lines = r.Data!.Value.GetProperty("lines");
        Assert.Equal(2, lines.GetArrayLength());
        Assert.Equal(3, lines[1].GetProperty("qty").GetInt32());
        Assert.Equal(1, r.Dropped);
    }

    [Fact]
    public void List_Stop_NamesTheEntry()
    {
        var r = Run(Config(key: "sku", source: "lines", into: "product", strategy: "stop"), Lines);
        Assert.Null(r.Data);
        Assert.Equal("source[1]", Assert.Single(r.Issues).Field);
    }

    [Fact]
    public void SourceThatIsNotAList_Fails()
    {
        var r = Run(Config(key: "sku", source: "lines"), """{ "lines": "x" }""");
        Assert.Equal("merge.no_list", Assert.Single(r.Issues).Code);
    }

    [Fact]
    public void Incomplete_ReportsEverythingTheStepCantRunWith()
    {
        Assert.Empty(DataMerger.Incomplete(Config()));
        Assert.Contains(DataMerger.Incomplete(Config(key: "")), p => p.StartsWith("Match on", StringComparison.Ordinal));
        Assert.Contains(DataMerger.Incomplete(Config(lookup: "[]")), p => p.Contains("at least one", StringComparison.Ordinal));
        Assert.Contains(DataMerger.Incomplete(Config(lookup: "not json")), p => p.Contains("valid JSON", StringComparison.Ordinal));
        Assert.Contains(DataMerger.Incomplete(Config(lookup: """[ { "id": "a" }, { "id": "a" } ]""")), p => p.Contains("repeats", StringComparison.Ordinal));
        Assert.Contains(DataMerger.Incomplete(Config(lookup: """[ { "name": "a" } ]""")), p => p.Contains("no 'id'", StringComparison.Ordinal));
        Assert.Contains(DataMerger.Incomplete(Config(strategy: "bogus")), p => p.Contains("no match", StringComparison.Ordinal));
    }
}
