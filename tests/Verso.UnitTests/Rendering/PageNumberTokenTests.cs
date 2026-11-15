using System.Text.Json;
using Verso.Rendering;
using Verso.Rendering.Html;
using Verso.Rendering.Layout;
using static Verso.UnitTests.Rendering.TestFiles;

namespace Verso.UnitTests.Rendering;

/// <summary><c>{{page}}</c> and <c>{{pages}}</c> in header and footer text (docs/layout-model.md).</summary>
public sealed class PageNumberTokenTests
{
    private static TemplateLayout Layout(string footerText, string bodyText = "Body") => ParseValid($$"""
        { "layoutSchemaVersion": 1,
          "body": { "blocks": [ { "id": "b", "type": "text", "props": { "text": "{{bodyText}}" } } ] },
          "footer": { "heightMm": 10, "elements": [ { "id": "f", "type": "text", "xMm": 0, "yMm": 0, "widthMm": 100, "heightMm": 6, "props": { "text": "{{footerText}}" } } ] } }
        """);

    private static RenderResult Render(TemplateLayout layout, RenderMode mode = RenderMode.Final) =>
        HtmlRenderer.Render(layout, A4, JsonDocument.Parse("{}").RootElement, mode, "T");

    [Fact]
    public void PrintTemplate_HandsTheNumbersToChromium_AndThePreviewSheetShowsPageOneOfOne()
    {
        var result = Render(Layout("Page {{page}} of {{pages}}"));

        Assert.Contains("Page <span class=\"pageNumber\"></span> of <span class=\"totalPages\"></span>", result.Print.FooterTemplate, StringComparison.Ordinal);
        Assert.Contains("Page 1 of 1", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Issues, i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void InTheBody_TheyAreOrdinaryDataPaths()
    {
        var result = Render(Layout("x", bodyText: "Page {{page}}"));

        Assert.Contains(result.Issues, i => i is { Code: "binding.missing", ElementId: "b", Path: "page" });
        Assert.DoesNotContain("pageNumber", result.Print.BodyHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void WithAFilter_OrOtherNames_TheyAreDataPaths_EvenInTheFooter()
    {
        var withFilter = Render(Layout("{{page|upper}}"));
        var other = Render(Layout("{{pageCount}}"));

        Assert.Contains(withFilter.Issues, i => i is { Code: "binding.missing", ElementId: "f", Path: "page" });
        Assert.Contains(other.Issues, i => i is { Code: "binding.missing", ElementId: "f", Path: "pageCount" });
        Assert.DoesNotContain("pageNumber", withFilter.Print.FooterTemplate, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_ShowsPageNumbersAsOne_NotAsMissingValues()
    {
        var result = Render(Layout("{{page}}/{{pages}}"), RenderMode.Preview);

        Assert.Contains("1/1", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("{{page}}", result.Html, StringComparison.Ordinal);
    }
}
