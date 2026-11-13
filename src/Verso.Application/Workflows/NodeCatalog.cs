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
/// The node catalog is code, not data (plan §4). Types: api-trigger, erp-connector, file-upload, sftp-folder, scheduled-trigger and manual-trigger (simulated),
/// folder-input (real: the Worker reads approved folders, see Ingestion), parse (real: JSON/XML/CSV text in a field of the data), map-fields (real: copies and transforms fields), transform (real: calculates and reshapes values with fixed rules), merge-data (real: joins the data with a lookup table typed into the step), if-else (real: sends the run down a yes or no path by comparing one field), switch (real: routes the run by a field's value into one of several branches), for-each (real: repeats the steps after it once per item of a list), approval (real: pauses the run until a person approves or declines, ADR 0002), validate, generate-pdf, generate-docx (real, ADR 0003), generate-email (real: an HTML email body built from the step's own text, ADR 0004; never sent), combine-pdfs (real: joins the PDFs the run generated into one, ADR 0005), send-email (real: SMTP through the relay in the Worker's configuration, ADR 0006), print-queue (real: PDF and job file into a configured spool folder, ADR 0007), archive.
/// Adding a type here (with a handler in M7) needs no schema change.
/// </summary>
public static class NodeCatalog
{
    public const string ApiTrigger = "api-trigger";
    public const string ErpConnector = "erp-connector";
    public const string FileUpload = "file-upload";
    public const string SftpFolder = "sftp-folder";
    public const string ScheduledTrigger = "scheduled-trigger";
    public const string ManualTrigger = "manual-trigger";
    public const string FolderInput = "folder-input";
    public const string ParseContent = "parse";
    public const string MapFields = "map-fields";
    public const string TransformData = "transform";
    public const string MergeData = "merge-data";
    public const string IfElse = "if-else";
    public const string SwitchRoute = "switch";
    public const string ForEach = "for-each";
    public const string ApprovalStep = "approval";
    public const string Validate = "validate";
    public const string GeneratePdf = "generate-pdf";
    public const string GenerateDocx = "generate-docx";
    public const string GenerateEmail = "generate-email";
    public const string CombinePdfs = "combine-pdfs";
    public const string SendEmail = "send-email";
    public const string PrintQueue = "print-queue";
    public const string UploadSftp = "upload-sftp";
    public const string DeliverApi = "deliver-api";
    public const string Archive = "archive";

    public const string LatestPublished = "latest-published";

    /// <summary>The steps that render a template version into a document (PDF or Word).</summary>
    public static bool IsDocumentStep(string type) => type is GeneratePdf or GenerateDocx;

    /// <summary>Every step that leaves a document for a later Archive step: the template steps and the HTML email (which needs no template).</summary>
    public static bool ProducesDocument(string type) => IsDocumentStep(type) || type is GenerateEmail or CombinePdfs;

    /// <summary>What a Send email step attaches: nothing, or the run's current document.</summary>
    public static readonly IReadOnlyDictionary<string, string> EmailAttach = new Dictionary<string, string>
    {
        ["none"] = "Nothing",
        ["document"] = "The generated document",
    };

    /// <summary>Page order of a Combine PDFs step: stored keys and labels.</summary>
    public static readonly IReadOnlyDictionary<string, string> CombineOrders = new Dictionary<string, string>
    {
        ["generated"] = "In the order generated",
        ["reverse"] = "Last generated first",
    };

    /// <summary>Presets of the Generate HTML email step: stored keys and their labels.</summary>
    public static readonly IReadOnlyDictionary<string, string> EmailLayouts = new Dictionary<string, string>
    {
        ["invoice"] = "Invoice email",
        ["statement"] = "Statement ready",
        ["reminder"] = "Payment reminder",
    };

    /// <summary>Word's editing restriction for a Generate DOCX step (advisory: no password). Keys are stored in the workflow; labels as in the prototype.</summary>
    public static readonly IReadOnlyDictionary<string, string> DocxEditing = new Dictionary<string, string>
    {
        ["allow"] = "Allow edits",
        ["trackedChanges"] = "Tracked changes only",
        ["readOnly"] = "Read only",
    };

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

    /// <summary>An https address: a host, an optional port and path. No user name or password, no query string or fragment.</summary>
    private const string ApiUrlPattern = @"^https://[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?(:[0-9]{1,5})?(/[A-Za-z0-9._~/%-]*)?$";

    private const string FolderPattern =@"^/[A-Za-z0-9._~/ -]*$";

    private const string FilePatternPattern = @"^[A-Za-z0-9._*?-]*$";

