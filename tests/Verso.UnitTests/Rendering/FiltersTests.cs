using Verso.Rendering.Binding;
using static Verso.UnitTests.Rendering.TestFiles;

namespace Verso.UnitTests.Rendering;

public sealed class FiltersTests
{
    [Theory]
    [InlineData("money", "1234.5", "€1,234.50")]
    [InlineData("money", "-12", "-€12.00")]
    [InlineData("money", "0.005", "€0.01")]
    [InlineData("money", "\"5502.25\"", "€5,502.25")]
    [InlineData("number", "18450", "18,450")]
    [InlineData("number", "0.2185", "0.2185")]
    [InlineData("date", "\"2026-11-02\"", "02 Nov 2026")]
    [InlineData("date", "\"2026-11-02T23:30:00+02:00\"", "02 Nov 2026")]
    [InlineData("year", "\"2026-11-02\"", "2026")]
    [InlineData("month", "\"2026-11-02\"", "11")]
    [InlineData("day", "\"2026-11-02\"", "02")]
    [InlineData("upper", "\"kestrel ltd\"", "KESTREL LTD")]
    [InlineData("lower", "\"INV-1\"", "inv-1")]
    public void Filters_FormatValues_IndependentOfServerCulture(string filter, string json, string expected)
    {
        Assert.True(Filters.TryApply(filter, Json(json), out var result));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("money", "\"abc\"")]
    [InlineData("date", "\"02/11/2026\"")]
    [InlineData("date", "20261102")]
    [InlineData("number", "true")]
    [InlineData("upper", "{ \"a\": 1 }")]
    [InlineData("money", "[1]")]
    public void Filters_RejectUnsuitableValues(string filter, string json)
    {
        Assert.False(Filters.TryApply(filter, Json(json), out _));
    }

    [Fact]
    public void Formatting_DoesNotDependOnCurrentCulture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.True(Filters.TryApply("money", Json("1234.5"), out var money));
            Assert.Equal("€1,234.50", money);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void DataPath_ResolvesNestedValues_AndTreatsNullAsMissing()
    {
        var data = Json("""{ "a": { "b": { "c": 1 }, "n": null } }""");

        Assert.True(DataPath.TryResolve(data, "a.b.c", out var value));
        Assert.Equal(1, value.GetInt32());
        Assert.False(DataPath.TryResolve(data, "a.n", out _));
        Assert.False(DataPath.TryResolve(data, "a.b.c.d", out _));
        Assert.False(DataPath.TryResolve(data, "x", out _));
    }
}
