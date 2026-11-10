using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Verso.Rendering.Binding;

namespace Verso.Application.Executions;

/// <summary>One failed rule. Messages describe the expectation only; they never echo the submitted value.</summary>
public sealed record ValidationIssue(string Path, string Rule, string Message);

/// <summary>
/// The Validate node's checks (plan §6.2 step 4): required fields, then typed checks on the fields that are present.
/// A type check on a missing field passes; add the field to "required" to demand it.
/// </summary>
public static partial class ValidationRules
{
    public static IReadOnlyList<ValidationIssue> Check(JsonElement config, JsonElement data)
    {
        var issues = new List<ValidationIssue>();
        foreach (var path in Strings(config, "required"))
        {
            if (!DataPath.TryResolve(data, path, out var value) || IsEmpty(value))
            {
                issues.Add(new(path, "required", "missing or empty"));
            }
        }

        if (config.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Array)
        {
            foreach (var rule in types.EnumerateArray())
            {
                if (rule.ValueKind != JsonValueKind.Object
                    || !rule.TryGetProperty("field", out var f) || f.ValueKind != JsonValueKind.String
                    || !rule.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String)
                {
                    continue; // config is schema-validated on publish
                }

                var field = f.GetString()!;
                var type = t.GetString()!;
                if (DataPath.TryResolve(data, field, out var value) && CheckType(type, value) is { } message)
                {
                    issues.Add(new(field, "type:" + type, message));
                }
            }
        }

        return issues;
    }

    /// <summary>Null when <paramref name="value"/> has the type, otherwise what was expected.</summary>
    public static string? CheckType(string type, JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : null;
        return type switch
        {
            "text" => text is not null ? null : "expected text",
            "number" => value.ValueKind == JsonValueKind.Number ? null : "expected a number",
            "date" => text is not null && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                ? null : "expected YYYY-MM-DD",
            "email" => text is not null && IsEmail(text) ? null : "expected an email address",
            "eu-vat" => text is not null && IsEuVat(text) ? null : "expected an EU VAT number (country prefix and number, e.g. CY12345678X)",
            "iban" => text is not null && IsIban(text) ? null : "expected a valid IBAN",
            _ => $"unknown type check '{type}'",
        };
    }

    public static bool IsEmail(string text) => text.Length <= 254 && Email().IsMatch(text);

    /// <summary>Format check per member state (as published for VIES). No online lookup in the MVP.</summary>
    public static bool IsEuVat(string text)
    {
        var vat = Compact(text);
        return vat.Length >= 4 && VatFormats.TryGetValue(vat[..2], out var format) && Regex.IsMatch(vat[2..], format, RegexOptions.None, TimeSpan.FromSeconds(1));
    }

    /// <summary>ISO 13616: country code, check digits, country-specific length (where known), mod-97 = 1.</summary>
    public static bool IsIban(string text)
    {
        var iban = Compact(text);
        if (!IbanShape().IsMatch(iban) || (IbanLengths.TryGetValue(iban[..2], out var length) && iban.Length != length))
        {
            return false;
        }

        var rearranged = iban[4..] + iban[..4];
        var digits = string.Concat(rearranged.Select(c => char.IsDigit(c) ? c.ToString() : (c - 'A' + 10).ToString(CultureInfo.InvariantCulture)));
        return BigInteger.Parse(digits, CultureInfo.InvariantCulture) % 97 == 1;
    }

    private static bool IsEmpty(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Array => value.GetArrayLength() == 0,
        _ => false,
    };

    private static string Compact(string text) =>
        string.Concat(text.Where(c => c is not (' ' or '-' or '.'))).ToUpperInvariant();

    private static IEnumerable<string> Strings(JsonElement config, string name) =>
        config.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)
            : [];

    [GeneratedRegex(@"^[^@\s]+@[^@\s.]+(\.[^@\s.]+)+$")]
    private static partial Regex Email();

    [GeneratedRegex("^[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}$")]
    private static partial Regex IbanShape();

    private static readonly Dictionary<string, string> VatFormats = new(StringComparer.Ordinal)
    {
        ["AT"] = "^U[0-9]{8}$",
        ["BE"] = "^[01][0-9]{9}$",
        ["BG"] = "^[0-9]{9,10}$",
        ["CY"] = "^[0-9]{8}[A-Z]$",
        ["CZ"] = "^[0-9]{8,10}$",
        ["DE"] = "^[0-9]{9}$",
        ["DK"] = "^[0-9]{8}$",
        ["EE"] = "^[0-9]{9}$",
        ["EL"] = "^[0-9]{9}$",
        ["ES"] = "^[0-9A-Z][0-9]{7}[0-9A-Z]$",
        ["FI"] = "^[0-9]{8}$",
        ["FR"] = "^[0-9A-Z]{2}[0-9]{9}$",
        ["HR"] = "^[0-9]{11}$",
        ["HU"] = "^[0-9]{8}$",
        ["IE"] = "^([0-9][0-9A-Z+*][0-9]{5}[A-Z]|[0-9]{7}[A-W][A-I])$",
        ["IT"] = "^[0-9]{11}$",
        ["LT"] = "^([0-9]{9}|[0-9]{12})$",
        ["LU"] = "^[0-9]{8}$",
        ["LV"] = "^[0-9]{11}$",
        ["MT"] = "^[0-9]{8}$",
        ["NL"] = "^[0-9]{9}B[0-9]{2}$",
        ["PL"] = "^[0-9]{10}$",
        ["PT"] = "^[0-9]{9}$",
        ["RO"] = "^[0-9]{2,10}$",
        ["SE"] = "^[0-9]{12}$",
        ["SI"] = "^[0-9]{8}$",
        ["SK"] = "^[0-9]{10}$",
        ["XI"] = "^([0-9]{9}|[0-9]{12}|GD[0-9]{3}|HA[0-9]{3})$",
    };

    /// <summary>IBAN lengths of SEPA countries; other countries get the generic shape and checksum checks.</summary>
    private static readonly Dictionary<string, int> IbanLengths = new(StringComparer.Ordinal)
    {
        ["AD"] = 24, ["AT"] = 20, ["BE"] = 16, ["BG"] = 22, ["CH"] = 21, ["CY"] = 28, ["CZ"] = 24, ["DE"] = 22,
        ["DK"] = 18, ["EE"] = 20, ["ES"] = 24, ["FI"] = 18, ["FR"] = 27, ["GB"] = 22, ["GI"] = 23, ["GR"] = 27,
        ["HR"] = 21, ["HU"] = 28, ["IE"] = 22, ["IS"] = 26, ["IT"] = 27, ["LI"] = 21, ["LT"] = 20, ["LU"] = 20,
        ["LV"] = 21, ["MC"] = 27, ["MT"] = 31, ["NL"] = 18, ["NO"] = 15, ["PL"] = 28, ["PT"] = 25, ["RO"] = 24,
        ["SE"] = 24, ["SI"] = 19, ["SK"] = 24, ["SM"] = 27, ["VA"] = 22,
    };
}