    public static readonly IReadOnlyDictionary<string, string> TimeZones = new Dictionary<string, string>
    {
        ["Europe/Nicosia"] = "Europe/Nicosia",
        ["UTC"] = "UTC",
        ["Europe/Luxembourg"] = "Europe/Luxembourg",
    };

    /// <summary>Five space-separated cron fields (minute hour day-of-month month day-of-week).</summary>
    private const string CronPattern = @"^[0-9A-Za-z*/,?#-]+( [0-9A-Za-z*/,?#-]+){4}$";

    /// <summary>Who may start a manual run (descriptive only: the step records the intent, nothing enforces it yet).</summary>
    public static readonly IReadOnlyDictionary<string, string> ManualStarters = new Dictionary<string, string>
    {
        ["template-leads"] = "Template leads",
        ["credit-control"] = "Credit control team",
        ["workspace"] = "Anyone in the workspace",
    };

    /// <summary>Scan intervals of the folder-input trigger (values in <see cref="Ingestion.FolderPaths.Intervals"/>).</summary>
    public static readonly IReadOnlyDictionary<string, string> FolderScanIntervals = new Dictionary<string, string>
    {
        ["every-minute"] = "Every minute",
        ["every-5-minutes"] = "Every 5 minutes",
        ["every-15-minutes"] = "Every 15 minutes",
        ["hourly"] = "Hourly",
    };

    public static readonly IReadOnlyDictionary<string, string> ErpModes = new Dictionary<string, string>
    {
        ["on-post"] = "When a record is posted",
        ["every-15-minutes"] = "Every 15 minutes",
        ["nightly"] = "Nightly batch",
    };

    private const string DataPathPattern =@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$";

    private const string OptionalDataPathPattern = @"^([A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*)?$";

    /// <summary>A number or a data path (or empty, for settings an operation doesn't use).</summary>
    private const string OperandPattern = @"^(-?[0-9]+(\.[0-9]+)?|[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*)?$";

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

        Node(ScheduledTrigger, "Scheduled trigger", "input",
            "Runs at fixed times. Simulated: nothing in Verso runs on a schedule yet, so runs are submitted to POST /api/v1/workflows/{key}/executions.",
            [], [new PortDto("out", "Out", true)],
            $$"""
            { "type": "object",
              "properties": {
                "cron": { "type": "string", "title": "Cron expression", "minLength": 1, "maxLength": 100, "pattern": "{{Escape(CronPattern)}}", "x-widget": "mono",
                  "description": "Five fields: minute, hour, day of month, month, day of week. For example 0 1 * * * runs every night at 01:00." },
                "tz": { "type": "string", "title": "Time zone", "enum": {{JsonSerializer.Serialize(TimeZones.Keys)}}, "x-enumLabels": {{JsonSerializer.Serialize(TimeZones.Values)}} } },
              "required": ["cron", "tz"] }
            """,
            """{ "cron": "0 1 * * *", "tz": "Europe/Nicosia" }"""),

        Node(ManualTrigger, "Manual trigger", "input",
            "Someone starts a run from Verso. Simulated: there is no Run button yet, so runs are submitted to POST /api/v1/workflows/{key}/executions.",
            [], [new PortDto("out", "Out", true)],
            $$"""
            { "type": "object",
              "properties": {
                "who": { "type": "string", "title": "Who can start it", "enum": {{JsonSerializer.Serialize(ManualStarters.Keys)}}, "x-enumLabels": {{JsonSerializer.Serialize(ManualStarters.Values)}} },
                "ask": { "type": "string", "title": "Ask for", "maxLength": 100,
                  "description": "What the person is asked to enter when they start a run, such as an invoice number. Optional." } },
              "required": ["who"] }
            """,
            """{ "who": "template-leads", "ask": "Invoice number" }"""),

