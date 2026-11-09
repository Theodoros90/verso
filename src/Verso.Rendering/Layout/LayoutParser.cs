using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Verso.Rendering.Binding;

namespace Verso.Rendering.Layout;

/// <summary>A structural problem in layout JSON. <see cref="Path"/> is relative to the layout root, e.g. <c>body.blocks[2].props.style.color</c>.</summary>
public sealed record LayoutError(string Path, string Message);

/// <summary>
/// Parses and validates layout JSON (model v1). Strict: unknown properties, unknown element types,
/// out-of-range numbers and unsafe values (colors, image sources) are errors, so stored layouts are always renderable.
/// </summary>
public static partial class LayoutParser
{
    public const int SchemaVersion = 1;
    public const int MaxElements = 500;
    public const decimal MaxRegionHeightMm = 150;
    public const decimal MaxCoordinateMm = 420; // A3 long edge; generous for A4 in either orientation
    public const int MaxImageDataUriChars = 700_000; // ≈ 500 KB image
    public const int MaxTextChars = 10_000;

    private static readonly string[] FixedRegionTypes = ["text", "field", "divider", "box", "image", "signature"];
    private static readonly string[] FlowRegionTypes = ["text", "field", "table", "divider", "image", "signature"];

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColorPattern();

    [GeneratedRegex("^data:image/(png|jpeg);base64,([A-Za-z0-9+/]+={0,2})$")]
    private static partial Regex ImageDataUriPattern();

    public static (TemplateLayout? Layout, IReadOnlyList<LayoutError> Errors) Parse(JsonElement root)
    {
        var ctx = new Context();
        if (root.ValueKind != JsonValueKind.Object)
        {
            ctx.Error("", "The layout must be a JSON object.");
            return (null, ctx.Errors);
        }

        ctx.AllowOnly(root, "", "layoutSchemaVersion", "header", "body", "footer");
        if (!root.TryGetProperty("layoutSchemaVersion", out var version) || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var v) || v != SchemaVersion)
        {
            ctx.Error("layoutSchemaVersion", $"Must be {SchemaVersion}.");
        }

        var header = root.TryGetProperty("header", out var h) ? ParseFixedRegion(h, "header", ctx) : FixedRegion.Empty;
        var body = root.TryGetProperty("body", out var b) ? ParseFlowRegion(b, "body", ctx) : FlowRegion.Empty;
        var footer = root.TryGetProperty("footer", out var f) ? ParseFixedRegion(f, "footer", ctx) : FixedRegion.Empty;

        if (ctx.ElementCount > MaxElements)
        {
            ctx.Error("", $"Too many elements ({ctx.ElementCount}); at most {MaxElements}.");
        }

