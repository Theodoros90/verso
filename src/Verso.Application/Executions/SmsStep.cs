using System.Globalization;
using System.Text;
using System.Text.Json;
using Verso.Application.Connections;
using Verso.Rendering.Binding;

namespace Verso.Application.Executions;

/// <summary>
/// The number and the text of a Send SMS step: fixed words with <c>{{data.path}}</c> pieces. The templates are checked at design time (publish); at run time
/// (<see cref="Resolve"/>) each piece is replaced by its value as PLAIN text (an SMS is not a URL or HTML), and the finished text goes through the same
/// <see cref="SmsRules.TryNormaliseText"/> rules the sender applies. No message here ever contains a data value: only the data path of the piece or the name of the rule.
/// </summary>
public static class SmsTemplates
{
    public const int MaxToLength = 100;

    /// <summary>Why the number template can't be used, or null when it can. A fixed number is allowed; otherwise the number comes from at least one data field.</summary>
    public static string? ValidateTo(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return "The recipient's number is required.";
        }

        if (template.Length > MaxToLength)
        {
            return $"The recipient's number is at most {MaxToLength} characters.";
        }

        var (pieces, problem) = Scan(template, out _);
        if (problem is not null)
        {
            return problem;
        }

        if (pieces == 0 && !template.All(c => char.IsAsciiDigit(c) || c is '+' or ' ' or '.' or '-' or '(' or ')'))
        {
            return "Write the number as a field such as {{customer.mobile}}, or as digits only (with + . - spaces and brackets).";
        }

