using System.Globalization;
using System.Text.Json;
using Verso.Application.Executions;

namespace Verso.UnitTests.Executions;

/// <summary>The Send SMS step's number and text templates (design time and the run-time resolving).</summary>
public sealed class SmsStepTests
{
    private const string Sentinel = "SENTINEL-VALUE-7731";

    private static JsonElement Data(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Codes(IReadOnlyList<object> issues) => string.Join(",", issues.Select(i => (string)i.GetType().GetProperty("code")!.GetValue(i)!));

    private static string Field(object issue) => (string)issue.GetType().GetProperty("field")!.GetValue(issue)!;

    private static string Message(object issue) => (string)issue.GetType().GetProperty("message")!.GetValue(issue)!;

    [Theory]
    [InlineData("{{customer.mobile}}")]
    [InlineData("+357 {{x}}")]
    [InlineData("{{ customer.mobile }}")]
    [InlineData("+357 99 000000")]
    [InlineData("00357-99.000(000)")]
    public void ValidateTo_Accepts(string template) => Assert.Null(SmsTemplates.ValidateTo(template));

    [Theory]
    [InlineData("{{ 1bad }}", "1bad")]
    [InlineData("{{a..b}}", "a..b")]
    [InlineData("{{a", "no closing")]
    [InlineData("a}}", "no opening")]
    [InlineData("{{}}", "valid data path")]
    [InlineData("", "required")]
    [InlineData("   ", "required")]
    [InlineData(null, "required")]
    [InlineData("call me", "digits")]
    public void ValidateTo_Refuses(string? template, string mentions)
    {
        var problem = SmsTemplates.ValidateTo(template);

        Assert.NotNull(problem);
        Assert.Contains(mentions, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateTo_RefusesMoreThan100Characters_AndAcceptsExactly100()
    {
        Assert.Contains("at most 100", SmsTemplates.ValidateTo(new string('1', 101)), StringComparison.Ordinal);
        Assert.Null(SmsTemplates.ValidateTo("{{" + new string('a', 96) + "}}"));
    }

    [Fact]
    public void ValidateTo_NamesOnlyAShortPieceOfAnOverlongPath()
    {
        var problem = SmsTemplates.ValidateTo("{{" + new string('9', 60) + ".}}");

        Assert.NotNull(problem);
        Assert.DoesNotContain(new string('9', 11), problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Dear {{customer.name}}, your statement is ready.")]
    [InlineData("{{a}}")]
    [InlineData("Line one\nline two {{x}}")]
    [InlineData("Line one\r\nline two")]
    [InlineData("Brace { alone } is text")]
    [InlineData("Grüße 😀 €")]
    public void ValidateText_Accepts(string template) => Assert.Null(SmsTemplates.ValidateText(template));

    [Theory]
    [InlineData("Hi {{ 1bad }}", "1bad")]
    [InlineData("Hi {{a..b}}", "a..b")]
    [InlineData("Hi {{a", "no closing")]
    [InlineData("Hi a}}", "no opening")]
    [InlineData("", "required")]
    [InlineData("  \n ", "required")]
    [InlineData(null, "required")]
    [InlineData("Hi\tthere", "can't be sent")]
    [InlineData("Hi\rthere", "can't be sent")]
    [InlineData("Hi\u0000there", "can't be sent")]
    [InlineData("Hi ‮there", "can't be sent")]
    [InlineData("Hi ​there", "can't be sent")]
    public void ValidateText_Refuses(string? template, string mentions)
    {
        var problem = SmsTemplates.ValidateText(template);

        Assert.NotNull(problem);
        Assert.Contains(mentions, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateText_RefusesALoneSurrogate_InTheFixedPart()
    {
        // Built in code: an attribute argument can't carry a lone surrogate.
        foreach (var template in new[] { "Hi " + '\uD800' + " there", "Hi there " + '\uDC00', "Hi {{a}} " + '\uD800' })
        {
            var problem = SmsTemplates.ValidateText(template);
            Assert.NotNull(problem);
            Assert.Contains("can't be sent", problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Resolve_Sentinel_ControlAndFormatCharactersInATextValue_NeverEchoTheValue()
    {
        foreach (var bad in new[] { "‮", "\t" })
        {
            var (_, issues) = SmsTemplates.Resolve("Hi {{c.v}}", Data($$"""{ "c": { "v": "{{Sentinel}}{{bad.Replace("\t", "\\t")}}" } }"""), isText: true);

            var issue = Assert.Single(issues);
            Assert.Equal("sms.text_invalid", Codes(issues));
            Assert.DoesNotContain(Sentinel, Message(issue), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ValidateText_Refuses481Characters_AndAccepts480()
    {
        Assert.Null(SmsTemplates.ValidateText(new string('a', 480)));
        Assert.Contains("at most 480", SmsTemplates.ValidateText(new string('a', 481)), StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateText_AcceptsAZwjEmoji_ButNotAnInvisibleTagCharacter()
    {
        Assert.Null(SmsTemplates.ValidateText("👨‍👩‍👧 hello"));
        Assert.NotNull(SmsTemplates.ValidateText("hi \U000E0041 there"));
    }

    [Fact]
    public void Resolve_FillsPiecesAsPlainText_NotUrlEncoded()
    {
        var (text, issues) = SmsTemplates.Resolve("Dear {{c.name}} & co, {{ c.n }} = {{c.ok}}/{{c.no}} ({{c.pi}}) €{{c.big}}", Data("""{ "c": { "name": "Zoë Müller/ö?", "n": 42, "ok": true, "no": false, "pi": 3.14, "big": 1000000000000 } }"""), isText: true);

        Assert.Empty(issues);
        Assert.Equal("Dear Zoë Müller/ö? & co, 42 = true/false (3.14) €1000000000000", text);
    }

    [Fact]
    public void Resolve_NumbersUseTheInvariantCulture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var (text, issues) = SmsTemplates.Resolve("{{a}} {{b}} {{c}}", Data("""{ "a": 3.5, "b": 1234567.25, "c": 0.5 }"""), isText: true);

            Assert.Empty(issues);
            Assert.Equal("3.5 1234567.25 0.5", text);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Resolve_TheNumberTemplate_ReturnsTheTrimmedValue()
    {
        var (number, issues) = SmsTemplates.Resolve("+357 {{c.mobile}}", Data("""{ "c": { "mobile": "99 000000 " } }"""), isText: false);
        Assert.Empty(issues);
        Assert.Equal("+357 99 000000", number);

        var (fixedNumber, _) = SmsTemplates.Resolve(" +357 99 000000 ", Data("{}"), isText: false);
        Assert.Equal("+357 99 000000", fixedNumber);

        var (numeric, _) = SmsTemplates.Resolve("{{m}}", Data("""{ "m": 35799000000 }"""), isText: false);
        Assert.Equal("35799000000", numeric);
    }

    [Theory]
    [InlineData("""{ "c": {} }""")]
    [InlineData("""{ "c": { "v": null } }""")]
    [InlineData("""{ }""")]
    [InlineData("""{ "c": "text" }""")]
    public void Resolve_AMissingOrNullValue_IsReported_OnTheDataPath(string json)
    {
        foreach (var isText in new[] { true, false })
        {
            var (value, issues) = SmsTemplates.Resolve("Hi {{c.v}}", Data(json), isText);

            Assert.Null(value);
            var issue = Assert.Single(issues);
            Assert.Equal("sms.value_missing", Codes(issues));
            Assert.Equal("c.v", Field(issue));
            Assert.Contains("'c.v'", Message(issue), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Resolve_EveryMissingPiece_IsReported()
    {
        var (_, issues) = SmsTemplates.Resolve("{{a}} {{b}} {{c}}", Data("""{ "b": "x" }"""), isText: true);

        Assert.Equal("sms.value_missing,sms.value_missing", Codes(issues));
        Assert.Equal(["a", "c"], issues.Select(Field));
    }

    [Theory]
    [InlineData("""{ "v": { "x": "SENTINEL-VALUE-7731" } }""")]
    [InlineData("""{ "v": ["SENTINEL-VALUE-7731"] }""")]
    public void Resolve_AGroupOrAList_IsInvalid(string json)
    {
        var (value, issues) = SmsTemplates.Resolve("Hi {{v}}", Data(json), isText: true);

        Assert.Null(value);
        Assert.Equal("sms.value_invalid", Codes(issues));
        Assert.Equal("v", Field(Assert.Single(issues)));
    }

    [Theory]
    [InlineData("""{ "v": "SENTINEL-VALUE-7731\uD800" }""")] // a lone surrogate escape: GetString() throws
    [InlineData("""{ "v": "SENTINEL-VALUE-7731\uDC00x" }""")]
    public void Resolve_AStringThatCantBeRead_IsTextInvalid_ForBothTemplates(string json)
    {
        foreach (var isText in new[] { true, false })
        {
            var (value, issues) = SmsTemplates.Resolve("{{v}}", Data(json), isText);

            Assert.Null(value);
            Assert.Equal("sms.text_invalid", Codes(issues));
            Assert.Equal("v", Field(Assert.Single(issues)));
        }
    }

    [Theory]
    [InlineData("""{ "v": "a\u0000b" }""")]
    [InlineData("""{ "v": "a\tb" }""")]
    [InlineData("""{ "v": "a\rb" }""")]
    [InlineData("""{ "v": "a‮b" }""")]
    [InlineData("""{ "v": "a​b" }""")]
    public void Resolve_AControlOrFormatCharacterInAValue_IsTextInvalid_OnThatPiece(string json)
    {
        var (value, issues) = SmsTemplates.Resolve("Hi {{v}}", Data(json), isText: true);

        Assert.Null(value);
        Assert.Equal("sms.text_invalid", Codes(issues));
        Assert.Equal("v", Field(Assert.Single(issues)));
    }

    [Fact]
    public void Resolve_ALineBreakAndAZwjEmojiInAValue_AreAccepted()
    {
        var (text, issues) = SmsTemplates.Resolve("{{v}}", Data("""{ "v": "line1\r\nline2 👨‍👩‍👧" }"""), isText: true);

        Assert.Empty(issues);
        Assert.Equal("line1\nline2 👨‍👩‍👧", text);
    }

    [Fact]
    public void Resolve_AResolvedTextOver480_IsTextInvalid_OnTheTextField()
    {
        var (value, issues) = SmsTemplates.Resolve("Hi {{v}}", Data($$"""{ "v": "{{new string('a', 480)}}" }"""), isText: true);

        Assert.Null(value);
        var issue = Assert.Single(issues);
        Assert.Equal("sms.text_invalid", Codes(issues));
        Assert.Equal("text", Field(issue));
        Assert.Contains("480", Message(issue), StringComparison.Ordinal);

        var (ok, none) = SmsTemplates.Resolve("{{v}}", Data($$"""{ "v": "{{new string('a', 480)}}" }"""), isText: true);
        Assert.Empty(none);
        Assert.Equal(480, ok!.Length);
    }

    [Theory]
    [InlineData("""{ "v": "" }""")]
    [InlineData("""{ "v": "  \n " }""")]
    public void Resolve_AnEmptyText_IsTextInvalid(string json)
    {
        var (value, issues) = SmsTemplates.Resolve("{{v}}", Data(json), isText: true);

        Assert.Null(value);
        var issue = Assert.Single(issues);
        Assert.Equal("sms.text_invalid", Codes(issues));
        Assert.Equal("text", Field(issue));
        Assert.Contains("empty", Message(issue), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "v": "" }""")]
    [InlineData("""{ "v": "   " }""")]
    public void Resolve_AnEmptyNumber_IsRecipientInvalid(string json)
    {
        var (value, issues) = SmsTemplates.Resolve("{{v}}", Data(json), isText: false);

        Assert.Null(value);
        var issue = Assert.Single(issues);
        Assert.Equal("sms.recipient_invalid", Codes(issues));
        Assert.Equal("to", Field(issue));
    }

    [Fact]
    public void Resolve_TheNumberTemplate_DoesNotApplyTheTextRules_TheEngineChecksTheNumber()
    {
        // 600 characters and a tab are the engine's business for a number (TryNormaliseRecipient), not a text problem.
        var (value, issues) = SmsTemplates.Resolve("{{v}}", Data($$"""{ "v": "{{new string('1', 600)}}" }"""), isText: false);

        Assert.Empty(issues);
        Assert.Equal(600, value!.Length);
    }

    [Fact]
    public void Resolve_AnUnclosedPiece_IsReported_AtRunTimeToo()
    {
        var (_, textIssues) = SmsTemplates.Resolve("Hi {{a", Data("{}"), isText: true);
        var (_, toIssues) = SmsTemplates.Resolve("{{a", Data("{}"), isText: false);

        Assert.Equal("sms.text_invalid", Codes(textIssues));
        Assert.Equal("sms.recipient_invalid", Codes(toIssues));
    }

    [Theory]
    [InlineData("""{ "c": { "v": { "x": "SENTINEL-VALUE-7731" } } }""")]
    [InlineData("""{ "c": { "v": ["SENTINEL-VALUE-7731"] } }""")]
    [InlineData("""{ "c": { "v": "SENTINEL-VALUE-7731\uD800" } }""")]
    public void Resolve_IssuesNeverContainAValue_OfAnyKind(string json)
    {
        foreach (var isText in new[] { true, false })
        {
            var (_, issues) = SmsTemplates.Resolve("Hi {{c.v}}", Data(json), isText);

            Assert.NotEmpty(issues);
            foreach (var issue in issues)
            {
                Assert.DoesNotContain(Sentinel, Message(issue), StringComparison.Ordinal);
                Assert.DoesNotContain(Sentinel, Field(issue), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Resolve_TheOverlongTextIssue_NeverContainsTheValue()
    {
        var (_, issues) = SmsTemplates.Resolve("{{v}}", Data($$"""{ "v": "{{Sentinel}}{{new string('a', 500)}}" }"""), isText: true);

        var issue = Assert.Single(issues);
        Assert.DoesNotContain(Sentinel, Message(issue), StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_AFixedTemplateWithoutPieces_IsReturnedAsIs()
    {
        var (text, issues) = SmsTemplates.Resolve("Your statement is ready.", Data("{}"), isText: true);

        Assert.Empty(issues);
        Assert.Equal("Your statement is ready.", text);
    }
}
