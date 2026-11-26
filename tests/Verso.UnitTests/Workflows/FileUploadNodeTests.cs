using System.Text.Json;
using System.Text.Json.Nodes;
using Verso.Application.Workflows;

namespace Verso.UnitTests.Workflows;

public sealed class FileUploadNodeTests
{
    private static readonly Guid TemplateId = Guid.Parse("01a1c000-0000-7000-8000-000000000001");

    private static WorkflowValidationDto Validate(string config)
    {
        var definition = GraphValidatorTests.InvoiceStandard(TemplateId);
        definition["nodes"]![0] = JsonNode.Parse($$"""
            { "id": "trigger", "type": "file-upload", "title": "File upload", "position": { "x": 0, "y": 80 }, "config": {{config}} }
            """);
        using var doc = JsonDocument.Parse(definition.ToJsonString());
        var (parsed, errors) = WorkflowDefinitionParser.Parse(doc.RootElement);
        Assert.True(parsed is not null, string.Join("; ", errors.Select(e => $"{e.Key}: {e.Value[0]}")));
        return GraphValidator.Validate(parsed!, new Dictionary<Guid, TemplateRef> { [TemplateId] = new(TemplateId, "Commercial invoice", new HashSet<int> { 1 }) });
    }

    [Fact]
    public void Default_settings_are_valid_and_description_is_not_simulated()
    {
        var type = NodeCatalog.Find(NodeCatalog.FileUpload)!;
        var defaults = type.Defaults;
        Assert.Equal("PDF, XML, CSV", defaults.GetProperty("accept").GetString());
        Assert.Equal("25mb", defaults.GetProperty("maxSize").GetString());
        Assert.Equal("staff", defaults.GetProperty("who").GetString());
        Assert.DoesNotContain(Validate(defaults.GetRawText()).Issues, i => i.Severity == GraphValidator.Error);
        Assert.DoesNotContain("Simulated", type.Description);
        Assert.StartsWith("A signed-in operator uploads one file in Verso.", type.Description);
    }

    [Theory]
    [InlineData("portal-customers")]
    [InlineData("anyone-with-link")]
    public void Who_other_than_staff_is_rejected(string who)
    {
        var result = Validate($$"""{ "accept": "PDF", "maxSize": "25mb", "who": "{{who}}" }""");
        Assert.Contains(result.Issues, i => i.NodeId == "trigger" && i.Field == "who" && i.Severity == GraphValidator.Error);
    }

    [Fact]
    public void Size_100mb_is_rejected()
    {
        var result = Validate("""{ "accept": "PDF", "maxSize": "100mb", "who": "staff" }""");
        Assert.Contains(result.Issues, i => i.NodeId == "trigger" && i.Field == "maxSize");
    }

    [Fact]
    public void Unknown_accept_type_is_rejected_on_accept()
    {
        var result = Validate("""{ "accept": "DOCX", "maxSize": "25mb", "who": "staff" }""");
        Assert.Contains(result.Issues, i => i.NodeId == "trigger" && i.Field == "accept" && i.Severity == GraphValidator.Error);
    }

    [Fact]
    public void Blank_accept_is_reported_once()
    {
        var result = Validate("""{ "accept": " ", "maxSize": "25mb", "who": "staff" }""");
        Assert.Single(result.Issues, i => i.NodeId == "trigger" && i.Field == "accept" && i.Severity == GraphValidator.Error);
    }
}
