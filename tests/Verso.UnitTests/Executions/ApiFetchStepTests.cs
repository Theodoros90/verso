using System.Text.Json;
using Verso.Application.Executions;

namespace Verso.UnitTests.Executions;

/// <summary>The Fetch from API step's query lines and path template (design time and the run-time resolving).</summary>
public sealed class ApiFetchStepTests
{
    private static JsonElement Data(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Codes(IReadOnlyList<object> issues) => string.Join(",", issues.Select(i => (string)i.GetType().GetProperty("code")!.GetValue(i)!));

    [Fact]
    public void ParseQuery_ReadsOneMappingPerLine_IgnoringBlankLines()
    {
        var (items, problem) = ApiFetchStep.ParseQuery("base = invoice.currency\n\n  symbols=a.b  \r\n");

        Assert.Null(problem);
        Assert.Equal([("base", "invoice.currency"), ("symbols", "a.b")], items);
    }

    [Theory]
    [InlineData("base invoice.currency", "base invoice.currency")]
    [InlineData("a = x\na = y", "'a' is listed more than once")]
    [InlineData("filter[a] = x\nfilter[b] = y", null)]
    [InlineData("has space = x", "has space")]
    [InlineData("a = ", "Query parameter 'a' needs a data path after the = sign.")]
    [InlineData("a = x..y", "Query parameter 'a': 'x..y' isn't a valid data path")]
    [InlineData("= x", "'' isn't a valid query parameter name.")]
    public void ParseQuery_ReportsAProblem_ForABadLine(string text, string? mentions)
    {
        var (_, problem) = ApiFetchStep.ParseQuery(text);

        if (mentions is null)
        {
            Assert.Null(problem);
            return;
        }

        Assert.NotNull(problem);
        Assert.Contains(mentions, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseQuery_NamesAreCaseSensitive_AndTheLinesAreLimited()
    {
        Assert.Null(ApiFetchStep.ParseQuery("a = x\nA = y").Problem);
        Assert.Null(ApiFetchStep.ParseQuery("").Problem);
        Assert.Null(ApiFetchStep.ParseQuery(null).Problem);
        Assert.Null(ApiFetchStep.ParseQuery(string.Join('\n', Enumerable.Range(1, 20).Select(i => $"p{i} = a.b"))).Problem);
        Assert.NotNull(ApiFetchStep.ParseQuery(string.Join('\n', Enumerable.Range(1, 21).Select(i => $"p{i} = a.b"))).Problem);
        Assert.NotNull(ApiFetchStep.ParseQuery(new string('n', 65) + " = a").Problem);
    }

    [Theory]
    [InlineData("/v1/latest")]
    [InlineData("/customers/{{customer.number}}/rate")]
    [InlineData("/customers/{{ customer.number }}")]
    [InlineData("/a/{{x}}/{{y.z}}")]
    [InlineData("/files/a.b-c_d~e")]
    [InlineData("/a//b")] // intentional: only a leading // is refused, as the service does
    [InlineData("/a/%2{{v}}")] // a static % stays allowed (percent-encoded text is legal); Resolve re-checks the result
    public void Validate_Accepts(string template) => Assert.Null(ApiPathTemplate.Validate(template));

    [Theory]
    [InlineData("//x")]
    [InlineData("x")]
    [InlineData("")]
    [InlineData("/a b")]
    [InlineData("/a?b=1")]
    [InlineData("/a#b")]
    [InlineData("/a\\b")]
    [InlineData("/a/../b")]
    [InlineData("/./a")]
    [InlineData("/a/%2e%2e/b")]
    [InlineData("/a/%2E/b")]
    [InlineData("/a/..")]
    [InlineData("/a/{{b")]
    [InlineData("/a/b}}")]
    [InlineData("/a/{b}")]
    [InlineData("/a/{{ 1bad }}")]
    [InlineData("/a/{{a..b}}")]
    [InlineData("/a/{{}}")]
    [InlineData("/ä")]
    public void Validate_Refuses(string template) => Assert.NotNull(ApiPathTemplate.Validate(template));

    [Fact]
    public void Validate_RefusesALongPath()
    {
        Assert.Null(ApiPathTemplate.Validate("/" + new string('a', 499)));
        Assert.NotNull(ApiPathTemplate.Validate("/" + new string('a', 500)));
    }

    [Theory]
    [InlineData("../x", "..%2Fx")]
    [InlineData("a/b", "a%2Fb")]
    [InlineData("x?y=1", "x%3Fy%3D1")]
    [InlineData("%2e", "%252e")]
    [InlineData("a#b", "a%23b")]
    [InlineData("a b", "a%20b")]
    [InlineData("Zürich", "Z%C3%BCrich")]
    public void Resolve_EncodesAValueIntoOneSegment(string value, string encoded)
    {
        var (path, issues) = ApiPathTemplate.Resolve("/customers/{{c.v}}/rate", Data(JsonSerializer.Serialize(new { c = new { v = value } })));

        Assert.Empty(issues);
        Assert.Equal($"/customers/{encoded}/rate", path);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("")]
    public void Resolve_RefusesADotOrEmptyValue_AsInvalidData_NamingThePath(string value)
    {
        var (path, issues) = ApiPathTemplate.Resolve("/a/{{c.v}}", Data(JsonSerializer.Serialize(new { c = new { v = value } })));

        Assert.Null(path);
        Assert.Equal("fetch.value_invalid", Codes(issues));
        Assert.Contains("c.v", JsonSerializer.Serialize(issues), StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_IssuesNeverEchoTheValue()
    {
        const string Sentinel = "SENTINEL-9f3a";
        foreach (var json in new[]
        {
            $$"""{ "c": { "v": { "x": "{{Sentinel}}" } } }""",
            $$"""{ "c": { "v": ["{{Sentinel}}"] } }""",
            $$"""{ "c": { "v": "{{Sentinel}}\uD800" } }""",
        })
        {
            var (_, issues) = ApiPathTemplate.Resolve("/a/{{c.v}}", Data(json));
            Assert.NotEmpty(issues);
            Assert.DoesNotContain(Sentinel, JsonSerializer.Serialize(issues), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("/a/%2{{v}}", "e")]
    [InlineData("/.%2{{v}}", "e")]
    [InlineData("/a/%2{{v}}", "E")]
    [InlineData("/a/.{{v}}", ".")] // ".." made of a static dot and a value
    [InlineData("/a/%2e{{v}}", "%2e")]
    public void Resolve_RefusesAPathWhoseSegmentBecomesADotSegmentAfterSubstitution(string template, string value)
    {
        Assert.Null(ApiPathTemplate.Validate(template)); // design time cannot know
        var (path, issues) = ApiPathTemplate.Resolve(template, Data(JsonSerializer.Serialize(new { v = value })));

        // "/a/%2e" + "%252e" is not a dot segment after one unescape ("%2e" text), so that row is the control: it must stay allowed.
        if (template == "/a/%2e{{v}}")
        {
            Assert.Empty(issues);
            Assert.Equal("/a/%2e%252e", path);
            return;
        }

        Assert.Null(path);
        Assert.Equal("fetch.value_invalid", Codes(issues));
        Assert.Contains("\"v\"", JsonSerializer.Serialize(issues), StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ALoneSurrogate_IsInvalid_ButAnEmojiPairIsEncoded()
    {
        var (path, issues) = ApiPathTemplate.Resolve("/a/{{v}}", Data("""{ "v": "x\uD800y" }"""));
        Assert.Null(path);
        Assert.Equal("fetch.value_invalid", Codes(issues));

        (path, issues) = ApiPathTemplate.Resolve("/a/{{v}}", Data("""{ "v": "😀" }"""));
        Assert.Empty(issues);
        Assert.Equal("/a/%F0%9F%98%80", path);
    }

    [Fact]
    public void IsValidText_RejectsALoneSurrogate_AndAcceptsPairsAndPlainText()
    {
        Assert.False(ApiFetchStep.IsValidText("a\uD800"));
        Assert.False(ApiFetchStep.IsValidText("\uDC00a"));
        Assert.True(ApiFetchStep.IsValidText("a😀"));
        Assert.True(ApiFetchStep.IsValidText("Zürich"));
        Assert.True(ApiFetchStep.IsValidText(""));
    }

    [Theory]
    [InlineData("1e21", "1000000000000000000000")] // fits a decimal
    [InlineData("-0.5", "-0.5")]
    [InlineData("123456789012345678901234567890", "1.2345678901234568E+29")] // too big for a decimal: the double "R" branch
    [InlineData("0.1e-5", "0.000001")]
    [InlineData("1.7976931348623157e308", "1.7976931348623157E+308")]
    public void Resolve_FormatsLargeNegativeAndScientificNumbers(string json, string expected)
    {
        var (path, issues) = ApiPathTemplate.Resolve("/n/{{v}}", Data($$"""{ "v": {{json}} }"""));

        Assert.Empty(issues);
        Assert.Equal("/n/" + Uri.EscapeDataString(expected), path);
    }

    [Fact]
    public void Resolve_AMissingOrNullValue_IsReported_NamingThePath()
    {
        var (path, issues) = ApiPathTemplate.Resolve("/a/{{c.v}}/{{d}}", Data("""{ "c": { "v": null } }"""));

        Assert.Null(path);
        Assert.Equal("fetch.value_missing,fetch.value_missing", Codes(issues));
        var text = JsonSerializer.Serialize(issues);
        Assert.Contains("c.v", text, StringComparison.Ordinal);
        Assert.Contains("\"d\"", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "c": { "v": { "x": "SECRET-VALUE" } } }""")]
    [InlineData("""{ "c": { "v": ["SECRET-VALUE"] } }""")]
    public void Resolve_AGroupOrList_IsInvalid_WithoutTheContent(string json)
    {
        var (path, issues) = ApiPathTemplate.Resolve("/a/{{c.v}}", Data(json));

        Assert.Null(path);
        Assert.Equal("fetch.value_invalid", Codes(issues));
        Assert.DoesNotContain("SECRET-VALUE", JsonSerializer.Serialize(issues), StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_NumbersAndBooleans_AreAllowed_WithTheInvariantCulture()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            var (path, issues) = ApiPathTemplate.Resolve("/n/{{a}}/{{b}}/{{c}}/{{d}}", Data("""{ "a": 42, "b": 1.5, "c": true, "d": false }"""));

            Assert.Empty(issues);
            Assert.Equal("/n/42/1.5/true/false", path);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = old;
        }
    }

    // ResolveQuery: the run-time values of the query lines (Task 4). Same scalar rules and number formatting as the path pieces, but an empty text,
    // "." and ".." are ordinary values here (the sender encodes them as one query value); the pairs keep the order of the lines and stay raw.

    [Fact]
    public void ResolveQuery_ReturnsRawPairs_InTheOrderOfTheLines()
    {
        var (items, _) = ApiFetchStep.ParseQuery("b = x.b\na = x.a\nempty = x.e\ndot = x.d\nslash = x.s");

        var (pairs, issues) = ApiFetchStep.ResolveQuery(items, Data("""{ "x": { "a": "1&2=3", "b": "../admin", "e": "", "d": "..", "s": "a/b?c#d %2e" } }"""));

        Assert.Empty(issues);
        Assert.Equal(
            [new("b", "../admin"), new("a", "1&2=3"), new("empty", ""), new("dot", ".."), new("slash", "a/b?c#d %2e")],
            pairs);
    }

    [Fact]
    public void ResolveQuery_NumbersAndBooleans_UseTheInvariantCulture_AsThePathDoes()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            var (items, _) = ApiFetchStep.ParseQuery("a = a\nb = b\nc = c\nd = d\ne = e\nf = f");
            var (pairs, issues) = ApiFetchStep.ResolveQuery(items, Data("""{ "a": 42, "b": 1.5, "c": true, "d": false, "e": 123456789012345678901234567890, "f": -0.5 }"""));

            Assert.Empty(issues);
            Assert.Equal(["42", "1.5", "true", "false", "1.2345678901234568E+29", "-0.5"], pairs.Select(p => p.Value));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = old;
        }
    }

    [Fact]
    public void ResolveQuery_AMissingOrNullValue_IsMissing_NamingTheParameterAndPath()
    {
        var (items, _) = ApiFetchStep.ParseQuery("first = c.v\nsecond = d");

        var (pairs, issues) = ApiFetchStep.ResolveQuery(items, Data("""{ "c": { "v": null } }"""));

        Assert.Empty(pairs);
        Assert.Equal("fetch.value_missing,fetch.value_missing", Codes(issues));
        var text = JsonSerializer.Serialize(issues);
        Assert.Contains("first", text, StringComparison.Ordinal);
        Assert.Contains("c.v", text, StringComparison.Ordinal);
        Assert.Contains("\"d\"", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "v": { "x": "SECRET-VALUE" } }""")]
    [InlineData("""{ "v": ["SECRET-VALUE"] }""")]
    [InlineData("""{ "v": "SECRET-VALUE\uD800" }""")] // a lone surrogate escape: GetString() would throw
    [InlineData("""{ "v": "SECRET-VALUE\uDC00x" }""")]
    public void ResolveQuery_AGroupAListOrALoneSurrogate_IsInvalid_WithoutTheContent(string json)
    {
        var (items, _) = ApiFetchStep.ParseQuery("q = v");

        var (pairs, issues) = ApiFetchStep.ResolveQuery(items, Data(json));

        Assert.Empty(pairs);
        Assert.Equal("fetch.value_invalid", Codes(issues));
        var text = JsonSerializer.Serialize(issues);
        Assert.DoesNotContain("SECRET-VALUE", text, StringComparison.Ordinal);
        Assert.Contains("\"v\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveQuery_ReportsEveryProblem_AndAnEmojiPairIsAValue()
    {
        var (items, _) = ApiFetchStep.ParseQuery("ok = e\nbad = g\ngone = m");

        var (pairs, issues) = ApiFetchStep.ResolveQuery(items, Data("""{ "e": "😀", "g": [1] }"""));

        Assert.Equal("fetch.value_invalid,fetch.value_missing", Codes(issues));
        Assert.Equal("😀", Assert.Single(pairs).Value);
    }

    [Fact]
    public void ResolveQuery_NoLines_NoPairs()
    {
        var (pairs, issues) = ApiFetchStep.ResolveQuery([], Data("{}"));

        Assert.Empty(pairs);
        Assert.Empty(issues);
    }
}
