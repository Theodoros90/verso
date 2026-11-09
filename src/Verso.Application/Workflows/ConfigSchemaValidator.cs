using System.Text.Json;
using System.Text.RegularExpressions;

namespace Verso.Application.Workflows;

/// <summary>
/// Validates node settings against the node's config schema. Supports only the JSON Schema subset the catalog
/// uses: <c>type</c> (object, array, string), <c>properties</c>, <c>required</c>, <c>items</c>, <c>minItems</c>,
/// <c>maxItems</c>, <c>minLength</c>, <c>maxLength</c>, <c>pattern</c>, <c>enum</c>, <c>format: uuid</c>.
/// Unknown properties are errors (settings round-trip exactly; nothing hides in them).
/// The schemas are our own code, so a general-purpose JSON Schema library isn't needed.
/// </summary>
public static class ConfigSchemaValidator
{
    public sealed record ConfigError(string Field, string Code, string Message);

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static IReadOnlyList<ConfigError> Validate(JsonElement schema, JsonElement value)
    {
        var errors = new List<ConfigError>();
        Check(schema, value, "", errors);
        return errors;
    }

    private static void Check(JsonElement schema, JsonElement value, string path, List<ConfigError> errors)
    {
        var title = schema.TryGetProperty("title", out var t) ? t.GetString() ?? path : path;
        switch (schema.GetProperty("type").GetString())
        {
            case "object":
                if (value.ValueKind != JsonValueKind.Object)
                {
                    errors.Add(new(path, "config.invalid", $"{Label(title)} must be an object."));
                    return;
                }

                var properties = schema.TryGetProperty("properties", out var p) ? p : default;
                foreach (var property in value.EnumerateObject())
                {
                    if (properties.ValueKind != JsonValueKind.Object || !properties.TryGetProperty(property.Name, out _))
                    {
                        errors.Add(new(Join(path, property.Name), "config.unknown", $"Unknown setting '{property.Name}'."));
                    }
                }

                if (schema.TryGetProperty("required", out var required))
                {
                    foreach (var name in required.EnumerateArray().Select(r => r.GetString()!))
                    {
                        if (!value.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null
                            || (v.ValueKind == JsonValueKind.String && v.GetString()!.Trim().Length == 0))
                        {
                            var propTitle = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var ps) && ps.TryGetProperty("title", out var pt)
                                ? pt.GetString()! : name;
                            errors.Add(new(Join(path, name), "config.required", $"{propTitle} is required."));
                        }
                    }
                }

                if (properties.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in properties.EnumerateObject())
                    {
                        if (value.TryGetProperty(property.Name, out var v) && v.ValueKind != JsonValueKind.Null
                            && !(v.ValueKind == JsonValueKind.String && v.GetString()!.Trim().Length == 0))
                        {
                            Check(property.Value, v, Join(path, property.Name), errors);
                        }
                    }
                }

                break;

            case "array":
                if (value.ValueKind != JsonValueKind.Array)
                {
                    errors.Add(new(path, "config.invalid", $"{Label(title)} must be a list."));
                    return;
                }

                var count = value.GetArrayLength();
                if (schema.TryGetProperty("minItems", out var min) && count < min.GetInt32())
                {
                    errors.Add(new(path, "config.invalid", $"{Label(title)}: add at least {min.GetInt32()}."));
                }

                if (schema.TryGetProperty("maxItems", out var max) && count > max.GetInt32())
                {
                    errors.Add(new(path, "config.invalid", $"{Label(title)}: at most {max.GetInt32()}."));
                }

                if (schema.TryGetProperty("items", out var items))
                {
                    var i = 0;
                    foreach (var item in value.EnumerateArray())
                    {
                        var itemPath = $"{path}[{i++}]";
                        if (items.GetProperty("type").GetString() == "string" && (item.ValueKind != JsonValueKind.String || item.GetString()!.Trim().Length == 0))
                        {
                            errors.Add(new(itemPath, "config.required", $"{Label(title)} {i}: enter a value."));
                            continue;
                        }

                        Check(items, item, itemPath, errors);
                    }
                }

                break;

            case "string":
                if (value.ValueKind != JsonValueKind.String)
                {
                    errors.Add(new(path, "config.invalid", $"{Label(title)} must be text."));
                    return;
                }

                var s = value.GetString()!;
                if (schema.TryGetProperty("minLength", out var minLength) && s.Length < minLength.GetInt32())
                {
                    errors.Add(new(path, "config.required", $"{Label(title)} is required."));
                }

                if (schema.TryGetProperty("maxLength", out var maxLength) && s.Length > maxLength.GetInt32())
                {
                    errors.Add(new(path, "config.invalid", $"{Label(title)}: at most {maxLength.GetInt32()} characters."));
                }

                if (schema.TryGetProperty("enum", out var allowed) && !allowed.EnumerateArray().Any(a => a.GetString() == s))
                {
                    errors.Add(new(path, "config.invalid", $"{Label(title)}: choose one of the listed values."));
                }

                if (schema.TryGetProperty("format", out var format) && format.GetString() == "uuid" && !Guid.TryParse(s, out _))
                {
                    errors.Add(new(path, "config.invalid", $"{Label(title)}: not a valid id."));
                }

                if (schema.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(s, pattern.GetString()!, RegexOptions.None, RegexTimeout))
                {
                    errors.Add(new(path, "config.invalid", $"{Label(title)}: '{Shorten(s)}' isn't valid here."));
                }

                break;
        }
    }

    private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    private static string Label(string title) => title.Length == 0 ? "Settings" : title;

    private static string Shorten(string s) => s.Length > 40 ? s[..40] + "…" : s;
}
