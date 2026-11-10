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
/// The node catalog is code, not data (plan §4). Types: api-trigger, erp-connector, file-upload and sftp-folder (simulated), validate, generate-pdf, archive.
/// Adding a type here (with a handler in M7) needs no schema change.
/// </summary>
public static class NodeCatalog
{
    public const string ApiTrigger = "api-trigger";
    public const string ErpConnector = "erp-connector";
    public const string FileUpload = "file-upload";
    public const string SftpFolder = "sftp-folder";
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

    /// <summary>How callers of the (mock) webhook authenticate. Descriptive only: nothing listens at the mock path.</summary>
    public static readonly IReadOnlyDictionary<string, string> AuthMethods = new Dictionary<string, string>
    {
        ["none"] = "None",
        ["api-key"] = "API key in header",
        ["hmac-sha256"] = "HMAC SHA-256 signature",
        ["oauth2"] = "OAuth 2.0 bearer token",
        ["mtls"] = "Mutual TLS",
    };

    /// <summary>ERP systems the (simulated) connector names. No ERP integration exists; see the node description.</summary>
    public static readonly IReadOnlyDictionary<string, string> ErpSystems = new Dictionary<string, string>
    {
        ["dynamics-365"] = "Microsoft Dynamics 365",
        ["sap-s4hana"] = "SAP S/4HANA",
        ["oracle-netsuite"] = "Oracle NetSuite",
    };

    public static readonly IReadOnlyDictionary<string, string> UploadSizes = new Dictionary<string, string>
    {
        ["10mb"] = "10 MB",
        ["25mb"] = "25 MB",
        ["100mb"] = "100 MB",
    };

    public static readonly IReadOnlyDictionary<string, string> Uploaders = new Dictionary<string, string>
    {
        ["staff"] = "Signed-in staff",
        ["portal-customers"] = "Portal customers",
        ["anyone-with-link"] = "Anyone with the link",
    };

    public static readonly IReadOnlyDictionary<string, string> SftpPolls = new Dictionary<string, string>
    {
        ["every-minute"] = "Every minute",
        ["every-5-minutes"] = "Every 5 minutes",
        ["hourly"] = "Hourly",
    };

    private const string HostPattern = @"^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$";

    private const string FolderPattern = @"^/[A-Za-z0-9._~/ -]*$";

    private const string FilePatternPattern = @"^[A-Za-z0-9._*?-]*$";

    public static readonly IReadOnlyDictionary<string, string> ErpModes = new Dictionary<string, string>
    {
        ["on-post"] = "When a record is posted",
        ["every-15-minutes"] = "Every 15 minutes",
        ["nightly"] = "Nightly batch",
    };

    private const string DataPathPattern =@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$";

    private const string EndpointPathPattern = @"^/[A-Za-z0-9._~/-]*$";

    /// <summary>The invoice shape the templates and the Validate step use (tests/fixtures/invoice/sample.json).</summary>
    private const string DefaultExpectedSchema = """
        {
          "type": "object",
          "required": ["invoice", "customer", "lines", "totals"],
          "properties": {
            "invoice": {
              "type": "object",
              "required": ["number", "date"],
              "properties": { "number": { "type": "string" }, "date": { "type": "string" }, "dueDate": { "type": "string" }, "currency": { "type": "string" } }
            },
            "customer": {
              "type": "object",
              "required": ["name"],
              "properties": { "name": { "type": "string" }, "email": { "type": "string" }, "vatNumber": { "type": "string" } }
            },
            "lines": { "type": "array" },
            "totals": {
              "type": "object",
              "required": ["gross"],
              "properties": { "net": { "type": "number" }, "vat": { "type": "number" }, "gross": { "type": "number" } }
            }
          }
        }
        """;

    /// <summary>Synthetic sample (fictional company, .example domain).</summary>
    private const string DefaultSamplePayload = """
        {
          "invoice": { "number": "INV-2026-003901", "date": "2026-11-02", "dueDate": "2026-12-02", "currency": "EUR" },
          "customer": { "name": "Kestrel Logistics Ltd", "email": "ap@kestrel-logistics.example", "vatNumber": "CY99887766K" },
          "lines": [ { "description": "Electricity supply, October 2026", "qty": 18450, "unit": "kWh", "unitPrice": 0.2185, "vatRate": 19, "amount": 4031.33 } ],
          "totals": { "net": 4031.33, "vat": 765.95, "gross": 4797.28 }
        }
        """;

    private static readonly PortDto In = new("in", "In", true);