        Node(FolderInput, "Folder input", "input",
            "Picks up files from a folder on the Worker's server or a network share (type its path), starts a run for each, and moves the file to the processed or failed folder. JSON files only in this version.",
            [], [new PortDto("out", "Out", true)],
            $$"""
            { "type": "object",
              "properties": {
                "sourcePath": { "type": "string", "title": "Source folder", "maxLength": {{Ingestion.FolderPaths.MaxPathLength}}, "x-widget": "folder-path",
                  "description": "Absolute path as the Worker's server sees it, such as D:\\Invoices\\in, \\\\fileserver\\share\\in or /mnt/verso/in. The Worker's service account needs Modify access." },
                "pattern": { "type": "string", "title": "File pattern", "minLength": 1, "maxLength": 100, "pattern": "{{Escape(Ingestion.FolderPaths.FilePatternPattern)}}", "x-widget": "mono",
                  "description": "For example *.json. Senders write under a temporary name (such as .tmp) and rename when the file is complete." },
                "includeSubfolders": { "type": "boolean", "title": "Include subfolders" },
                "interval": { "type": "string", "title": "Scan interval", "enum": {{JsonSerializer.Serialize(FolderScanIntervals.Keys)}}, "x-enumLabels": {{JsonSerializer.Serialize(FolderScanIntervals.Values)}} },
                "processedPath": { "type": "string", "title": "Processed files go to", "maxLength": {{Ingestion.FolderPaths.MaxPathLength}}, "x-widget": "folder-path",
                  "description": "Must not be inside the source folder when subfolders are included, or the files would be picked up again." },
                "failedPath": { "type": "string", "title": "Failed files go to", "maxLength": {{Ingestion.FolderPaths.MaxPathLength}}, "x-widget": "folder-path",
                  "description": "An error file (name.error.json) explains each failure." },
                "sourceLocation": { "type": "string", "maxLength": 64, "pattern": "{{Escape(Ingestion.FolderPaths.LocationKeyPattern)}}", "x-widget": "legacy" },
                "sourceSubfolder": { "type": "string", "maxLength": {{Ingestion.FolderPaths.MaxSubfolderLength}}, "pattern": "{{Escape(Ingestion.FolderPaths.SubfolderPattern)}}", "x-widget": "legacy" },
                "processedLocation": { "type": "string", "maxLength": 64, "pattern": "{{Escape(Ingestion.FolderPaths.LocationKeyPattern)}}", "x-widget": "legacy" },
                "processedSubfolder": { "type": "string", "maxLength": {{Ingestion.FolderPaths.MaxSubfolderLength}}, "pattern": "{{Escape(Ingestion.FolderPaths.SubfolderPattern)}}", "x-widget": "legacy" },
                "failedLocation": { "type": "string", "maxLength": 64, "pattern": "{{Escape(Ingestion.FolderPaths.LocationKeyPattern)}}", "x-widget": "legacy" },
                "failedSubfolder": { "type": "string", "maxLength": {{Ingestion.FolderPaths.MaxSubfolderLength}}, "pattern": "{{Escape(Ingestion.FolderPaths.SubfolderPattern)}}", "x-widget": "legacy" } },
              "required": ["pattern", "includeSubfolders", "interval"] }
            """,
            """
            { "sourcePath": "", "pattern": "*.json", "includeSubfolders": false, "interval": "every-minute", "processedPath": "", "failedPath": "" }
            """),

        Node(ParseContent, "Parse JSON / XML / CSV", "process",
            "Turns raw file content into structured data: reads the text in one field of the submitted data and replaces the run's data with that one record. A list is accepted only with a single entry.",
            [In], [new PortDto("success", "Success", true), new PortDto("failed", "Failed", false)],
            $$"""
            { "type": "object",
              "properties": {
                "source": { "type": "string", "title": "Content field", "minLength": 1, "maxLength": 200, "pattern": "{{Escape(DataPathPattern)}}", "x-widget": "mono",
                  "description": "The field of the submitted data that holds the file's content, such as content." },
                "format": { "type": "string", "title": "Format", "enum": {{JsonSerializer.Serialize(Executions.RecordParser.Formats.Keys)}}, "x-enumLabels": {{JsonSerializer.Serialize(Executions.RecordParser.Formats.Values)}} },
                "encoding": { "type": "string", "title": "Encoding", "enum": {{JsonSerializer.Serialize(Executions.RecordParser.Encodings.Keys)}}, "x-enumLabels": {{JsonSerializer.Serialize(Executions.RecordParser.Encodings.Values)}},
                  "description": "UTF-8: the field holds the text itself. The others: the field holds the file's bytes as base64." },
                "recordPath": { "type": "string", "title": "Record path (optional)", "maxLength": 200, "pattern": "{{Escape(OptionalDataPathPattern)}}", "x-widget": "mono",
                  "description": "Dotted names down to the record, such as invoices.invoice. XML keeps its root element's name. Leave empty to use the whole content." },
                "numbers": { "type": "boolean", "title": "Read plain numbers in XML and CSV as numbers" } },
              "required": ["source", "format", "encoding"] }
            """,
            """{ "source": "content", "format": "auto", "encoding": "utf-8", "recordPath": "", "numbers": true }"""),