        return ctx.Errors.Count == 0 ? (new TemplateLayout(header, body, footer), ctx.Errors) : (null, ctx.Errors);
    }

    public static (TemplateLayout? Layout, IReadOnlyList<LayoutError> Errors) Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return Parse(document.RootElement);
        }
        catch (JsonException)
        {
            return (null, [new LayoutError("", "Not valid JSON.")]);
        }
    }

    private static FixedRegion ParseFixedRegion(JsonElement region, string path, Context ctx)
    {
        if (!ctx.RequireObject(region, path))
        {
            return FixedRegion.Empty;
        }

        ctx.AllowOnly(region, path, "heightMm", "elements");
        var height = ctx.Number(region, path, "heightMm", 0, MaxRegionHeightMm, required: true) ?? 0;
        var elements = new List<PositionedElement>();
        foreach (var (item, itemPath) in ctx.Array(region, path, "elements"))
        {
            if (!ctx.RequireObject(item, itemPath))
            {
                continue;
            }

            ctx.AllowOnly(item, itemPath, "id", "type", "xMm", "yMm", "widthMm", "heightMm", "props");
            var x = ctx.Number(item, itemPath, "xMm", 0, MaxCoordinateMm, required: true);
            var y = ctx.Number(item, itemPath, "yMm", 0, MaxCoordinateMm, required: true);
            var w = ctx.Number(item, itemPath, "widthMm", 0.1m, MaxCoordinateMm, required: true);
            var hgt = ctx.Number(item, itemPath, "heightMm", 0.1m, MaxCoordinateMm, required: true);
            var element = ParseElement(item, itemPath, FixedRegionTypes, ctx);
            if (element is not null && x is not null && y is not null && w is not null && hgt is not null)
            {
                elements.Add(new PositionedElement(element, x.Value, y.Value, w.Value, hgt.Value));
            }
        }

        return new FixedRegion(height, elements);
    }

    private static FlowRegion ParseFlowRegion(JsonElement region, string path, Context ctx)
    {
        if (!ctx.RequireObject(region, path))
        {
            return FlowRegion.Empty;
        }

        ctx.AllowOnly(region, path, "blocks");
        var blocks = new List<FlowBlock>();
        foreach (var (item, itemPath) in ctx.Array(region, path, "blocks"))
        {
            if (!ctx.RequireObject(item, itemPath))
            {
                continue;
            }

            ctx.AllowOnly(item, itemPath, "id", "type", "spaceBeforeMm", "props");
            var space = ctx.Number(item, itemPath, "spaceBeforeMm", 0, 100, required: false) ?? 0;
            var element = ParseElement(item, itemPath, FlowRegionTypes, ctx);
            if (element is not null)
            {
                blocks.Add(new FlowBlock(element, space));
            }
        }

        return new FlowRegion(blocks);
    }

    private static LayoutElement? ParseElement(JsonElement item, string path, string[] allowedTypes, Context ctx)
    {
        ctx.ElementCount++;
        var id = ctx.String(item, path, "id", required: true, maxLength: 64);
        if (id is not null)
        {
            if (!IdPattern().IsMatch(id))
            {
                ctx.Error($"{path}.id", "Use 1–64 letters, digits, '-' or '_'.");
            }
            else if (!ctx.Ids.Add(id))
            {
                ctx.Error($"{path}.id", $"Duplicate element id '{id}'.");
            }
        }

        var type = ctx.String(item, path, "type", required: true, maxLength: 32);
        if (type is null)
        {
            return null;
        }

        if (!allowedTypes.Contains(type))
        {
            ctx.Error($"{path}.type", $"'{type}' isn't allowed here. Allowed: {string.Join(", ", allowedTypes)}.");
            return null;
        }

        var propsPath = $"{path}.props";
        if (!item.TryGetProperty("props", out var props))
        {
            ctx.Error(propsPath, "Required.");
            return null;
        }

        if (!ctx.RequireObject(props, propsPath))
        {
            return null;
        }

        id ??= "";
        return type switch
        {
            "text" => ParseText(id, props, propsPath, ctx),
            "field" => ParseField(id, props, propsPath, ctx),
            "table" => ParseTable(id, props, propsPath, ctx),
            "divider" => ParseDivider(id, props, propsPath, ctx),
            "box" => ParseBox(id, props, propsPath, ctx),
            "image" => ParseImage(id, props, propsPath, ctx),
            "signature" => ParseSignature(id, props, propsPath, ctx),
            _ => null,
        };
    }

    private static TextElement? ParseText(string id, JsonElement props, string path, Context ctx)
    {
        ctx.AllowOnly(props, path, "text", "style");
        var text = ctx.String(props, path, "text", required: true, maxLength: MaxTextChars, allowEmpty: true);
        var style = ParseStyle(props, path, ctx);
        return text is null ? null : new TextElement(id, text, style);
    }

    private static FieldElement? ParseField(string id, JsonElement props, string path, Context ctx)
    {
        ctx.AllowOnly(props, path, "binding", "filter", "label", "style");
        var binding = ctx.Binding(props, path, "binding");
        var filter = ctx.Filter(props, path, "filter");
        var label = ctx.String(props, path, "label", required: false, maxLength: 200, allowEmpty: true);
        var style = ParseStyle(props, path, ctx);
        return binding is null ? null : new FieldElement(id, binding, filter, label, style);
    }

    private static TableElement? ParseTable(string id, JsonElement props, string path, Context ctx)
    {
        ctx.AllowOnly(props, path, "binding", "columns", "emptyText", "style");
        var binding = ctx.Binding(props, path, "binding");
        var emptyText = ctx.String(props, path, "emptyText", required: false, maxLength: 200, allowEmpty: true);
        var style = ParseStyle(props, path, ctx);

        var columns = new List<TableColumn>();
        foreach (var (col, colPath) in ctx.Array(props, path, "columns"))
        {
            if (!ctx.RequireObject(col, colPath))
            {
                continue;
            }

            ctx.AllowOnly(col, colPath, "header", "binding", "filter", "align", "widthPct");
            var header = ctx.String(col, colPath, "header", required: true, maxLength: 100, allowEmpty: true);
            var colBinding = ctx.Binding(col, colPath, "binding");
            var filter = ctx.Filter(col, colPath, "filter");
            var align = ctx.Enum(col, colPath, "align", TextAlign.Left);
            var width = ctx.Number(col, colPath, "widthPct", 1, 100, required: false);
            if (header is not null && colBinding is not null)
            {
                columns.Add(new TableColumn(header, colBinding, filter, align, width));
            }
        }

        if (columns.Count == 0 && !ctx.HasErrorUnder($"{path}.columns"))
        {
            ctx.Error($"{path}.columns", "Add at least one column.");
        }

        if (columns.Sum(c => c.WidthPct ?? 0) > 100)
        {
            ctx.Error($"{path}.columns", "Column widths add up to more than 100%.");
        }

        return binding is null || columns.Count == 0 ? null : new TableElement(id, binding, columns, emptyText, style);
    }

    private static DividerElement ParseDivider(string id, JsonElement props, string path, Context ctx)
    {
        ctx.AllowOnly(props, path, "thicknessPt", "color");
        var thickness = ctx.Number(props, path, "thicknessPt", 0.25m, 6, required: false) ?? 0.75m;
        var color = ctx.Color(props, path, "color") ?? "#1A232C";
        return new DividerElement(id, thickness, color);
    }

    private static BoxElement ParseBox(string id, JsonElement props, string path, Context ctx)
    {
        ctx.AllowOnly(props, path, "borderColor", "fill", "radiusMm");
        var border = ctx.Color(props, path, "borderColor") ?? "#C4CDD6";
        var fill = ctx.Color(props, path, "fill");
        var radius = ctx.Number(props, path, "radiusMm", 0, 20, required: false) ?? 1;
        return new BoxElement(id, border, fill, radius);
    }

    private static ImageElement? ParseImage(string id, JsonElement props, string path, Context ctx)
    {
        ctx.AllowOnly(props, path, "src", "alt", "fit");
        var src = ctx.String(props, path, "src", required: true, maxLength: MaxImageDataUriChars);
        var alt = ctx.String(props, path, "alt", required: false, maxLength: 200, allowEmpty: true) ?? "";
        var fit = ctx.Enum(props, path, "fit", ImageFit.Contain);
        if (src is null)
        {
            return null;
        }

        var match = ImageDataUriPattern().Match(src);
        if (!match.Success || !IsBase64(match.Groups[2].Value))
        {
            ctx.Error($"{path}.src", "Must be an embedded PNG or JPEG: data:image/png;base64,… or data:image/jpeg;base64,… (external URLs aren't allowed).");
            return null;
        }

        return new ImageElement(id, src, alt, fit);

        static bool IsBase64(string s)
        {
            var buffer = new byte[s.Length];
            return s.Length % 4 == 0 && Convert.TryFromBase64String(s, buffer, out _);
        }
    }

    private static SignatureElement ParseSignature(string id, JsonElement props, string path, Context ctx)
    {
        ctx.AllowOnly(props, path, "label");
        var label = ctx.String(props, path, "label", required: false, maxLength: 200, allowEmpty: true) ?? "Authorised signatory";
        return new SignatureElement(id, label);
    }

    private static TextStyle ParseStyle(JsonElement props, string path, Context ctx)
    {
        if (!props.TryGetProperty("style", out var style))
        {
            return TextStyle.Default;
        }

        var stylePath = $"{path}.style";
        if (!ctx.RequireObject(style, stylePath))
        {
            return TextStyle.Default;
        }

        ctx.AllowOnly(style, stylePath, "fontSizePt", "bold", "italic", "align", "color", "font");
        var d = TextStyle.Default;
        return new TextStyle(
            ctx.Number(style, stylePath, "fontSizePt", 5, 48, required: false) ?? d.FontSizePt,
            ctx.Bool(style, stylePath, "bold") ?? d.Bold,
            ctx.Bool(style, stylePath, "italic") ?? d.Italic,
            ctx.Enum(style, stylePath, "align", d.Align),
            ctx.Color(style, stylePath, "color") ?? d.Color,
            ctx.Enum(style, stylePath, "font", d.Font));
    }

    /// <summary>Collects errors and the set of element ids while parsing.</summary>
    private sealed class Context
    {
        public List<LayoutError> Errors { get; } = [];
        public HashSet<string> Ids { get; } = new(StringComparer.Ordinal);
        public int ElementCount { get; set; }

        public void Error(string path, string message) => Errors.Add(new LayoutError(path, message));

        public bool HasErrorUnder(string path) => Errors.Any(e => e.Path.StartsWith(path, StringComparison.Ordinal));

        public bool RequireObject(JsonElement e, string path)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                return true;
            }

            Error(path, "Must be a JSON object.");
            return false;
        }

        public void AllowOnly(JsonElement obj, string path, params string[] allowed)
        {
            foreach (var p in obj.EnumerateObject())
            {
                if (!allowed.Contains(p.Name, StringComparer.Ordinal))
                {
                    Error(Join(path, p.Name), $"Unknown property. Allowed: {string.Join(", ", allowed)}.");
                }
            }
        }

        public List<(JsonElement Item, string Path)> Array(JsonElement obj, string path, string name)
        {
            var arrayPath = Join(path, name);
            if (!obj.TryGetProperty(name, out var array))
            {
                return [];
            }

            if (array.ValueKind != JsonValueKind.Array)
            {
                Error(arrayPath, "Must be an array.");
                return [];
            }

            return array.EnumerateArray().Select((item, i) => (item, $"{arrayPath}[{i}]")).ToList();
        }

        public decimal? Number(JsonElement obj, string path, string name, decimal min, decimal max, bool required)
        {
            var p = Join(path, name);
            if (!obj.TryGetProperty(name, out var e))
            {
                if (required)
                {
                    Error(p, "Required.");
                }

                return null;
            }

            if (e.ValueKind != JsonValueKind.Number || !e.TryGetDecimal(out var value) || value < min || value > max)
            {
                Error(p, string.Create(CultureInfo.InvariantCulture, $"Must be a number from {min} to {max}."));
                return null;
            }

            return value;
        }

        public string? String(JsonElement obj, string path, string name, bool required, int maxLength, bool allowEmpty = false)
        {
            var p = Join(path, name);
            if (!obj.TryGetProperty(name, out var e))
            {
                if (required)
                {
                    Error(p, "Required.");
                }

                return null;
            }

            if (e.ValueKind != JsonValueKind.String)
            {
                Error(p, "Must be a string.");
                return null;
            }

            var s = e.GetString()!;
            if ((!allowEmpty && s.Length == 0) || s.Length > maxLength)
            {
                Error(p, allowEmpty ? $"At most {maxLength} characters." : $"1–{maxLength} characters.");
                return null;
            }

            return s;
        }

        public bool? Bool(JsonElement obj, string path, string name)
        {
            if (!obj.TryGetProperty(name, out var e))
            {
                return null;
            }

            if (e.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return e.GetBoolean();
            }

            Error(Join(path, name), "Must be true or false.");
            return null;
        }

        public TEnum Enum<TEnum>(JsonElement obj, string path, string name, TEnum fallback)
            where TEnum : struct, System.Enum
        {
            var s = String(obj, path, name, required: false, maxLength: 32);
            if (s is null)
            {
                return fallback;
            }

            // JSON uses lower case ("right", "serif"); enum names are PascalCase. Only exact lower-case spellings are accepted.
            var match = s.All(char.IsLower)
                ? System.Enum.GetNames<TEnum>().FirstOrDefault(n => string.Equals(n, s, StringComparison.OrdinalIgnoreCase))
                : null;
            if (match is null)
            {
                Error(Join(path, name), $"Use one of: {string.Join(", ", System.Enum.GetNames<TEnum>().Select(LowerCase))}.");
                return fallback;
            }

            return System.Enum.Parse<TEnum>(match);
        }

        public string? Color(JsonElement obj, string path, string name)
        {
            var s = String(obj, path, name, required: false, maxLength: 7);
            if (s is not null && !ColorPattern().IsMatch(s))
            {
                Error(Join(path, name), "Use a hex color like #1A232C.");
                return null;
            }

            return s;
        }

        public string? Binding(JsonElement obj, string path, string name)
        {
            var s = String(obj, path, name, required: true, maxLength: 200);
            if (s is not null && !DataPath.IsValid(s))
            {
                Error(Join(path, name), "Use a data path like invoice.number (letters, digits and _, separated by dots).");
                return null;
            }

            return s;
        }

        public string? Filter(JsonElement obj, string path, string name)
        {
            var s = String(obj, path, name, required: false, maxLength: 32);
            if (s is not null && !Filters.IsKnown(s))
            {
                Error(Join(path, name), $"Unknown filter. Use one of: {string.Join(", ", Filters.Names)}.");
                return null;
            }

            return s;
        }

        private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

        private static string LowerCase(string enumName) => string.Concat(enumName.Select(char.ToLowerInvariant));
    }
}