    public static IReadOnlyList<NodeTypeDto> All { get; } =
    [
        Node(ApiTrigger, "API / webhook", "input",
            "Starts a run when a system posts JSON to a URL.",
            [], [new PortDto("out", "Out", true)],
            $$"""
            { "type": "object",
              "properties": {
                "path": { "type": "string", "title": "Path", "minLength": 1, "maxLength": 200, "pattern": "{{Escape(EndpointPathPattern)}}", "x-widget": "endpoint-path",
                  "description": "Mock endpoint for testing. Real runs are submitted to POST /api/v1/workflows/{key}/executions." },
                "auth": { "type": "string", "title": "Authentication method", "enum": {{JsonSerializer.Serialize(AuthMethods.Keys)}}, "x-enumLabels": {{JsonSerializer.Serialize(AuthMethods.Values)}} },
                "expectedSchema": { "type": "string", "title": "Expected JSON schema", "maxLength": 20000, "x-widget": "json" },
                "samplePayload": { "type": "string", "title": "Sample JSON payload", "maxLength": 20000, "x-widget": "json" } },
              "required": ["path", "auth"] }
            """,
            new JsonObject
            {
                ["path"] = "/hooks/invoices",
                ["auth"] = "hmac-sha256",
                ["expectedSchema"] = DefaultExpectedSchema,
                ["samplePayload"] = DefaultSamplePayload,
            }.ToJsonString()),

        Node(ErpConnector, "ERP connector", "input",
            "Reads records from SAP, Dynamics or Oracle. Simulated: no ERP is connected, so runs are submitted to POST /api/v1/workflows/{key}/executions.",
            [], [new PortDto("out", "Out", true)],
            $$"""
            { "type": "object",
              "properties": {
                "system": { "type": "string", "title": "System", "enum": {{JsonSerializer.Serialize(ErpSystems.Keys)}}, "x-enumLabels": {{JsonSerializer.Serialize(ErpSystems.Values)}} },
                "entity": { "type": "string", "title": "Record type", "minLength": 1, "maxLength": 100 },
                "mode": { "type": "string", "title": "Start a run", "enum": {{JsonSerializer.Serialize(ErpModes.Keys)}}, "x-enumLabels": {{JsonSerializer.Serialize(ErpModes.Values)}} } },
              "required": ["system", "entity", "mode"] }
            """,
            """{ "system": "dynamics-365", "entity": "Invoices", "mode": "on-post" }"""),

        Node(FileUpload, "File upload", "input",
            "Someone uploads a file in Verso or the portal. Simulated: there is no upload screen yet, so runs are submitted to POST /api/v1/workflows/{key}/executions.",
            [], [new PortDto("out", "Out", true)],
            $$"""
            { "type": "object",
              "properties": {
                "accept": { "type": "string", "title": "Accepted file types", "minLength": 1, "maxLength": 200 },
                "maxSize": { "type": "string", "title": "Largest file", "enum": {{JsonSerializer.Serialize(UploadSizes.Keys)}}, "x-enumLabels": {{JsonSerializer.Serialize(UploadSizes.Values)}} },
                "who": { "type": "string", "title": "Who can upload", "enum": {{JsonSerializer.Serialize(Uploaders.Keys)}}, "x-enumLabels": {{JsonSerializer.Serialize(Uploaders.Values)}} } },
              "required": ["accept", "maxSize", "who"] }
            """,
            """{ "accept": "PDF, XML, CSV", "maxSize": "25mb", "who": "staff" }"""),

        Node(SftpFolder, "SFTP folder", "input",
            "Picks up files from a watched folder. Simulated: nothing connects to an SFTP server, so runs are submitted to POST /api/v1/workflows/{key}/executions.",
            [], [new PortDto("out", "Out", true)],
            $$"""
            { "type": "object",
              "properties": {
                "host": { "type": "string", "title": "Host", "minLength": 1, "maxLength": 253, "pattern": "{{Escape(HostPattern)}}", "x-widget": "mono",
                  "description": "A host name such as sftp.example.com. No user name, password or URL." },
                "folder": { "type": "string", "title": "Folder", "minLength": 1, "maxLength": 300, "pattern": "{{Escape(FolderPattern)}}", "x-widget": "mono" },
                "pattern": { "type": "string", "title": "File pattern", "maxLength": 100, "pattern": "{{Escape(FilePatternPattern)}}", "x-widget": "mono",
                  "description": "For example *.xml. Leave empty for every file." },
                "poll": { "type": "string", "title": "Check for files", "enum": {{JsonSerializer.Serialize(SftpPolls.Keys)}}, "x-enumLabels": {{JsonSerializer.Serialize(SftpPolls.Values)}} } },
              "required": ["host", "folder", "poll"] }
            """,
            """{ "host": "", "folder": "/inbound", "pattern": "*.xml", "poll": "every-5-minutes" }"""),

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