        Node(MapFields, "Map fields", "process",
            "Turns source data into the template's data model: each row copies a field to a new name, optionally transformed. Fields no row mentions stay as they are.",
            [In], [new PortDto("success", "Success", true), new PortDto("failed", "Failed", false)],
            $$"""
            { "type": "object",
              "properties": {
                "rows": { "type": "array", "title": "Mappings", "maxItems": 100,
                  "items": { "type": "object", "required": ["from", "to", "transform"],
                    "properties": {
                      "from": { "type": "string", "title": "Source field", "minLength": 1, "maxLength": 200, "pattern": "{{Escape(DataPathPattern)}}", "x-widget": "mono" },
                      "to": { "type": "string", "title": "Destination field", "minLength": 1, "maxLength": 200, "pattern": "{{Escape(DataPathPattern)}}", "x-widget": "data-path" },
                      "transform": { "type": "string", "title": "Transform", "enum": {{JsonSerializer.Serialize(Executions.FieldMapper.Transforms.Keys)}}, "x-enumLabels": {{JsonSerializer.Serialize(Executions.FieldMapper.Transforms.Values)}} } } } } },
              "required": ["rows"] }
            """,
            """{ "rows": [] }"""),

        Node(TransformData, "Transform data", "process",
            "Calculates, reformats or reshapes values with an ordered list of rules (no scripts): each rule writes a result to a target field, and a rule sees what the rules above it wrote.",
            [In], [new PortDto("success", "Success", true), new PortDto("failed", "Failed", false)],
            $$$"""
            { "type": "object",
              "properties": {
                "rules": { "type": "array", "title": "Rules", "maxItems": {{{Executions.DataTransformer.MaxRules}}},
                  "items": { "type": "object", "required": ["target", "operation"],
                    "properties": {
                      "target": { "type": "string", "title": "Target field", "minLength": 1, "maxLength": 200, "pattern": "{{{Escape(DataPathPattern)}}}", "x-widget": "data-path",
                        "description": "Where the result is written, such as totals.gross." },
                      "operation": { "type": "string", "title": "Operation", "enum": {{{JsonSerializer.Serialize(Executions.DataTransformer.Operations.Keys)}}}, "x-enumLabels": {{{JsonSerializer.Serialize(Executions.DataTransformer.Operations.Values)}}} },
                      "a": { "type": "string", "title": "A: field or number", "maxLength": 200, "pattern": "{{{Escape(OperandPattern)}}}", "x-widget": "mono",
                        "description": "The field to read, or a number. Not used by Build text and Remove." },
                      "b": { "type": "string", "title": "B: field or number", "maxLength": 200, "pattern": "{{{Escape(OperandPattern)}}}", "x-widget": "mono",
                        "description": "The second value of Add, Subtract, Multiply and Divide; the decimals of Round (default 2)." },
                      "text": { "type": "string", "title": "Text", "maxLength": 500, "x-widget": "pattern",
                        "description": "Build text: use fields such as {{customer.name}} ({{invoice.number}}). Fallback: the value to use when A is missing or empty." } } } } },
              "required": ["rules"] }
            """,
            """{ "rules": [] }"""),

        Node(MergeData, "Merge data", "process",
            "Joins the data with a lookup table on a key: each record gets the fields of the lookup row whose key equals the record's. The lookup table is typed into the step (one list of records).",
            [In], [new PortDto("success", "Success", true), new PortDto("failed", "Failed", false)],
            $$"""
            { "type": "object",
              "properties": {
                "key": { "type": "string", "title": "Match on", "minLength": 1, "maxLength": 200, "pattern": "{{Escape(DataPathPattern)}}", "x-widget": "mono",
                  "description": "The field of the data to look up, such as customer.id." },
                "lookupKey": { "type": "string", "title": "Lookup field", "minLength": 1, "maxLength": 200, "pattern": "{{Escape(DataPathPattern)}}", "x-widget": "mono",
                  "description": "The field of each lookup record that holds the key, such as id. Each key may appear once." },
                "lookup": { "type": "string", "title": "Lookup table (JSON list)", "minLength": 1, "maxLength": {{Executions.DataMerger.MaxLookupText}}, "x-widget": "json",
                  "description": "A JSON list of records, such as [{ \"id\": \"C-1001\", \"name\": \"Kestrel Logistics Ltd\" }]. Reference data only, no real customer data." },
                "source": { "type": "string", "title": "List to enrich (optional)", "maxLength": 200, "pattern": "{{Escape(OptionalDataPathPattern)}}", "x-widget": "mono",
                  "description": "A list in the data, such as lines: each entry is matched on its own, and 'Match on' is read from the entry. Leave empty to match the data itself." },
                "into": { "type": "string", "title": "Write matches into (optional)", "maxLength": 200, "pattern": "{{Escape(OptionalDataPathPattern)}}", "x-widget": "data-path",
                  "description": "Where the lookup record's fields are written, such as customer. Leave empty to write them at the top level of the record." },
                "overwrite": { "type": "boolean", "title": "Replace values the data already has",
                  "description": "Off: the lookup only fills in what's missing." },
                "strategy": { "type": "string", "title": "When there's no match", "enum": {{JsonSerializer.Serialize(Executions.DataMerger.Strategies.Keys)}}, "x-enumLabels": {{JsonSerializer.Serialize(Executions.DataMerger.Strategies.Values)}},
                  "description": "Dropping applies to a list; a single record with no match fails the step." } },
              "required": ["key", "lookupKey", "lookup", "strategy"] }
            """,
            """{ "key": "", "lookupKey": "", "lookup": "[]", "source": "", "into": "", "overwrite": false, "strategy": "keep" }"""),

