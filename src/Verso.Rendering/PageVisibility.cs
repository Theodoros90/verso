namespace Verso.Rendering;

/// <summary>On which pages of a document a header or footer band is printed (layout JSON <c>showOn</c>).</summary>
public enum PageVisibility
{
    All,
    First,
    Last,
    NotFirst,
    NotLast,
}

public static class PageVisibilityExtensions
{
    /// <summary>JSON spelling: <c>all</c>, <c>first</c>, <c>last</c>, <c>not-first</c>, <c>not-last</c>.</summary>
    public static string ToJson(this PageVisibility v) => v switch
    {
        PageVisibility.First => "first",
        PageVisibility.Last => "last",
        PageVisibility.NotFirst => "not-first",
        PageVisibility.NotLast => "not-last",
        _ => "all",
    };

    public static bool TryParse(string? text, out PageVisibility value)
    {
        (var known, value) = text switch
        {
            "all" => (true, PageVisibility.All),
            "first" => (true, PageVisibility.First),
            "last" => (true, PageVisibility.Last),
            "not-first" => (true, PageVisibility.NotFirst),
            "not-last" => (true, PageVisibility.NotLast),
            _ => (false, PageVisibility.All),
        };
        return known;
    }

    /// <summary>Whether the band is printed on <paramref name="page"/> (1-based) of a <paramref name="total"/>-page document.</summary>
    public static bool IsVisible(this PageVisibility v, int page, int total) => v switch
    {
        PageVisibility.First => page == 1,
        PageVisibility.Last => page == total,
        PageVisibility.NotFirst => page != 1,
        PageVisibility.NotLast => page != total,
        _ => true,
    };
}
