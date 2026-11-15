namespace Verso.Rendering.Binding;

/// <summary>
/// The two reserved tokens of header and footer text: <c>{{page}}</c> (this page's number) and <c>{{pages}}</c> (how many pages
/// the document has). In the body, and with a filter, they are ordinary data paths.
/// </summary>
internal static class PageTokens
{
    public const string Page = "page";
    public const string Pages = "pages";

    public static bool Is(TokenPart token) => token.Filter is null && token.Path is Page or Pages;
}
