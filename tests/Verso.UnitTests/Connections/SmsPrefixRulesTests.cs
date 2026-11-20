using Verso.Application.Connections;

namespace Verso.UnitTests.Connections;

public sealed class SmsPrefixRulesTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("357", "357")]
    [InlineData("+357", "357")]
    [InlineData("00357", "357")]
    [InlineData("357, 30", "357,30")]
    [InlineData("357;30 49", "357,30,49")]
    [InlineData("49,30,49,+30", "49,30")]
    [InlineData(",, 1 ,", "1")]
    [InlineData("1234", "1234")]
    public void Valid_input_is_normalised_in_the_order_typed(string? input, string expected)
    {
        Assert.True(SmsRules.TryNormalisePrefixes(input, out var normalised, out var problem));
        Assert.Equal(expected, normalised);
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("35x")]
    [InlineData("0")]
    [InlineData("0357")]
    [InlineData("12345")]
    [InlineData("+")]
    [InlineData("00")]
    [InlineData("++357")]
    [InlineData("3-5")]
    public void Invalid_input_is_refused_with_a_problem(string input)
    {
        Assert.False(SmsRules.TryNormalisePrefixes(input, out var normalised, out var problem));
        Assert.Equal("", normalised);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public void The_problem_echoes_at_most_ten_characters_of_the_token()
    {
        Assert.False(SmsRules.TryNormalisePrefixes("ABCDEFGHIJKLMNOPQRSTUVWXYZ", out _, out var problem));

        Assert.Contains("ABCDEFGHIJ", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("ABCDEFGHIJK", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Twenty_prefixes_are_allowed_and_twenty_one_are_not()
    {
        var twenty = string.Join(',', Enumerable.Range(1, 20));
        Assert.True(SmsRules.TryNormalisePrefixes(twenty, out var normalised, out _));
        Assert.Equal(twenty, normalised);

        Assert.False(SmsRules.TryNormalisePrefixes(twenty + ",21", out _, out var problem));
        Assert.Contains("20", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicates_do_not_count_towards_the_limit()
    {
        var input = string.Join(',', Enumerable.Range(1, 20)) + ",1,2,+3";
        Assert.True(SmsRules.TryNormalisePrefixes(input, out var normalised, out _));
        Assert.Equal(20, normalised.Split(',').Length);
    }
}
