using System.Text.Json;
using System.Text.Json.Nodes;
using Verso.Application.Workflows;

namespace Verso.UnitTests.Workflows;

/// <summary>Design-time rules of the folder-input trigger: approved locations only, subfolders inside them, no rescan loops.</summary>
public sealed class FolderInputValidationTests
{
    private static readonly Guid TemplateId = Guid.Parse("01a1c000-0000-7000-8000-000000000001");

    private static readonly Dictionary<Guid, TemplateRef> Templates = new()
    {
        [TemplateId] = new TemplateRef(TemplateId, "Commercial invoice", new HashSet<int> { 1 }),
    };

    private static readonly Dictionary<string, FolderLocationRef> Locations = new()
    {
        ["ap-inbound"] = new("ap-inbound", "AP inbound share", Input: true, Output: true, Enabled: true),
        ["ap-archive"] = new("ap-archive", "AP archive share", Input: false, Output: true, Enabled: true),
        ["old-share"] = new("old-share", "Old share", Input: true, Output: true, Enabled: false),
    };

    internal static JsonObject Config(
        string source = "ap-inbound", string sourceSubfolder = "incoming", bool include = false,
        string processed = "ap-archive", string processedSubfolder = "processed", string failed = "ap-archive", string failedSubfolder = "failed") => new()
        {
            ["sourceLocation"] = source, ["sourceSubfolder"] = sourceSubfolder, ["pattern"] = "*.json", ["includeSubfolders"] = include,
            ["interval"] = "every-minute", ["processedLocation"] = processed, ["processedSubfolder"] = processedSubfolder,
            ["failedLocation"] = failed, ["failedSubfolder"] = failedSubfolder,
        };

    private static WorkflowValidationDto Validate(JsonObject config)
    {
        var definition = GraphValidatorTests.InvoiceStandard(TemplateId);
        definition["nodes"]![0] = new JsonObject
        {
            ["id"] = "trigger", ["type"] = "folder-input", ["title"] = "AP folder", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 80 },
            ["config"] = config,
        };
        using var doc = JsonDocument.Parse(definition.ToJsonString());
        var (parsed, errors) = WorkflowDefinitionParser.Parse(doc.RootElement);
        Assert.True(parsed is not null, string.Join("; ", errors.Select(e => $"{e.Key}: {e.Value[0]}")));
        return GraphValidator.Validate(parsed!, Templates, Locations);
    }

    private static List<string> TriggerErrors(WorkflowValidationDto v) =>
        [.. v.Issues.Where(i => i.NodeId == "trigger" && i.Severity == GraphValidator.Error).Select(i => $"{i.Field}:{i.Code}")];

    [Fact]
    public void ApprovedLocations_AreValid()
    {
        var result = Validate(Config());
        Assert.Empty(TriggerErrors(result));
        Assert.True(result.Valid);
    }

    [Fact]
    public void Defaults_NeedLocationsChosen()
    {
        var defaults = JsonNode.Parse(NodeCatalog.Find(NodeCatalog.FolderInput)!.Defaults.GetRawText())!.AsObject();
        Assert.Equal(
            ["sourceLocation:config.required", "processedLocation:config.required", "failedLocation:config.required"],
            TriggerErrors(Validate(defaults)));
    }

    [Fact]
    public void UnknownLocation_IsAnError() =>
        Assert.Contains("sourceLocation:folder.location_unknown", TriggerErrors(Validate(Config(source: "c-drive"))));

    [Fact]
    public void ServerPathsAreNotLocations() =>
        Assert.Contains("sourceLocation:config.invalid", TriggerErrors(Validate(Config(source: @"C:\Windows"))));

    [Fact]
    public void DisabledLocation_IsAnError() =>
        Assert.Contains("sourceLocation:folder.location_disabled", TriggerErrors(Validate(Config(source: "old-share"))));

    [Fact]
    public void OutputOnlyLocation_CantBeASource() =>
        Assert.Contains("sourceLocation:folder.location_purpose", TriggerErrors(Validate(Config(source: "ap-archive"))));

    [Theory]
    [InlineData("../secrets")]
    [InlineData("/etc")]          // absolute-looking: refused, subfolders are written relative
    [InlineData(".verso-inflight")]
    [InlineData(@"in\..\..")]
    [InlineData(@"\\fileserver\share")]
    public void Subfolders_StayInsideTheLocation(string subfolder) =>
        Assert.Contains(TriggerErrors(Validate(Config(sourceSubfolder: subfolder))), e => e.StartsWith("sourceSubfolder:", StringComparison.Ordinal));

    [Fact]
    public void ProcessedFilesInTheScannedFolder_AreAnError() =>
        Assert.Contains("processedSubfolder:folder.destination_scanned",
            TriggerErrors(Validate(Config(processed: "ap-inbound", processedSubfolder: "incoming"))));

    [Fact]
    public void FailedFilesBelowTheScannedFolder_AreAnError_WhenSubfoldersAreIncluded()
    {
        Assert.Contains("failedSubfolder:folder.destination_scanned",
            TriggerErrors(Validate(Config(include: true, failed: "ap-inbound", failedSubfolder: "incoming/failed"))));
        Assert.Empty(TriggerErrors(Validate(Config(include: false, failed: "ap-inbound", failedSubfolder: "incoming/failed"))));
    }

    [Fact]
    public void IncludeSubfolders_MustBeABoolean()
    {
        var config = Config();
        config["includeSubfolders"] = "yes";
        Assert.Contains("includeSubfolders:config.invalid", TriggerErrors(Validate(config)));
    }

    [Fact]
    public void NoCredentialSettings_Exist()
    {
        var config = Config();
        config["password"] = "secret";
        Assert.Contains("password:config.unknown", TriggerErrors(Validate(config)));
    }
}
