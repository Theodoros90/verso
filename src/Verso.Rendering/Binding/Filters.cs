using System.Globalization;
using System.Text.Json;

namespace Verso.Rendering.Binding;

/// <summary>
/// Value formatting for bindings. Output is fixed (English, EUR) and independent of the server's culture,
/// so the same data renders identically everywhere: money <c>€1,234.56</c>, date <c>02 Nov 2026</c>.
/// </summary>
public static class Filters
{
    public static IReadOnlyList<string> Names { get; } = ["date", "money", "number", "upper", "lower", "year", "month", "day"];

    public static bool IsKnown(string name) => Names.Contains(name, StringComparer.Ordinal);

    /// <summary>Plain text of a scalar value; false for objects and arrays.</summary>
    public static bool TryGetScalarText(JsonElement value, out string text)
    {
        text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
        return value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;
    }

    /// <summary>Applies <paramref name="filter"/>; false when the value doesn't suit it (e.g. <c>money</c> of "abc").</summary>
    public static bool TryApply(string filter, JsonElement value, out string result)
    {
        result = "";
        if (!TryGetScalarText(value, out var text))
        {
            return false;
        }

        switch (filter)
        {
            case "upper":
                result = text.ToUpperInvariant();
                return true;
            case "lower":
                result = string.Concat(text.Select(char.ToLowerInvariant));
                return true;
            case "money":
                if (!TryGetDecimal(value, text, out var amount))
                {
                    return false;
                }

                var rounded = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
                result = (rounded < 0 ? "-" : "") + "€" + Math.Abs(rounded).ToString("#,##0.00", CultureInfo.InvariantCulture);
                return true;
            case "number":
                if (!TryGetDecimal(value, text, out var number))
                {
                    return false;
                }

                result = number.ToString("#,##0.##########", CultureInfo.InvariantCulture);
                return true;
            case "date" or "year" or "month" or "day":
                if (!TryGetDate(text, out var date))
                {
                    return false;
                }

                result = filter switch
                {
                    "date" => date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
                    "year" => date.ToString("yyyy", CultureInfo.InvariantCulture),
                    "month" => date.ToString("MM", CultureInfo.InvariantCulture),
                    _ => date.ToString("dd", CultureInfo.InvariantCulture),
                };
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetDecimal(JsonElement value, string text, out decimal number) =>
        value.ValueKind == JsonValueKind.Number
            ? value.TryGetDecimal(out number)
            : decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out number);

    /// <summary>Accepts <c>yyyy-MM-dd</c> or an ISO 8601 date-time; the calendar date as written is used (no time-zone shift).</summary>
    private static bool TryGetDate(string text, out DateOnly date)
    {
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }

        if (text.Length > 10 && text[10] == 'T'
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
            && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }

        date = default;
        return false;
    }
}
