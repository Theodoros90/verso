using System.Text.Json;
using Verso.Application.Ingestion;

namespace Verso.UnitTests.Ingestion;

public sealed class FolderPathsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("incoming")]
    [InlineData("invoices/incoming")]
    [InlineData("AP 2026/in-bound_1")]
    [InlineData("a.b/c")]
    public void Subfolder_Valid(string subfolder) => Assert.Null(FolderPaths.SubfolderProblem(subfolder));

    [Theory]
    [InlineData("..")]                         // parent
    [InlineData("in/../../etc")]               // traversal in the middle
    [InlineData("./in")]                       // current folder
    [InlineData(".verso-inflight")]            // Verso's working folder (leading dot)
    [InlineData("in/.hidden")]
    [InlineData(@"in\out")]                    // backslash: use '/'
    [InlineData("C:/Windows")]                 // drive
    [InlineData("in//out")]                    // empty segment
    [InlineData("in /out")]                    // trailing space (Windows trims it)
    [InlineData("in./out")]                    // trailing dot (Windows trims it)
    [InlineData("nul")]                        // device name
    [InlineData("in/COM1.txt")]                // device name with an extension
    [InlineData("a/b/c/d/e/f/g/h/i")]          // too deep
    [InlineData("in*")]                        // wildcard
    [InlineData("in:stream")]                  // alternate data stream
    public void Subfolder_Invalid(string subfolder) => Assert.NotNull(FolderPaths.SubfolderProblem(FolderPaths.NormalizeSubfolder(subfolder)));

    [Fact]
    public void Subfolder_LeadingAndTrailingSlashesAreTrimmed() => Assert.Equal("in/out", FolderPaths.NormalizeSubfolder(" /in/out/ "));

    [Theory]
    [InlineData("*.json", true)]
    [InlineData("INV-*.xml", true)]
    [InlineData("invoice_??.csv", true)]
    [InlineData("", false)]
    [InlineData(".*", false)]                  // hidden files are never picked up
    [InlineData("../*.json", false)]
    [InlineData("in/*.json", false)]
    [InlineData(@"in\*.json", false)]
    public void FilePattern(string pattern, bool valid) => Assert.Equal(valid, FolderPaths.IsValidFilePattern(pattern));

    [Theory]
    [InlineData("invoice.json", false)]
    [InlineData("invoice.json.tmp", true)]
    [InlineData("invoice.json.part", true)]
    [InlineData("invoice.JSON.PARTIAL", true)]
    [InlineData("invoice.json.filepart", true)]  // WinSCP
    [InlineData(".invoice.json", true)]
    [InlineData("~invoice.json", true)]
    public void HandoffConvention_TemporaryNames(string name, bool temporary) => Assert.Equal(temporary, FolderPaths.IsTemporaryName(name));

    [Theory]
    [InlineData("in", "", "in", "processed", false, false)]        // sibling-of-scan isn't scanned without subfolders
    [InlineData("in", "", "in", "processed", true, true)]          // ...but is with subfolders
    [InlineData("in", "inbound", "in", "inbound", false, true)]    // the scanned folder itself
    [InlineData("in", "inbound", "in", "INBOUND", false, true)]    // case-insensitively
    [InlineData("in", "inbound", "in", "inbound/done", true, true)]
    [InlineData("in", "inbound", "in", "inbound/done", false, false)]
    [InlineData("in", "inbound", "in", "inbound-done", true, false)] // a prefix of the name isn't "below"
    [InlineData("in", "", "out", "", true, false)]                 // another location
    public void WouldRescan(string srcLoc, string srcSub, string dstLoc, string dstSub, bool includeSubfolders, bool expected) =>
        Assert.Equal(expected, FolderPaths.WouldRescan(new FolderRef(srcLoc, srcSub), new FolderRef(dstLoc, dstSub), includeSubfolders));

    [Fact]
    public void Settings_ParseTheNodeConfig()
    {
        using var config = JsonDocument.Parse("""
            { "sourceLocation": "in", "sourceSubfolder": "/invoices/", "pattern": "*.json", "includeSubfolders": true, "interval": "every-5-minutes",
              "processedLocation": "out", "processedSubfolder": "done", "failedLocation": "out", "failedSubfolder": "" }
            """);

        var settings = FolderInputSettings.Parse(config.RootElement)!;

        Assert.Equal(new FolderRef("in", "invoices"), settings.Source);
        Assert.True(settings.IncludeSubfolders);
        Assert.Equal(TimeSpan.FromMinutes(5), settings.IntervalTime);
        Assert.Equal(new FolderRef("out", "done"), settings.Processed);
        Assert.Equal(new FolderRef("out", ""), settings.Failed);
    }

    [Fact]
    public void Settings_WithoutALocation_AreNotUsable()
    {
        using var config = JsonDocument.Parse("""{ "sourceLocation": "", "pattern": "*.json", "processedLocation": "out", "failedLocation": "out" }""");
        Assert.Null(FolderInputSettings.Parse(config.RootElement));
    }
}
