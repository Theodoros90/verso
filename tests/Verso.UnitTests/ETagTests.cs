using Verso.Application;

namespace Verso.UnitTests;

public sealed class ETagTests
{
    private static readonly byte[] Current = [0, 0, 0, 0, 0, 0, 7, 209];

    [Fact]
    public void Check_AcceptsTheCurrentETag_StrongOrWeak()
    {
        var etag = ETag.From(Current);

        Assert.Null(ETag.Check(etag, Current, out var expected));
        Assert.Equal(Current, expected);
        Assert.Null(ETag.Check("W/" + etag, Current, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    public void Check_RequiresAnExplicitETag(string? ifMatch)
    {
        Assert.Equal(ErrorKind.PreconditionRequired, ETag.Check(ifMatch, Current, out _)!.Kind);
    }

    [Theory]
    [InlineData("\"AAAAAAAAB9A=\"")] // a different rowversion
    [InlineData("\"not base64!\"")]
    [InlineData("AAAAAAAAB9E=")] // unquoted
    public void Check_RejectsStaleOrMalformedETags(string ifMatch)
    {
        Assert.Equal(ErrorKind.PreconditionFailed, ETag.Check(ifMatch, Current, out _)!.Kind);
    }
}