        Node(IfElse, "If / else", "logic",
            "Sends the run down the Yes path when its conditions are met, otherwise the No path. The data is not changed.",
            [In], [new PortDto("yes", "Yes", false), new PortDto("no", "No", false)],
            $$"""
            { "type": "object",
              "properties": {
                "conditions": { "type": "array", "title": "Conditions", "maxItems": {{Executions.IfElse.MaxConditions}}, "x-widget": "conditions",
                  "description": "Rows are joined by AND or OR; AND is applied first. Text ignores case; number comparisons need numbers.",
                  "items": { "type": "object", "required": ["field", "op"],
                    "properties": {
                      "join": { "type": "string", "title": "Joined to the row above by", "enum": {{JsonSerializer.Serialize(Executions.IfElse.Joins.Keys)}}, "x-enumLabels": {{JsonSerializer.Serialize(Executions.IfElse.Joins.Values)}} },
                      "field": { "type": "string", "title": "Field", "minLength": 1, "maxLength": 200, "pattern": "{{Escape(DataPathPattern)}}", "x-widget": "data-path" },
                      "op": { "type": "string", "title": "Operator", "enum": {{JsonSerializer.Serialize(Executions.IfElse.Operators.Keys)}}, "x-enumLabels": {{JsonSerializer.Serialize(Executions.IfElse.Operators.Values)}} },
                      "compare": { "type": "string", "title": "Compare with", "enum": {{JsonSerializer.Serialize(Executions.IfElse.Compares.Keys)}}, "x-enumLabels": {{JsonSerializer.Serialize(Executions.IfElse.Compares.Values)}} },
                      "value": { "type": "string", "title": "Value or field", "maxLength": 200,
                        "description": "A value such as 10000, or (when comparing with a field) a field path such as totals.net." } } } },
                "yesLabel": { "type": "string", "title": "Name of the Yes path", "maxLength": 40, "description": "Shown on the step. Optional." },
                "noLabel": { "type": "string", "title": "Name of the No path", "maxLength": 40, "description": "Shown on the step. Optional." } },
              "required": ["conditions"] }
            """,
            """{ "conditions": [ { "join": "and", "field": "", "op": "equals", "compare": "value", "value": "" } ], "yesLabel": "Yes", "noLabel": "No" }"""),

        Node(SwitchRoute, "Switch", "logic",
            "Routes the run by the value of one field: the first branch whose value matches decides the path, and 'Other' catches the rest. The outputs are the branches (listed in the settings). The data is not changed.",
            [In], [],
            $$"""
            { "type": "object",
              "properties": {
                "field": { "type": "string", "title": "Field", "minLength": 1, "maxLength": 200, "pattern": "{{Escape(DataPathPattern)}}", "x-widget": "data-path",
                  "description": "The field of the data to route by, such as customer.deliveryPreference." },
                "op": { "type": "string", "title": "Operator", "enum": {{JsonSerializer.Serialize(Executions.Switch.Operators.Keys)}}, "x-enumLabels": {{JsonSerializer.Serialize(Executions.Switch.Operators.Values)}},
                  "description": "Each branch compares the field with its own value. Text ignores case; number comparisons need numbers." },
                "branches": { "type": "array", "title": "Branches", "maxItems": {{Executions.Switch.MaxBranches}}, "x-widget": "branches",
                  "items": { "type": "object", "required": ["id", "label"],
                    "properties": {
                      "id": { "type": "string", "title": "Output id", "minLength": 1, "maxLength": 30, "pattern": "{{Escape(Executions.Switch.IdPattern)}}" },
                      "label": { "type": "string", "title": "Branch name", "minLength": 1, "maxLength": 40 },
                      "compare": { "type": "string", "title": "Compare with", "enum": {{JsonSerializer.Serialize(Executions.IfElse.Compares.Keys)}}, "x-enumLabels": {{JsonSerializer.Serialize(Executions.IfElse.Compares.Values)}} },
                      "value": { "type": "string", "title": "Value or field", "maxLength": 200 } } } },
                "other": { "type": "boolean", "title": "Add an 'Other' path for values that match no branch" } },
              "required": ["field", "op", "branches"] }
            """,
            """
            { "field": "", "op": "equals", "other": true,
              "branches": [ { "id": "branch-1", "label": "Branch 1", "compare": "value", "value": "" }, { "id": "branch-2", "label": "Branch 2", "compare": "value", "value": "" } ] }
            """),

