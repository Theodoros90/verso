using Verso.Rendering;
using Verso.Rendering.Email;
using static Verso.UnitTests.Rendering.TestFiles;

namespace Verso.UnitTests.Rendering;

public sealed class EmailRendererTests
{
    private static readonly System.Text.Json.JsonElement Data = Json("""
        { "invoice": { "number": "INV-1", "due": "2026-12-01", "total": 1234.5 }, "customer": { "name": "Kestrel <Logistics> & Co", "evil": "javascript:alert(1)" } }
        """);

    private static EmailSpec Spec(
        string subject = "Invoice {{invoice.number}}", string body = "Dear {{customer.name}},\n\nTotal {{invoice.total|money}}.",
        string label = "", string url = "", string footer = "", string preheader = "", bool dark = false, EmailLayout layout = EmailLayout.Invoice) =>
        new(layout, subject, preheader, body, label, url, footer, dark);

    [Fact]
    public void ResolvesTokensAndFilters_IntoSubjectHtmlAndPlainText()
    {
        var result = EmailRenderer.Render(Spec(), Data);

        Assert.Empty(result.Issues);
        Assert.Equal("Invoice INV-1", result.Subject);
        Assert.Contains("€1,234.50", result.Html, StringComparison.Ordinal);
        Assert.StartsWith("Invoice INV-1\n\nDear Kestrel <Logistics> & Co,", result.PlainText, StringComparison.Ordinal);
        Assert.Contains("Total €1,234.50.", result.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public void EncodesBoundValuesOnce_AndNeverEmitsScriptsOrRemoteResources()
    {
        var result = EmailRenderer.Render(Spec(label: "Pay", url: "https://pay.example.test/?a=1&b=2"), Data);

        Assert.Contains("Kestrel &lt;Logistics&gt; &amp; Co", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<Logistics>", result.Html, StringComparison.Ordinal);
        Assert.Contains("a=1&amp;b=2", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("url(", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void TextInTheStepThatLooksLikeMarkup_IsShownAsText()
    {
        var result = EmailRenderer.Render(Spec(body: "<b onclick=alert(1)>hi</b>"), Data);

        Assert.Contains("&lt;b onclick=alert(1)&gt;hi&lt;/b&gt;", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b onclick", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingValue_IsAnError_WithTheFieldAndPath()
    {
        var result = EmailRenderer.Render(Spec(body: "Hello {{customer.nickname}}"), Data);

        var issue = Assert.Single(result.Issues);
        Assert.Equal((IssueSeverity.Error, "binding.missing", "body", "customer.nickname"), (issue.Severity, issue.Code, issue.ElementId, issue.Path));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("{{customer.evil}}")]
    [InlineData("//evil.example.test")]
    [InlineData("data:text/html;base64,AAAA")]
    [InlineData("INV-1")]
    public void ButtonLinkThatIsNotHttpHttpsOrMailto_IsAnError_AndNoButtonIsWritten(string url)
    {
        var result = EmailRenderer.Render(Spec(label: "Pay", url: url), Data);

        Assert.Contains(result.Issues, i => i is { Severity: IssueSeverity.Error, Code: "email.url_invalid" });
        Assert.DoesNotContain("<a href", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void MailtoLink_IsAllowed()
    {
        var result = EmailRenderer.Render(Spec(label: "Write to us", url: "mailto:billing@example.test"), Data);

        Assert.Empty(result.Issues);
        Assert.Contains("href=\"mailto:billing@example.test\"", result.Html, StringComparison.Ordinal);
        Assert.Contains("Write to us: mailto:billing@example.test", result.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public void SubjectIsOneLine_SoDataCannotStartAnotherHeader()
    {
        var data = Json("""{ "x": "Hello\r\nBcc: someone@example.test" }""");

        var result = EmailRenderer.Render(Spec(subject: "{{x}}"), data);

        Assert.DoesNotContain('\n', result.Subject);
        Assert.DoesNotContain('\r', result.Subject);
        Assert.Equal("Hello Bcc: someone@example.test", result.Subject);
    }

    [Fact]
    public void EmptySubjectOrBody_AreErrors()
    {
        var result = EmailRenderer.Render(Spec(subject: "  ", body: ""), Data);

        Assert.Contains(result.Issues, i => i.Code == "email.subject_empty");
        Assert.Contains(result.Issues, i => i.Code == "email.body_empty");
    }

    [Fact]
    public void DarkMode_AddsColourSchemeAndMediaQuery_OnlyWhenOn()
    {
        var on = EmailRenderer.Render(Spec(dark: true), Data).Html;
        var off = EmailRenderer.Render(Spec(dark: false), Data).Html;

        Assert.Contains("prefers-color-scheme:dark", on, StringComparison.Ordinal);
        Assert.Contains("name=\"color-scheme\"", on, StringComparison.Ordinal);
        Assert.DoesNotContain("prefers-color-scheme", off, StringComparison.Ordinal);
        Assert.Contains("max-width:620px", off, StringComparison.Ordinal); // responsive either way
    }

    [Fact]
    public void LayoutPresets_ChangeTheHeadingLabel()
    {
        Assert.Contains(">Invoice<", EmailRenderer.Render(Spec(layout: EmailLayout.Invoice), Data).Html, StringComparison.Ordinal);
        Assert.Contains(">Statement<", EmailRenderer.Render(Spec(layout: EmailLayout.Statement), Data).Html, StringComparison.Ordinal);
        Assert.Contains(">Payment reminder<", EmailRenderer.Render(Spec(layout: EmailLayout.Reminder), Data).Html, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankLinesMakeParagraphs_SingleLinesBecomeBreaks_PreheaderIsHidden()
    {
        var result = EmailRenderer.Render(Spec(body: "One\ntwo\n\nThree", preheader: "Short preview"), Data);

        Assert.Contains(">One<br>two</p>", result.Html, StringComparison.Ordinal);
        Assert.Contains(">Three</p>", result.Html, StringComparison.Ordinal);
        Assert.Contains("display:none", result.Html, StringComparison.Ordinal);
        Assert.Contains("Short preview", result.Html, StringComparison.Ordinal);
    }
}
