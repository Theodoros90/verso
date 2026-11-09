using System.Text.Json;
using System.Text.RegularExpressions;
using Verso.Domain.Templates;

namespace Verso.Application.Templates;

/// <summary>Input rules for template requests. Error keys are camelCase JSON paths of the offending field.</summary>
public static partial class TemplateValidation
{
    public const int CurrentLayoutSchemaVersion = 1;
    public const int MaxLayoutChars = 1_000_000;
    public const int MaxDataSchemaChars = 256_000;
    public const int MaxSampleDataChars = 256_000;
    public const decimal MaxMarginMm = 50;

    [GeneratedRegex("^[A-Z][A-Z0-9-]{1,31}$")]
    private static partial Regex KeyPattern();

    public static IReadOnlyDictionary<string, string[]>? ValidateCreate(CreateTemplateRequest request, out DocumentType documentType)
    {
        var errors = new Dictionary<string, string[]>();
        documentType = DocumentType.Invoice;

        if (string.IsNullOrWhiteSpace(request.Key) || !KeyPattern().IsMatch(request.Key))
        {
            errors["key"] = ["Use 2–32 characters: capital letters, digits and hyphens, starting with a letter (e.g. INV-STD)."];
        }

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)
        {
            errors["name"] = ["A name of 1–200 characters is required."];
        }

        if (request.Description is { Length: > 1000 })
        {
            errors["description"] = ["Use at most 1000 characters."];
        }

        if (request.DocumentType is not null && !Enum.TryParse(request.DocumentType, ignoreCase: false, out documentType))
        {
            errors["documentType"] = [$"Supported values: {string.Join(", ", Enum.GetNames<DocumentType>())}."];
        }

        return errors.Count == 0 ? null : errors;
    }

    public static IReadOnlyDictionary<string, string[]>? ValidateContent(SaveDraftRequest request, out TemplateContent? content)
    {
        var errors = new Dictionary<string, string[]>();
        content = null;

        var page = ValidatePageSettings(request.PageSettings, errors);
        var layout = RequireObject(request.Layout, "layout", MaxLayoutChars, errors);
        var dataSchema = RequireObject(request.DataSchema, "dataSchema", MaxDataSchemaChars, errors);
        var sampleData = RequireObject(request.SampleData, "sampleData", MaxSampleDataChars, errors);

        if (request.Layout is { ValueKind: JsonValueKind.Object } layoutElement)
        {
            if (!layoutElement.TryGetProperty("layoutSchemaVersion", out var v)
                || v.ValueKind != JsonValueKind.Number
                || !v.TryGetInt32(out var version)
                || version != CurrentLayoutSchemaVersion)
            {
                errors["layout.layoutSchemaVersion"] = [$"Must be {CurrentLayoutSchemaVersion}."];
            }
        }

        if (errors.Count > 0 || page is null || layout is null || dataSchema is null || sampleData is null)
        {
            return errors;
        }

        content = new TemplateContent(page, layout, dataSchema, sampleData);
        return null;
    }

    private static PageSettings? ValidatePageSettings(PageSettingsDto? dto, Dictionary<string, string[]> errors)
    {
        if (dto is null)
        {
            errors["pageSettings"] = ["Required."];
            return null;
        }

        var before = errors.Count;
        if (dto.Size != "A4")
        {
            errors["pageSettings.size"] = ["Only A4 is supported."];
        }

        if (dto.Orientation is not ("portrait" or "landscape"))
        {
            errors["pageSettings.orientation"] = ["Use portrait or landscape."];
        }

        if (dto.MarginsMm is null)
        {
            errors["pageSettings.marginsMm"] = ["Required."];
        }
        else
        {
            var m = dto.MarginsMm;
            foreach (var (name, value) in new[] { ("top", m.Top), ("right", m.Right), ("bottom", m.Bottom), ("left", m.Left) })
            {
                if (value is < 0 or > MaxMarginMm)
                {
                    errors[$"pageSettings.marginsMm.{name}"] = [$"Must be between 0 and {MaxMarginMm} mm."];
                }
            }
        }

        return errors.Count > before ? null : new PageSettings(dto.Size!, dto.Orientation!, dto.MarginsMm!.Top, dto.MarginsMm.Right, dto.MarginsMm.Bottom, dto.MarginsMm.Left);
    }

    private static string? RequireObject(JsonElement? element, string field, int maxChars, Dictionary<string, string[]> errors)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj)
        {
            errors[field] = ["Required, and must be a JSON object."];
            return null;
        }

        var raw = obj.GetRawText();
        if (raw.Length > maxChars)
        {
            errors[field] = [$"Too large: at most {maxChars:N0} characters."];
            return null;
        }

        return raw;
    }
}