        Node(ForEach, "For each", "logic",
            "Repeats the steps after it for every item of a list in the data. Mode 'each' keeps the whole record and adds the current item; mode 'split' makes each item its own record. One step of the loop may connect back into this step to finish an item. An empty list leaves by 'No items'.",
            [In], [new PortDto(Executions.ForEach.Each, Executions.ForEach.DefaultEachLabel, true), new PortDto(Executions.ForEach.Empty, Executions.ForEach.DefaultEmptyLabel, false)],
            $$"""
            { "type": "object",
              "properties": {
                "field": { "type": "string", "title": "List field", "minLength": 1, "maxLength": 200, "pattern": "{{Escape(DataPathPattern)}}", "x-widget": "data-path",
                  "description": "A field that holds a list, such as invoice.lines." },
                "mode": { "type": "string", "title": "Loop behaviour", "enum": {{JsonSerializer.Serialize(Executions.ForEach.Modes.Keys)}}, "x-enumLabels": {{JsonSerializer.Serialize(Executions.ForEach.Modes.Values)}} },
                "item": { "type": "string", "title": "Item name", "minLength": 1, "maxLength": 40, "pattern": "{{Escape(Executions.ForEach.ItemNamePattern)}}",
                  "description": "Later steps refer to each item by this name, such as item.description. A field of the record with the same name is replaced while the item runs." },
                "eachLabel": { "type": "string", "title": "Name of the loop path", "maxLength": 40, "description": "Shown on the step. Optional." },
                "emptyLabel": { "type": "string", "title": "Name of the empty-list path", "maxLength": 40, "description": "Shown on the step. Optional." } },
              "required": ["field", "mode", "item"] }
            """,
            $$"""{ "field": "", "mode": "each", "item": "{{Executions.ForEach.DefaultItemName}}", "eachLabel": "{{Executions.ForEach.DefaultEachLabel}}", "emptyLabel": "{{Executions.ForEach.DefaultEmptyLabel}}" }"""),

        Node(ApprovalStep, "Approval", "logic",
            "Pauses the run until someone holding the chosen role approves or declines. Approved continues down the Approved path; declined takes the Declined path, or ends the run as Rejected when that isn't connected.",
            [In], [new PortDto(Executions.Approval.Approved, "Approved", true), new PortDto(Executions.Approval.Rejected, "Declined", false)],
            $$"""
            { "type": "object",
              "properties": {
                "approver": { "type": "string", "title": "Approver", "enum": {{JsonSerializer.Serialize(Executions.Approval.Approvers.Keys)}}, "x-enumLabels": {{JsonSerializer.Serialize(Executions.Approval.ApproverLabels)}},
                  "description": "Anyone with this role can decide (administrators always can)." },
                "escalateAfter": { "type": "string", "title": "Escalate after", "enum": {{JsonSerializer.Serialize(Executions.Approval.Escalations.Keys)}}, "x-enumLabels": {{JsonSerializer.Serialize(Executions.Approval.EscalationLabels)}},
                  "description": "After this long the request is shown as overdue. Nothing is sent or reassigned automatically." } },
              "required": ["approver", "escalateAfter"] }
            """,
            $$"""{ "approver": "{{Executions.Approval.DefaultApprover}}", "escalateAfter": "{{Executions.Approval.DefaultEscalation}}" }"""),

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

