using Verso.Application.Ingestion;

namespace Verso.UnitTests.Ingestion;

public sealed class FolderLocationAdminTests
{
    [Theory]
    [InlineData(@"D:\Verso\inbound")]
    [InlineData("D:/Verso/inbound")]
    [InlineData(@"\\fileserver\verso\ap-inbound")]
    [InlineData("/mnt/verso/ap-inbound")]
    [InlineData(@"C:\Folder With Spaces\in")]
    public void AbsolutePaths_AreAccepted(string path) => Assert.Null(FolderLocationAdmin.PathProblem(path));

    [Theory]
    [InlineData("")]
    [InlineData("inbound")]                    // relative
    [InlineData(@"..\inbound")]
    [InlineData(@"D:inbound")]                 // drive-relative
    [InlineData(@"D:\Verso\..\Windows")]       // traversal
    [InlineData(@"D:\Verso\.\in")]
    [InlineData("/mnt/../etc")]
    [InlineData(@"D:\Ver*so")]                 // wildcard
    [InlineData("D:\\Verso\\in\u0000")]        // control character
    public void OtherPaths_AreRefused(string path) => Assert.NotNull(FolderLocationAdmin.PathProblem(path));

    [Fact]
    public void LongPaths_AreRefused() => Assert.NotNull(FolderLocationAdmin.PathProblem(@"D:\" + new string('a', 450)));
}
