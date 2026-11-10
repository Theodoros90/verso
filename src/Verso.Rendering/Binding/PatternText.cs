using System.Text;
using System.Text.Json;

namespace Verso.Rendering.Binding;

/// <summary>A pattern resolved to plain text, plus the bindings that had no value (they resolve to empty text).</summary>
public sealed record ResolvedPattern(string Text, IReadOnlyList<string> Missing);

/// <summary>
/// Resolves plain-text patterns such as <c>{{invoice.number}}.pdf</c> or <c>invoices/{{invoice.date|year}}</c> against
/// data, with the same token grammar and filters as text elements. The result is plain text (not HTML): callers that
/// use it as a file name or folder must sanitize it.
/// </summary>
public static class PatternText
{
    public static ResolvedPattern Resolve(string pattern, JsonElement data)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var text = new StringBuilder();
        var missing = new List<string>();
        foreach (var part in TextTemplate.Parse(pattern))
        {
            switch (part)
            {
                case LiteralPart literal:
                    text.Append(literal.Text);
                    break;
                case TokenPart token:
                    if (!DataPath.TryResolve(data, token.Path, out var value) || !Filters.TryGetScalarText(value, out var raw))
                    {
                        missing.Add(token.Path);
                    }
                    else if (token.Filter is null)
                    {
                        text.Append(raw);
                    }
                    else
                    {
                        text.Append(Filters.TryApply(token.Filter, value, out var formatted) ? formatted : raw);
                    }

                    break;
                case MalformedTokenPart bad:
                    missing.Add(bad.Source);
                    break;
            }
        }

        return new ResolvedPattern(text.ToString(), missing);
    }
}
