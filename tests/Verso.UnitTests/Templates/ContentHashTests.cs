using Verso.Application;
using Verso.Application.Templates;
using Verso.Domain.Templates;

namespace Verso.UnitTests.Templates;

public sealed class ContentHashTests
{
    private static TemplateContent Content(string layout, string dataSchema = """{"type":"object"}""", string sample = "{}", PageSettings? page = null) =>
        new(page ?? PageSettings.DefaultA4, layout, dataSchema, sample);

    [Fact]
    public void CanonicalJson_IgnoresPropertyOrderAndWhitespace()
    {
        var a = CanonicalJson.Sha256Hex("""{"b":1,"a":{"y":[1,2],"x":"s"}}""");
        var b = CanonicalJson.Sha256Hex("""
            { "a": { "x": "s", "y": [ 1, 2 ] },
              "b": 1 }
            """);

        Assert.Equal(a, b);
        Assert.Matches("^[0-9a-f]{64}$", a);
    }

    [Fact]
    public void CanonicalJson_KeepsArrayOrder()
    {
        Assert.NotEqual(CanonicalJson.Sha256Hex("[1,2]"), CanonicalJson.Sha256Hex("[2,1]"));
    }

    [Fact]
    public void ContentHash_ChangesWithLayoutSchemaOrPage_ButNotSampleData()
    {
        var baseline = TemplateService.ComputeContentHash(Content("""{"layoutSchemaVersion":1}"""));

        Assert.NotEqual(baseline, TemplateService.ComputeContentHash(Content("""{"layoutSchemaVersion":1,"x":1}""")));
        Assert.NotEqual(baseline, TemplateService.ComputeContentHash(Content("""{"layoutSchemaVersion":1}""", dataSchema: """{"type":"object","required":["a"]}""")));
        Assert.NotEqual(baseline, TemplateService.ComputeContentHash(Content("""{"layoutSchemaVersion":1}""", page: PageSettings.DefaultA4 with { Orientation = "landscape" })));
        Assert.Equal(baseline, TemplateService.ComputeContentHash(Content("""{"layoutSchemaVersion":1}""", sample: """{"invoice":{"number":"X"}}""")));
    }

    [Fact]
    public void ContentHash_TreatsEqualMarginsWithDifferentScaleAsEqual()
    {
        // The database stores decimal(5,2), so 18 comes back as 18.00.
        var a = TemplateService.ComputeContentHash(Content("{}", page: PageSettings.DefaultA4 with { MarginTopMm = 18m }));
        var b = TemplateService.ComputeContentHash(Content("{}", page: PageSettings.DefaultA4 with { MarginTopMm = 18.00m }));

        Assert.Equal(a, b);
    }
}
