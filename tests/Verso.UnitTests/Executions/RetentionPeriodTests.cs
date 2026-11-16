using Verso.Application.Documents;

namespace Verso.UnitTests.Executions;

public sealed class RetentionPeriodTests
{
    private static readonly DateTimeOffset From = new(2026, 11, 30, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("90d", "2027-02-28")]
    [InlineData("3m", "2027-02-28")]
    [InlineData("6m", "2027-05-30")]
    [InlineData("7y", "2033-11-30")]
    [InlineData("10y", "2036-11-30")]
    public void Until_AddsTheAmountInTheUnit(string retention, string expected)
    {
        Assert.Equal(DateTimeOffset.Parse(expected + "T10:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture), RetentionPeriod.Until(retention, From));
    }

    [Theory]
    [InlineData("permanent")]
    [InlineData("")]
    [InlineData("0m")]
    [InlineData("-3m")]
    [InlineData("6w")]
    [InlineData("m")]
    public void Until_IsNull_ForPermanentOrUnrecognisedValues(string retention)
    {
        Assert.Null(RetentionPeriod.Until(retention, From));
    }
}
