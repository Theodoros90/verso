using System.Diagnostics;
using Microsoft.Extensions.Options;
using Verso.Application.Ingestion;
using Verso.Infrastructure.Ingestion;

namespace Verso.Api.Tests;

/// <summary>The Worker's folder adapter on real temporary folders: containment, handoff convention, claims, delivery, access checks.</summary>
public sealed class LocalFolderAccessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "verso-test-folders", Guid.NewGuid().ToString("N"));
    private readonly LocalFolderAccess _folders;

    public LocalFolderAccessTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "in"));
        Directory.CreateDirectory(Path.Combine(_root, "out"));
        _folders = Create(new()
        {
            ["in"] = new() { Name = "Inbound", Path = Path.Combine(_root, "in"), Input = true },
            ["out"] = new() { Name = "Outbound", Path = Path.Combine(_root, "out"), Output = true },
        });
    }

    public void Dispose()
    {
        foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).Where(d => new DirectoryInfo(d).LinkTarget is not null).ToList())
        {
            Directory.Delete(dir); // remove links first, so deleting the tree never follows them
        }

        Directory.Delete(_root, recursive: true);
    }

    private static LocalFolderAccess Create(Dictionary<string, FolderLocationOptions> locations) =>
        new(Options.Create(new FolderAccessOptions { Locations = locations }));

    private string Write(string relative, string content = "{}", TimeSpan? age = null)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - (age ?? TimeSpan.FromMinutes(1)));
        return path;
    }

    private IReadOnlyList<FoundFile> List(string subfolder = "", string pattern = "*.json", bool include = false, TimeSpan? settle = null) =>
        _folders.List(new FolderRef("in", subfolder), pattern, include, settle ?? TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow, 100);

    [Theory]
    [InlineData("relative/path", "absolute")]
    [InlineData("", "absolute")]
    public void ConfiguredPaths_MustBeAbsolute(string path, string problem)
    {
        var folders = Create(new() { ["bad"] = new() { Path = path, Input = true } });
        Assert.Contains(problem, folders.Locations.Single().Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfiguredPaths_MustBeNormalized_AndNotARoot()
    {
        var folders = Create(new()
        {
            ["dots"] = new() { Path = Path.Combine(_root, "in", "..", "out"), Output = true },
            ["root"] = new() { Path = Path.GetPathRoot(_root)!, Input = true },
            ["none"] = new() { Path = _root },
        });

        Assert.Contains("normalized", folders.Locations.Single(l => l.Key == "dots").Problem, StringComparison.Ordinal);
        Assert.Contains("root", folders.Locations.Single(l => l.Key == "root").Problem, StringComparison.Ordinal);
        Assert.Contains("Neither", folders.Locations.Single(l => l.Key == "none").Problem, StringComparison.Ordinal);
        var ex = Assert.Throws<FolderException>(() => folders.List(new FolderRef("root", ""), "*", false, TimeSpan.Zero, DateTimeOffset.UtcNow, 10));
        Assert.Equal(FolderErrors.Misconfigured, ex.Code);
    }

    [Fact]
    public void Locations_ExposeNoPaths() =>
        Assert.DoesNotContain(_folders.Locations, l => (l.Name + l.Problem).Contains(_root, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void List_FollowsTheHandoffConvention()
    {
        Write("in/ready.json");
        Write("in/writing.json.tmp");                      // temporary name
        Write("in/.hidden.json");                         // hidden
        Write("in/fresh.json", age: TimeSpan.Zero);       // still changing (inside the settle time)
        Write("in/other.xml");                            // not the pattern
        Write("in/sub/nested.json");                      // subfolder not included
        Write("in/.verso-inflight/0123.json");            // Verso's working folder

        Assert.Equal(["ready.json"], List().Select(f => f.RelativePath));
        Assert.Equal(["ready.json", "sub/nested.json"], List(include: true).Select(f => f.RelativePath).Order());
        Assert.Equal(["fresh.json", "ready.json"], List(settle: TimeSpan.Zero).Select(f => f.RelativePath).Order());
    }

    [Fact]
    public void List_MatchesThePatternCaseInsensitively()
    {
        Write("in/INV-1.JSON");
        Write("in/inv-2.json");
        Write("in/other.json");
        Assert.Equal(["INV-1.JSON", "inv-2.json"], List(pattern: "inv-*.json").Select(f => f.FileName).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("../out")]
    [InlineData("sub/../../out")]
    [InlineData(@"..\out")]
    public void Traversal_IsRefused(string subfolder)
    {
        var ex = Assert.Throws<FolderException>(() => List(subfolder));
        Assert.Equal(FolderErrors.OutsideLocation, ex.Code);
    }

    [Fact]
    public void RelativePathsFromTheDatabase_CantLeaveTheFolder()
    {
        var ex = Assert.Throws<FolderException>(() => _folders.TryMoveToInFlight(new FolderRef("in", ""), "../out/x.json", Guid.NewGuid()));
        Assert.Equal(FolderErrors.OutsideLocation, ex.Code);
    }

    [Fact]
    public void LinkedSubfolders_AreRefused()
    {
        var target = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "secret.json"), "{}");
        File.SetLastWriteTimeUtc(Path.Combine(target, "secret.json"), DateTime.UtcNow.AddMinutes(-1));
        CreateDirectoryLink(Path.Combine(_root, "in", "link"), target);

        var ex = Assert.Throws<FolderException>(() => List("link"));
        Assert.Equal(FolderErrors.OutsideLocation, ex.Code);
        Assert.Empty(List(include: true)); // and recursion doesn't follow it
    }

    [Fact]
    public void UnknownLocations_AndWrongPurpose_AreRefused()
    {
        Assert.Equal(FolderErrors.UnknownLocation, Assert.Throws<FolderException>(() =>
            _folders.List(new FolderRef("c-drive", ""), "*", false, TimeSpan.Zero, DateTimeOffset.UtcNow, 1)).Code);
        Assert.Equal(FolderErrors.Misconfigured, Assert.Throws<FolderException>(() =>
            _folders.List(new FolderRef("out", ""), "*", false, TimeSpan.Zero, DateTimeOffset.UtcNow, 1)).Code);
    }

    [Fact]
    public void MissingFolder_IsReportedAsNotFound()
    {
        var ex = Assert.Throws<FolderException>(() => List("does-not-exist"));
        Assert.Equal(FolderErrors.NotFound, ex.Code);
        Assert.DoesNotContain(_root, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFileOpenForWriting_IsNotClaimed()
    {
        var path = Write("in/busy.json");
        using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.False(_folders.TryMoveToInFlight(new FolderRef("in", ""), "busy.json", Guid.NewGuid()));
            }
        }

        Assert.True(_folders.TryMoveToInFlight(new FolderRef("in", ""), "busy.json", Guid.NewGuid()));
    }

    [Fact]
    public void OnlyOneClaimOfAFile_Succeeds()
    {
        Write("in/a.json");
        var source = new FolderRef("in", "");
        Assert.True(_folders.TryMoveToInFlight(source, "a.json", Guid.NewGuid()));
        Assert.False(_folders.TryMoveToInFlight(source, "a.json", Guid.NewGuid()));
    }

    [Fact]
    public async Task Claim_Read_Deliver_RoundTrip_AvoidsOverwritingAndIsIdempotent()
    {
        var source = new FolderRef("in", "");
        var done = new FolderRef("out", "done");
        Write("out/done/a.json", "someone else's file"); // the name is taken in the destination
        Write("in/a.json", """{ "n": 1 }""");
        var id = Guid.NewGuid();

        Assert.True(_folders.TryMoveToInFlight(source, "a.json", id));
        Assert.False(File.Exists(Path.Combine(_root, "in", "a.json")));
        var content = await _folders.ReadInFlightAsync(source, id, 1024, CancellationToken.None);
        Assert.Equal("""{ "n": 1 }""", System.Text.Encoding.UTF8.GetString(content.Content!));

        var name = _folders.Deliver(source, id, done, "a.json", content.Sha256);

        Assert.Equal($"a.{id.ToString("N")[..8]}.json", name);
        Assert.Equal("someone else's file", File.ReadAllText(Path.Combine(_root, "out", "done", "a.json")));
        Assert.False(_folders.InFlightExists(source, id));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "out", "done"), ".*")); // no temporary files left

        // A Worker that crashed after moving, before recording it, gets the same answer again.
        Assert.Equal(name, _folders.Deliver(source, id, done, "a.json", content.Sha256));
    }

    [Fact]
    public async Task Deliver_AfterACrashBetweenRenameAndCleanup_RemovesTheWorkingCopy()
    {
        var source = new FolderRef("in", "");
        Write("in/b.json", "{}");
        var id = Guid.NewGuid();
        _folders.TryMoveToInFlight(source, "b.json", id);
        var sha = (await _folders.ReadInFlightAsync(source, id, 1024, CancellationToken.None)).Sha256;
        Directory.CreateDirectory(Path.Combine(_root, "out"));
        File.Copy(Path.Combine(_root, "in", ".verso-inflight", id.ToString("N")), Path.Combine(_root, "out", "b.json"));

        Assert.Equal("b.json", _folders.Deliver(source, id, new FolderRef("out", ""), "b.json", sha));
        Assert.False(_folders.InFlightExists(source, id));
    }

    [Fact]
    public void Deliver_OfAVanishedFile_IsFileMissing()
    {
        var ex = Assert.Throws<FolderException>(() => _folders.Deliver(new FolderRef("in", ""), Guid.NewGuid(), new FolderRef("out", ""), "x.json", new string('0', 64)));
        Assert.Equal(FolderErrors.FileMissing, ex.Code);
    }

    [Fact]
    public async Task LargeFiles_AreHashedButNotLoaded()
    {
        Write("in/big.json", new string('x', 5000));
        var id = Guid.NewGuid();
        _folders.TryMoveToInFlight(new FolderRef("in", ""), "big.json", id);

        var content = await _folders.ReadInFlightAsync(new FolderRef("in", ""), id, 1000, CancellationToken.None);

        Assert.Null(content.Content);
        Assert.Equal(5000, content.SizeBytes);
        Assert.Equal(64, content.Sha256.Length);
    }

    [Fact]
    public void Test_ChecksInputAndOutputFolders_AndLeavesNothingBehind()
    {
        var input = _folders.Test(new FolderRef("in", ""), FolderAccessMode.Input);
        var output = _folders.Test(new FolderRef("out", "new/sub"), FolderAccessMode.Output);

        Assert.All(input, c => Assert.True(c.Ok, c.Message));
        Assert.Equal(["approved", "folder", "modify"], input.Select(c => c.Name));
        Assert.All(output, c => Assert.True(c.Ok, c.Message));
        Assert.Contains(output, c => c.Message.Contains("created", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Test_ReportsWrongPurpose_AndUnknownLocations()
    {
        var wrong = _folders.Test(new FolderRef("out", ""), FolderAccessMode.Input);
        var unknown = _folders.Test(new FolderRef("nope", ""), FolderAccessMode.Output);

        Assert.False(wrong.Single().Ok);
        Assert.Contains("isn't approved for input", wrong.Single().Message, StringComparison.Ordinal);
        Assert.Contains("isn't configured on this Worker", unknown.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AccessDenied_IsReportedWithTheServiceIdentity()
    {
        var denied = Path.Combine(_root, "in", "denied");
        Directory.CreateDirectory(denied);
        if (!Deny(denied))
        {
            Assert.False(OperatingSystem.IsWindows(), "test setup: icacls couldn't deny access");
            return; // Linux as root: permissions don't apply, nothing to check
        }

        try
        {
            var ex = Assert.Throws<FolderException>(() => List("denied"));
            Assert.Equal(FolderErrors.AccessDenied, ex.Code);
            Assert.Contains(_folders.Identity, ex.Message, StringComparison.Ordinal);

            var checks = _folders.Test(new FolderRef("in", "denied"), FolderAccessMode.Input);
            Assert.False(checks[^1].Ok);
            Assert.Contains("Access denied", checks[^1].Message, StringComparison.Ordinal);
        }
        finally
        {
            Allow(denied);
        }
    }

    [Fact]
    public void IsSameOrInside_ComparesResolvedPaths()
    {
        var folders = Create(new()
        {
            ["share"] = new() { Path = Path.Combine(_root, "in"), Input = true },
            ["share-done"] = new() { Path = Path.Combine(_root, "in", "done"), Output = true }, // another key for a folder below "share"
        });

        Assert.True(folders.IsSameOrInside(new FolderRef("share-done", ""), new FolderRef("share", "")));
        Assert.True(folders.IsSameOrInside(new FolderRef("share-done", "x"), new FolderRef("share", "done")));
        Assert.False(folders.IsSameOrInside(new FolderRef("share", ""), new FolderRef("share-done", "")));
        Assert.False(folders.IsSameOrInside(new FolderRef("share", "done2"), new FolderRef("share", "done")));
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            // A junction: needs no administrator rights (symbolic links do).
            Run("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"");
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }

        Assert.True(new DirectoryInfo(link).LinkTarget is not null, "test setup: the link wasn't created");
    }

    private static bool Deny(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            Run("icacls.exe", $"\"{folder}\" /deny \"{Environment.UserDomainName}\\{Environment.UserName}\":(OI)(CI)(RD,WD,AD)");
            try
            {
                Directory.GetFiles(folder);
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        File.SetUnixFileMode(folder, UnixFileMode.None);
        try
        {
            Directory.GetFiles(folder);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void Allow(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            Run("icacls.exe", $"\"{folder}\" /remove:d \"{Environment.UserDomainName}\\{Environment.UserName}\"");
        }
        else
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void Run(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        process.WaitForExit(10_000);
    }
}