        return null;
    }

    /// <summary>Why the text template can't be used, or null when it can. Mirrors the checks the Worker applies to the finished text.</summary>
    public static string? ValidateText(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return "The message is required.";
        }

        if (template.Length > SmsRules.MaxTextLength)
        {
            return $"The message is at most {SmsRules.MaxTextLength} characters.";
        }

        var (_, problem) = Scan(template, out var fixedText);
        if (problem is not null)
        {
            return problem;
        }

        // Pieces count as one harmless letter: the fixed words alone must already be a sendable text (the values are checked at run time).
        if (HasBadCharacter(fixedText))
        {
            return "The message has a character that can't be sent: a control character (only a line break is allowed), an invisible formatting character or half of a character pair.";
        }

        return SmsRules.TryNormaliseText(fixedText, out _) ? null : "The message can't be empty.";
    }

    /// <summary>
    /// Builds the number (<paramref name="isText"/> false, trimmed; the engine reads it with <see cref="SmsRules.TryNormaliseRecipient"/>) or the text
    /// (<paramref name="isText"/> true, already through <see cref="SmsRules.TryNormaliseText"/>) from <paramref name="data"/>. Issues (code, message, field), never a value:
    /// <c>sms.value_missing</c> (a missing or null value), <c>sms.value_invalid</c> (a list or a group of fields), <c>sms.text_invalid</c> (text that can't be read or sent),
    /// <c>sms.recipient_invalid</c> (an empty number). The field is the data path of the piece, or "to" / "text" for a rule on the whole.
    /// </summary>
    public static (string? Value, IReadOnlyList<object> Issues) Resolve(string template, JsonElement data, bool isText)
    {
        ArgumentNullException.ThrowIfNull(template);
        var which = isText ? "text" : "to";
        var issues = new List<object>();
        var result = new StringBuilder();
        var i = 0;
        while (i < template.Length)
        {
            if (!template.AsSpan(i).StartsWith("{{", StringComparison.Ordinal))
            {
                result.Append(template[i++]);
                continue;
            }

            var close = template.IndexOf("}}", i + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                issues.Add(Issue(isText ? "sms.text_invalid" : "sms.recipient_invalid", $"The {(isText ? "message" : "number")} has a {{{{ without a closing }}}}.", which));
                break;
            }

            var dataPath = template[(i + 2)..close].Trim();
            i = close + 2;
            if (!DataPath.IsValid(dataPath) || !DataPath.TryResolve(data, dataPath, out var value))
            {
                issues.Add(Issue("sms.value_missing", $"The {(isText ? "message" : "number")} needs a value, but '{Short(dataPath, 40)}' is missing or null in the data.", dataPath));
                continue;
            }

            var text = ApiFetchStep.ScalarText(value); // null for a list, a group of fields, or text System.Text.Json can't read (a lone surrogate escape)
            if (text is null)
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    issues.Add(Issue("sms.text_invalid", $"'{Short(dataPath, 40)}' holds text that can't be sent (it can't be read as text).", dataPath));
                }
                else
                {
                    issues.Add(Issue("sms.value_invalid", $"'{Short(dataPath, 40)}' holds {(value.ValueKind == JsonValueKind.Array ? "a list" : "a group of fields")}; only text, a number or true/false can be used.", dataPath));
                }

                continue;
            }

            if (isText && HasBadCharacter(text))
            {
                issues.Add(Issue("sms.text_invalid", $"'{Short(dataPath, 40)}' holds a character that can't be sent in a message (a control character, an invisible formatting character or half of a character pair).", dataPath));
                continue;
            }

            result.Append(text);
        }

        if (issues.Count > 0)
        {
            return (null, issues);
        }

        if (!isText)
        {
            var number = result.ToString().Trim();
            if (number.Length == 0)
            {
                issues.Add(Issue("sms.recipient_invalid", "The recipient's number is empty once the values are filled in.", "to"));
                return (null, issues);
            }

            return (number, issues);
        }

        var built = result.ToString();
        if (!SmsRules.TryNormaliseText(built, out var normalised))
        {
            var reason = string.IsNullOrWhiteSpace(built) ? "is empty once the values are filled in"
                : built.Replace("\r\n", "\n", StringComparison.Ordinal).Length > SmsRules.MaxTextLength ? $"is longer than {SmsRules.MaxTextLength} characters once the values are filled in"
                : "has a character that can't be sent once the values are filled in";
            issues.Add(Issue("sms.text_invalid", $"The message {reason}.", "text"));
            return (null, issues);
        }

        return (normalised, issues);
    }

    /// <summary>
    /// Walks the template: returns how many pieces it has and the first problem (unbalanced braces, a piece that isn't a data path), plus the fixed words with every
    /// piece replaced by "x".
    /// </summary>
    private static (int Pieces, string? Problem) Scan(string template, out string fixedText)
    {
        var sb = new StringBuilder();
        fixedText = "";
        var pieces = 0;
        var i = 0;
        while (i < template.Length)
        {
            if (template.AsSpan(i).StartsWith("{{", StringComparison.Ordinal))
            {
                var close = template.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    return (pieces, "A {{ has no closing }}.");
                }

                var inner = template[(i + 2)..close].Trim();
                if (!DataPath.IsValid(inner))
                {
                    return (pieces, $"'{Short(inner, 10)}' inside {{{{ }}}} isn't a valid data path (such as customer.mobile).");
                }

                pieces++;
                sb.Append('x');
                i = close + 2;
                continue;
            }

            if (template.AsSpan(i).StartsWith("}}", StringComparison.Ordinal))
            {
                return (pieces, "A }} has no opening {{.");
            }

            sb.Append(template[i++]);
        }

        fixedText = sb.ToString();
        return (pieces, null);
    }

    /// <summary>
    /// True when <paramref name="text"/> has a control character other than a line feed (a carriage return only right before a line feed), a lone surrogate or an invisible
    /// format character (except the zero-width joiner). The same rules as <see cref="SmsRules.TryNormaliseText"/> without the length and emptiness rules, so a piece can
    /// be checked on its own.
    /// </summary>
    private static bool HasBadCharacter(string text)
    {
        var value = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])
                    || CharUnicodeInfo.GetUnicodeCategory(char.ConvertToUtf32(c, value[i + 1])) == UnicodeCategory.Format)
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c) || (char.IsControl(c) && c != '\n')
                || (c != '‍' && CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format))
            {
                return true;
            }
        }

        return false;
    }

    private static object Issue(string code, string message, string field) => new { code, message, field };

    private static string Short(string s, int max) => s.Length > max ? s[..max] + "…" : s;
}
