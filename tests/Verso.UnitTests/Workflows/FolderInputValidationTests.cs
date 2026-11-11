using System.Text.Json;
using System.Text.Json.Nodes;
using Verso.Application.Ingestion;
using Verso.Application.Workflows;

namespace Verso.UnitTests.Workflows;

/// <summary>
/// Design-time rules of the folder-input trigger: three typed absolute paths whose shape is checked here (the Worker
/// checks them where they live), no rescan loops, and workflows saved with approved locations still validate.
/// </summary>
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

    internal static JsonObject Typed(
        string source = @"D:\Invoices\in", string processed = @"D:\Invoices\done", string failed = @"D:\Invoices\failed", bool include = false) => new()
        {
            ["sourcePath"] = source, ["pattern"] = "*.json", ["includeSubfolders"] = include, ["interval"] = "every-minute",
            ["processedPath"] = processed, ["failedPath"] = failed,
        };

    /// <summary>A workflow saved when folders were chosen from approved locations.</summary>
    internal static JsonObject Legacy(
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

    // ----- typed paths -----

    [Theory]
    [InlineData(@"D:\Invoices\in")]
    [InlineData("D:/Invoices/in")]
    [InlineData(@"\\fileserver\verso\ap-inbound")]
    [InlineData("/mnt/verso/ap-inbound")]
    public void TypedPaths_AreValid(string source)
    {
        var result = Validate(Typed(source: source));
        Assert.Empty(TriggerErrors(result));
        Assert.True(result.Valid);
    }

    [Fact]
    public void Defaults_NeedThePathsTyped() =>
        Assert.Equal(
            ["sourcePath:config.required", "processedPath:config.required", "failedPath:config.required"],
            TriggerErrors(Validate(JsonNode.Parse(NodeCatalog.Find(NodeCatalog.FolderInput)!.Defaults.GetRawText())!.AsObject())));

    [Theory]
    [InlineData("inbound")]                    // relative
    [InlineData(@"..\inbound")]
    [InlineData(@"D:\Invoices\..\Windows")]    // traversal
    [InlineData(@"D:\Invoices\*")]             // wildcard
    [InlineData("D:inbound")]                  // drive-relative
    public void TypedPaths_MustBeAbsoluteAndPlain(string source) =>
        Assert.Contains("sourcePath:folder.path_invalid", TriggerErrors(Validate(Typed(source: source))));

    [Theory]
    [InlineData("processedPath")]
    [InlineData("failedPath")]
    public void EveryTypedPath_IsChecked(string field)
    {
        var config = Typed();
        config[field] = "relative";
        Assert.Contains($"{field}:folder.path_invalid", TriggerErrors(Validate(config)));
    }

    [Fact]
    public void ProcessedFilesInTheScannedFolder_AreAnError() =>
        Assert.Contains("processedPath:folder.destination_scanned",
            TriggerErrors(Validate(Typed(processed: @"d:\invoices\IN\")))); // the same folder, typed differently

    [Fact]
    public void FailedFilesBelowTheScannedFolder_AreAnError_OnlyWhenSubfoldersAreIncluded()
    {
        Assert.Contains("failedPath:folder.destination_scanned",
            TriggerErrors(Validate(Typed(include: true, failed: @"D:\Invoices\in\failed"))));
        Assert.Empty(TriggerErrors(Validate(Typed(include: false, failed: @"D:\Invoices\in\failed"))));
        Assert.Empty(TriggerErrors(Validate(Typed(include: true, failed: @"D:\Invoices\in-failed")))); // only a shared name prefix
    }

    [Fact]
    public void IncludeSubfolders_MustBeABoolean()
    {
        var config = Typed();
        config["includeSubfolders"] = "yes";
        Assert.Contains("includeSubfolders:config.invalid", TriggerErrors(Validate(config)));
    }

    [Fact]
    public void NoCredentialSettings_Exist()
    {
        var config = Typed();
        config["password"] = "secret";
        Assert.Contains("password:config.unknown", TriggerErrors(Validate(config)));
    }

    [Fact]
    public void ATypedPathWins_OverALeftoverLocation()
    {
        var config = Legacy(source: "c-drive"); // an unknown location...
        config["sourcePath"] = @"D:\Invoices\in"; // ...replaced by a typed path
        Assert.DoesNotContain(TriggerErrors(Validate(config)), e => e.StartsWith("sourceLocation", StringComparison.Ordinal));
    }

    // ----- workflows saved with approved locations -----

    [Fact]
    public void ApprovedLocations_StillValidate()
    {
        var result = Validate(Legacy());
        Assert.Empty(TriggerErrors(result));
        Assert.True(result.Valid);
    }

    [Fact]
    public void UnknownLocation_IsAnError() =>
        Assert.Contains("sourceLocation:folder.location_unknown", TriggerErrors(Validate(Legacy(source: "c-drive"))));

    [Fact]
    public void ServerPathsAreNotLocationKeys() =>
        Assert.Contains("sourceLocation:config.invalid", TriggerErrors(Validate(Legacy(source: @"C:\Windows"))));

    [Fact]
    public void DisabledLocation_IsAnError() =>
        Assert.Contains("sourceLocation:folder.location_disabled", TriggerErrors(Validate(Legacy(source: "old-share"))));

    [Fact]
    public void OutputOnlyLocation_CantBeASource() =>
        Assert.Contains("sourceLocation:folder.location_purpose", TriggerErrors(Validate(Legacy(source: "ap-archive"))));

    [Theory]
    [InlineData("../secrets")]
    [InlineData("/etc")]
    [InlineData(".verso-inflight")]
    [InlineData(@"in\..\..")]
    [InlineData(@"\\fileserver\share")]
    public void LocationSubfolders_StayInsideTheLocation(string subfolder) =>
        Assert.Contains(TriggerErrors(Validate(Legacy(sourceSubfolder: subfolder))), e => e.StartsWith("sourceSubfolder:", StringComparison.Ordinal));

    [Fact]
    public void LocationProcessedFilesInTheScannedFolder_AreAnError() =>
        Assert.Contains("processedSubfolder:folder.destination_scanned",
            TriggerErrors(Validate(Legacy(processed: "ap-inbound", processedSubfolder: "incoming"))));
}