        Node(GenerateDocx, "Generate DOCX", "doc",
            "Renders a published template version as an editable Word document with the run's data.",
            [In], [new PortDto("success", "Success", true), new PortDto("failed", "Failed", false)],
            $$$"""
            { "type": "object",
              "properties": {
                "templateId": { "type": "string", "title": "Template", "format": "uuid", "x-widget": "template" },
                "templateVersion": { "type": "string", "title": "Template version", "pattern": "^({{{LatestPublished}}}|[1-9][0-9]{0,5})$", "x-widget": "template-version",
                  "description": "'latest-published' is resolved to a specific version when a run is submitted." },
                "fileName": { "type": "string", "title": "Output filename pattern", "minLength": 1, "maxLength": 200, "x-widget": "pattern",
                  "description": "Use fields such as {{invoice.number}}." },
                "protect": { "type": "string", "title": "Editing", "enum": {{{JsonSerializer.Serialize(DocxEditing.Keys)}}}, "x-enumLabels": {{{JsonSerializer.Serialize(DocxEditing.Values)}}},
                  "description": "Word's editing restriction. It has no password, so a person can lift it in Word." } },
              "required": ["templateId", "templateVersion", "fileName", "protect"] }
            """,
            """{ "templateId": "", "templateVersion": "latest-published", "fileName": "{{invoice.number}}.docx", "protect": "allow" }"""),

        Node(GenerateEmail, "Generate HTML email", "doc",
            "Builds a responsive HTML email body from text typed into the step and the run's data. Nothing is sent.",
            [In], [new PortDto("success", "Success", true), new PortDto("failed", "Failed", false)],
            $$$"""
            { "type": "object",
              "properties": {
                "layout": { "type": "string", "title": "Email layout", "enum": {{{JsonSerializer.Serialize(EmailLayouts.Keys)}}}, "x-enumLabels": {{{JsonSerializer.Serialize(EmailLayouts.Values)}}},
                  "description": "Changes the heading label and accent colour, not the content." },
                "subject": { "type": "string", "title": "Subject", "minLength": 1, "maxLength": 200, "x-widget": "pattern",
                  "description": "One line. Use fields such as {{invoice.number}}." },
                "preheader": { "type": "string", "title": "Preview text", "maxLength": 200, "x-widget": "pattern",
                  "description": "The short line mail apps show next to the subject." },
                "body": { "type": "string", "title": "Body", "minLength": 1, "maxLength": 5000, "x-widget": "multiline",
                  "description": "Leave a blank line between paragraphs. Fields are written as {{customer.name}}." },
                "buttonLabel": { "type": "string", "title": "Button label", "maxLength": 80, "x-widget": "pattern" },
                "buttonUrl": { "type": "string", "title": "Button link", "maxLength": 500, "x-widget": "pattern",
                  "description": "A full http, https or mailto address. The button shows only when both the label and the link are set." },
                "footer": { "type": "string", "title": "Footer", "maxLength": 1000, "x-widget": "multiline" },
                "fileName": { "type": "string", "title": "Output filename pattern", "minLength": 1, "maxLength": 200, "x-widget": "pattern" },
                "darkMode": { "type": "boolean", "title": "Support dark mode",
                  "description": "Adds dark colours for mail apps that use them." } },
              "required": ["layout", "subject", "body", "fileName", "darkMode"] }
            """,
            """{ "layout": "invoice", "subject": "Invoice {{invoice.number}}", "preheader": "", "body": "Dear {{customer.name}},\n\nYour invoice {{invoice.number}} is ready.", "buttonLabel": "", "buttonUrl": "", "footer": "", "fileName": "{{invoice.number}}.html", "darkMode": true }"""),

        Node(CombinePdfs, "Combine PDFs", "doc",
            "Joins the PDFs this run has already generated into one PDF, with optional bookmarks. Documents made by Generate PDF steps earlier on the path (and, in a For each loop, for the same item) are used.",
            [In], [new PortDto("success", "Success", true), new PortDto("failed", "Failed", false)],
            $$$"""
            { "type": "object",
              "properties": {
                "order": { "type": "string", "title": "Page order", "enum": {{{JsonSerializer.Serialize(CombineOrders.Keys)}}}, "x-enumLabels": {{{JsonSerializer.Serialize(CombineOrders.Values)}}} },
                "bookmarks": { "type": "boolean", "title": "Add bookmarks",
                  "description": "One bookmark per combined document, named after its file." },
                "fileName": { "type": "string", "title": "Output filename pattern", "minLength": 1, "maxLength": 200, "x-widget": "pattern",
                  "description": "Use fields such as {{invoice.number}}." } },
              "required": ["order", "bookmarks", "fileName"] }
            """,
            """{ "order": "generated", "bookmarks": true, "fileName": "{{invoice.number}}-pack.pdf" }"""),

