using System.Security.Cryptography;
using System.Text.Json;

namespace Verso.Application;

/// <summary>
/// Canonical JSON for hashing: object properties sorted by ordinal name, no insignificant whitespace,
/// strings re-escaped consistently. Numbers are kept exactly as written (1 and 1.0 differ).
/// </summary>
public static class CanonicalJson
{
    public static string Sha256Hex(JsonElement element)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(element, writer);
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    public static string Sha256Hex(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Sha256Hex(document.RootElement);
    }

    private static void Write(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Write(item, writer);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
            default:
                element.WriteTo(writer); // strings, true, false, null
                break;
        }
    }
}
