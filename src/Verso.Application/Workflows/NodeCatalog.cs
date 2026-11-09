using System.Text.Json;
using System.Text.Json.Nodes;

namespace Verso.Application.Workflows;

public sealed record PortDto(string Id, string Label, bool Required);

/// <summary>
/// One node type. <see cref="ConfigSchema"/> is a JSON Schema subset (see <see cref="ConfigSchemaValidator"/>);
/// <c>x-widget</c> and <c>x-enumLabels</c> are hints for the designer's generated settings panel.
/// </summary>
public sealed record NodeTypeDto(
    string Type,
    string Label,
    string Category,
    string Description,
    IReadOnlyList<PortDto> Inputs,
    IReadOnlyList<PortDto> Outputs,
    JsonElement ConfigSchema,
    JsonElement Defaults);

/// <summary>
/// The node catalog is code, not data (plan §4). MVP types: api-trigger, validate, generate-pdf, archive.
/// Adding a type here (with a handler in M7) needs no schema change.
/// </summary>
public static class NodeCatalog
{
    public const string ApiTrigger = "api-trigger";
    public const string Validate = "validate";
    public const string GeneratePdf = "generate-pdf";
    public const string Archive = "archive";

    public const string LatestPublished = "latest-published";

    /// <summary>Field types the Validate node can check (labels as in the prototype).</summary>
    public static readonly IReadOnlyDictionary<string, string> FieldTypes = new Dictionary<string, string>
    {
        ["text"] = "Text",
        ["number"] = "Number",
        ["date"] = "Date (YYYY-MM-DD)",
        ["email"] = "Email address",
        ["eu-vat"] = "EU VAT number",
        ["iban"] = "IBAN",
    };

    public static readonly IReadOnlyDictionary<string, string> Retention = new Dictionary<string, string>
    {
        ["7y"] = "7 years",
        ["10y"] = "10 years",
        ["permanent"] = "Permanently",
    };

    private const string DataPathPattern = @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$";

    private static readonly PortDto In = new("in", "In", true);

    public static IReadOnlyList<NodeTypeDto> All { get; } =
    [
        Node(ApiTrigger, "API trigger", "input",
            "Starts a run when invoice JSON is submitted to POST /api/v1/workflows/{key}/executions.",
            [], [new PortDto("out", "Out", true)],
            """{ "type": "object", "properties": {} }""",
            "{}"),

        Node(Validate, "Validate data", "process",
            "Checks required fields and value types. Records that pass leave by 'valid', the others by 'invalid'.",
            [In], [new PortDto("valid", "Valid", true), new PortDto("invalid", "Invalid", false)],
            $$"""
            { "type": "object",
              "properties": {
                "required": { "type": "array", "title": "Required fields", "maxItems": 100,
                  "items": { "type": "string", "title": "Field", "pattern": "{{Escape(DataPathPattern)}}", "maxLength": 200, "x-widget": "data-path" } },
                "types": { "type": "array", "title": "Field type checks", "maxItems": 100,
                  "items": { "type": "object", "required": ["field", "type"],
                    "properties": {
                      "field": { "type": "string", "title": "Field", "pattern": "{{Escape(DataPathPattern)}}", "maxLength": 200, "x-widget": "data-path" },
                      "type": { "type": "string", "title": "Expected type", "enum": {{JsonSerializer.Serialize(FieldTypes.Keys)}}, "x-enumLabels": {{JsonSerializer.Serialize(FieldTypes.Values)}} } } } } },
              "required": ["required", "types"] }
            """,
            """{ "required": [], "types": [] }"""),

        Node(GeneratePdf, "Generate PDF", "doc",
            "Renders a published template version as PDF with the run's data.",
            [In], [new PortDto("success", "Success", true), new PortDto("failed", "Failed", false)],
            $$$"""
            { "type": "object",
              "properties": {
                "templateId": { "type": "string", "title": "Template", "format": "uuid", "x-widget": "template" },
                "templateVersion": { "type": "string", "title": "Template version", "pattern": "^({{{LatestPublished}}}|[1-9][0-9]{0,5})$", "x-widget": "template-version",
                  "description": "'latest-published' is resolved to a specific version when a run is submitted." },
                "fileName": { "type": "string", "title": "Output filename pattern", "minLength": 1, "maxLength": 200, "x-widget": "pattern",
                  "description": "Use fields such as {{invoice.number}}." } },
              "required": ["templateId", "templateVersion", "fileName"] }
            """,
            """{ "templateId": "", "templateVersion": "latest-published", "fileName": "{{invoice.number}}.pdf" }"""),

        Node(Archive, "Archive", "output",
            "Stores the document in the Verso archive with metadata and a retention period.",
            [In], [],
            $$"""
            { "type": "object",
              "properties": {
                "folderPattern": { "type": "string", "title": "Folder pattern", "minLength": 1, "maxLength": 300, "x-widget": "pattern",
                  "description": "Filters: |year, |month, |day." },
                "retention": { "type": "string", "title": "Keep for", "enum": {{JsonSerializer.Serialize(Retention.Keys)}}, "x-enumLabels": {{JsonSerializer.Serialize(Retention.Values)}} },
                "metadata": { "type": "array", "title": "Metadata", "maxItems": 20,
                  "items": { "type": "object", "required": ["key", "value"],
                    "properties": {
                      "key": { "type": "string", "title": "Key", "pattern": "^[A-Za-z][A-Za-z0-9]{0,63}$" },
                      "value": { "type": "string", "title": "Value", "maxLength": 300, "x-widget": "pattern" } } } } },
              "required": ["folderPattern", "retention", "metadata"] }
            """,
            """
            { "folderPattern": "invoices/{{invoice.date|year}}/{{invoice.date|month}}", "retention": "7y",
              "metadata": [ { "key": "InvoiceNumber", "value": "{{invoice.number}}" }, { "key": "Customer", "value": "{{customer.name}}" } ] }
            """),
    ];

    public static NodeTypeDto? Find(string type) => All.FirstOrDefault(t => t.Type == type);

    private static NodeTypeDto Node(string type, string label, string category, string description,
        IReadOnlyList<PortDto> inputs, IReadOnlyList<PortDto> outputs, string schema, string defaults) =>
        new(type, label, category, description, inputs, outputs, Parse(schema), Parse(defaults));

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string Escape(string pattern) => JsonValue.Create(pattern)!.ToJsonString()[1..^1];
}