        Node(SendEmail, "Send email", "output",
            "Emails a message to the recipients through the bank's SMTP relay (set up by the administrator, ADR 0006), optionally with the generated document attached. A generated HTML email becomes the message body.",
            [In], [new PortDto("sent", "Sent", true), new PortDto("failed", "Failed", false)],
            $$$"""
            { "type": "object",
              "properties": {
                "to": { "type": "string", "title": "To", "minLength": 1, "maxLength": 500, "x-widget": "pattern",
                  "description": "Up to 10 addresses separated by commas, or a field such as {{customer.email}}. The administrator may limit which domains can receive mail." },
                "subject": { "type": "string", "title": "Subject", "minLength": 1, "maxLength": 200, "x-widget": "pattern",
                  "description": "One line. Use fields such as {{invoice.number}}." },
                "body": { "type": "string", "title": "Message", "maxLength": 5000, "x-widget": "multiline",
                  "description": "Plain text; fields are written as {{customer.name}}. When an HTML email is attached this is its plain-text alternative." },
                "attach": { "type": "string", "title": "Attach", "enum": {{{JsonSerializer.Serialize(EmailAttach.Keys)}}}, "x-enumLabels": {{{JsonSerializer.Serialize(EmailAttach.Values)}}},
                  "description": "The document made by the Generate step before this one (up to 10 MB)." } },
              "required": ["to", "subject", "attach"] }
            """,
            """{ "to": "{{customer.email}}", "subject": "Invoice {{invoice.number}}", "body": "Dear {{customer.name}},\n\nPlease find your invoice {{invoice.number}} attached.", "attach": "document" }"""),

        Node(PrintQueue, "Print queue", "output",
            "Sends the generated PDF for printing and posting: the Worker writes it, with a small job file, into the spool folder of a print queue the administrator set up (ADR 0007). Nothing is sent to a printer directly.",
            [In], [new PortDto("queued", "Queued", true)],
            $$"""
            { "type": "object",
              "properties": {
                "queue": { "type": "string", "title": "Print queue", "minLength": 2, "maxLength": 64, "pattern": "{{Escape(Executions.PrintQueues.KeyPattern)}}", "x-widget": "mono",
                  "description": "The queue's key as the administrator configured it (Print:Queues), such as cyprus-print-nightly. Lower-case letters, digits and hyphens." },
                "envelope": { "type": "string", "title": "Envelope", "enum": {{JsonSerializer.Serialize(Executions.PrintQueues.Envelopes.Keys)}}, "x-enumLabels": {{JsonSerializer.Serialize(Executions.PrintQueues.Envelopes.Values)}} },
                "duplex": { "type": "boolean", "title": "Print double-sided" } },
              "required": ["queue", "envelope"] }
            """,
            $$"""{ "queue": "", "envelope": "{{Executions.PrintQueues.DefaultEnvelope}}", "duplex": true }"""),

        Node(UploadSftp, "Upload to SFTP", "output",
            "Puts the generated document on a partner's SFTP server. Simulated: nothing connects to an SFTP server and no credentials are stored, so the step uploads nothing and its result says so.",
            [In], [new PortDto("success", "Success", true), new PortDto("failed", "Failed", false)],
            $$"""
            { "type": "object",
              "properties": {
                "host": { "type": "string", "title": "Host", "minLength": 1, "maxLength": 253, "pattern": "{{Escape(HostPattern)}}", "x-widget": "mono",
                  "description": "A host name such as sftp.example.com. No user name, password or URL." },
                "folder": { "type": "string", "title": "Folder", "minLength": 1, "maxLength": 300, "pattern": "{{Escape(FolderPattern)}}", "x-widget": "mono",
                  "description": "The folder on the server, starting with /." } },
              "required": ["host", "folder"] }
            """,
            """{ "host": "", "folder": "/in" }"""),

        Node(DeliverApi, "Deliver via API", "output",
            "Posts the generated document to another system's API. Simulated: nothing is sent over the network and no credentials are stored, so the step delivers nothing and its result says so.",
            [In], [new PortDto("delivered", "Delivered", true), new PortDto("failed", "Failed", false)],
            $$"""
            { "type": "object",
              "properties": {
                "url": { "type": "string", "title": "Endpoint URL", "minLength": 1, "maxLength": 300, "pattern": "{{Escape(ApiUrlPattern)}}", "x-widget": "mono",
                  "description": "An https address such as https://api.partner.com/documents. No user name, password or query string." } },
              "required": ["url"] }
            """,
            """{ "url": "" }"""),

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

    /// <summary>
    /// The outputs of one node. For most types that is the catalog's list; a Switch has one per branch of its settings
    /// (its catalog list is empty).
    /// </summary>
    public static IReadOnlyList<PortDto> OutputsOf(NodeTypeDto type, System.Text.Json.JsonElement config) => type.Type switch
    {
        SwitchRoute => Executions.Switch.Ports(config),
        ForEach => Executions.ForEach.Ports(config),
        _ => type.Outputs,
    };

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
