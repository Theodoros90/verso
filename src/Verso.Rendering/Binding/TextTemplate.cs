using System.Text.RegularExpressions;

namespace Verso.Rendering.Binding;

/// <summary>A piece of a text element: literal text, a <c>{{path|filter}}</c> token, or a malformed token kept as text.</summary>
internal abstract record TextPart;

internal sealed record LiteralPart(string Text) : TextPart;

internal sealed record TokenPart(string Path, string? Filter, string Source) : TextPart;

internal sealed record MalformedTokenPart(string Source) : TextPart;

/// <summary>Splits text such as <c>Invoice {{invoice.number}} due {{invoice.dueDate|date}}</c> into parts.</summary>
internal static partial class TextTemplate
{
    [GeneratedRegex(@"\{\{(.*?)\}\}", RegexOptions.Singleline)]
    private static partial Regex AnyToken();

    [GeneratedRegex(@"^\s*([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\s*(?:\|\s*([a-z]+)\s*)?$")]
    private static partial Regex ValidToken();

    public static IReadOnlyList<TextPart> Parse(string text)
    {
        var parts = new List<TextPart>();
        var last = 0;
        foreach (Match m in AnyToken().Matches(text))
        {
            if (m.Index > last)
            {
                parts.Add(new LiteralPart(text[last..m.Index]));
            }

            var inner = ValidToken().Match(m.Groups[1].Value);
            if (inner.Success && (!inner.Groups[2].Success || Filters.IsKnown(inner.Groups[2].Value)))
            {
                parts.Add(new TokenPart(inner.Groups[1].Value, inner.Groups[2].Success ? inner.Groups[2].Value : null, m.Value));
            }
            else
            {
                parts.Add(new MalformedTokenPart(m.Value));
            }

            last = m.Index + m.Length;
        }

        if (last < text.Length)
        {
            parts.Add(new LiteralPart(text[last..]));
        }

        return parts;
    }
}
