using System.Text.Json;
using System.Text.Json.Nodes;
using Verso.Application.Workflows;

namespace Verso.UnitTests.Workflows;

public sealed class GraphValidatorTests
{
    private static readonly Guid TemplateId = Guid.Parse("01a1c000-0000-7000-8000-000000000001");

    private static readonly Dictionary<Guid, TemplateRef> PublishedTemplate = new()
    {
        [TemplateId] = new TemplateRef(TemplateId, "Commercial invoice", new HashSet<int> { 1, 2 }),
    };

    /// <summary>The "Invoice standard" workflow from the M6 acceptance criteria: trigger → validate → generate PDF → archive.</summary>
    internal static JsonObject InvoiceStandard(Guid templateId, string version = "latest-published") => (JsonObject)JsonNode.Parse($$"""
        { "nodes": [
            { "id": "trigger", "type": "api-trigger", "title": "API trigger", "position": { "x": 0, "y": 80 }, "config": { "path": "/hooks/invoices", "auth": "hmac-sha256" } },
            { "id": "validate", "type": "validate", "title": "Validate invoice data", "position": { "x": 260, "y": 80 },
              "config": { "required": ["invoice.number", "customer.name"], "types": [ { "field": "invoice.date", "type": "date" }, { "field": "totals.gross", "type": "number" } ] } },
            { "id": "pdf", "type": "generate-pdf", "title": "Generate invoice PDF", "position": { "x": 520, "y": 80 },
              "config": { "templateId": "{{templateId}}", "templateVersion": "{{version}}", "fileName": "{{"{{"}}invoice.number{{"}}"}}.pdf" } },
            { "id": "archive", "type": "archive", "title": "Archive", "position": { "x": 780, "y": 80 },
              "config": { "folderPattern": "invoices/{{"{{"}}invoice.date|year{{"}}"}}", "retention": "7y", "metadata": [ { "key": "InvoiceNumber", "value": "{{"{{"}}invoice.number{{"}}"}}" } ] } } ],
          "edges": [
            { "id": "e1", "source": "trigger", "sourcePort": "out", "target": "validate", "targetPort": "in" },
            { "id": "e2", "source": "validate", "sourcePort": "valid", "target": "pdf", "targetPort": "in" },
            { "id": "e3", "source": "pdf", "sourcePort": "success", "target": "archive", "targetPort": "in" } ] }
        """)!;

    private static WorkflowValidationDto Validate(JsonNode definition, IReadOnlyDictionary<Guid, TemplateRef>? templates = null)
    {
        using var doc = JsonDocument.Parse(definition.ToJsonString());
        var (parsed, errors) = WorkflowDefinitionParser.Parse(doc.RootElement);
        Assert.True(parsed is not null, string.Join("; ", errors.Select(e => $"{e.Key}: {e.Value[0]}")));
        return GraphValidator.Validate(parsed!, templates ?? PublishedTemplate);
    }

    private static List<string> Codes(WorkflowValidationDto v, string severity = GraphValidator.Error) =>
        [.. v.Issues.Where(i => i.Severity == severity).Select(i => $"{i.NodeId}:{i.Code}")];

    [Fact]
    public void InvoiceStandard_IsValid_WithWarningsOnlyForTheOptionalPorts()
    {
        var result = Validate(InvoiceStandard(TemplateId));

        Assert.True(result.Valid);
        Assert.Empty(Codes(result));
        Assert.Equal(["validate:port.unconnected", "pdf:port.unconnected"], Codes(result, GraphValidator.Warning));
        Assert.Contains(result.Issues, i => i.Port == "invalid" && i.Message == "Invalid records will end the run as Rejected.");
    }

    [Fact]
    public void TemplateWithoutPublishedVersion_IsAnError()
    {
        var draftOnly = new Dictionary<Guid, TemplateRef> { [TemplateId] = new(TemplateId, "Commercial invoice", new HashSet<int>()) };

        var result = Validate(InvoiceStandard(TemplateId), draftOnly);

        Assert.False(result.Valid);
        var issue = Assert.Single(result.Issues, i => i.Code == "template.not_published");
        Assert.Equal("pdf", issue.NodeId);
        Assert.Equal("templateVersion", issue.Field);
    }

    [Fact]
    public void SpecificVersion_MustBePublished_AndTheTemplateMustExist()
    {
        Assert.Contains("pdf:template.not_published", Codes(Validate(InvoiceStandard(TemplateId, "3"))));
        Assert.True(Validate(InvoiceStandard(TemplateId, "2")).Valid);
        Assert.Contains("pdf:template.not_found", Codes(Validate(InvoiceStandard(Guid.NewGuid()))));
    }

    [Fact]
    public void DisconnectedGraph_ReportsEachProblemOnItsNode()
    {
        var def = InvoiceStandard(TemplateId);
        def["edges"]!.AsArray().RemoveAt(2); // pdf → archive

        var codes = Codes(Validate(def));

        Assert.Contains("pdf:port.unconnected", codes);   // required 'success'
        Assert.Contains("archive:node.unreachable", codes);
    }

    [Fact]
    public void Cycles_DuplicateOutputs_UnknownPortsAndNodes_AreErrors()
    {
        var def = InvoiceStandard(TemplateId);
        var edges = def["edges"]!.AsArray();
        edges.Add(JsonNode.Parse("""{ "id": "loop", "source": "pdf", "sourcePort": "failed", "target": "validate", "targetPort": "in" }"""));
        edges.Add(JsonNode.Parse("""{ "id": "dup", "source": "validate", "sourcePort": "valid", "target": "archive", "targetPort": "in" }"""));
        edges.Add(JsonNode.Parse("""{ "id": "bad", "source": "trigger", "sourcePort": "nope", "target": "archive", "targetPort": "in" }"""));
        edges.Add(JsonNode.Parse("""{ "id": "ghost", "source": "trigger", "sourcePort": "out", "target": "missing", "targetPort": "in" }"""));

        var issues = Validate(def).Issues.Where(i => i.Severity == GraphValidator.Error).ToList();

        Assert.Contains(issues, i => i.Code == "graph.cycle" && i.EdgeId == "loop");
        Assert.Contains(issues, i => i.Code == "edge.duplicate_output" && i.EdgeId == "dup");
        Assert.Contains(issues, i => i.Code == "edge.unknown_port" && i.EdgeId == "bad");
        Assert.Contains(issues, i => i.Code == "edge.unknown_node" && i.EdgeId == "ghost");
    }

    [Fact]
    public void Triggers_ExactlyOne()
    {
        var none = InvoiceStandard(TemplateId);
        none["nodes"]!.AsArray().RemoveAt(0);
        none["edges"]!.AsArray().RemoveAt(0);
        Assert.Contains(":graph.no_trigger", Codes(Validate(none)));

        var two = InvoiceStandard(TemplateId);
        two["nodes"]!.AsArray().Add(JsonNode.Parse("""{ "id": "t2", "type": "api-trigger", "title": "Second", "position": { "x": 0, "y": 300 }, "config": { "path": "/hooks/invoices", "auth": "hmac-sha256" } }"""));
        Assert.Contains("t2:graph.multiple_triggers", Codes(Validate(two)));
    }

    [Fact]
    public void Settings_AreCheckedAgainstTheNodeSchema()
    {
        var def = InvoiceStandard(TemplateId);
        var nodes = def["nodes"]!.AsArray();
        nodes[1]!["config"] = JsonNode.Parse("""{ "required": [], "types": [] }""");                                    // no rules
        nodes[2]!["config"]!["fileName"] = "";                                                                            // required
        nodes[3]!["config"]!["retention"] = "forever";                                                                     // not in enum
        nodes[3]!["config"]!["metadata"] = JsonNode.Parse("""[ { "key": "1bad", "value": "x" } ]""");                     // pattern
        nodes[3]!["config"]!["colour"] = "red";                                                                            // unknown

        var issues = Validate(def).Issues;

        Assert.Contains(issues, i => i.NodeId == "validate" && i.Code == "config.required" && i.Message.StartsWith("Add at least one rule", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.NodeId == "pdf" && i.Field == "fileName" && i.Message == "Output filename pattern is required.");
        Assert.Contains(issues, i => i.NodeId == "archive" && i.Field == "retention");
        Assert.Contains(issues, i => i.NodeId == "archive" && i.Field == "metadata[0].key");
        Assert.Contains(issues, i => i.NodeId == "archive" && i.Field == "colour" && i.Code == "config.unknown");
    }

    [Fact]
    public void UnknownNodeType_IsAnErrorOnThatNode()
    {
        var def = InvoiceStandard(TemplateId);
        def["nodes"]!.AsArray()[3]!["type"] = "print-letter";

        Assert.Contains("archive:node.unknown_type", Codes(Validate(def)));
    }

    [Fact]
    public void Parser_RejectsBrokenStructure_WithFieldPaths()
    {
        using var doc = JsonDocument.Parse("""
            { "nodes": [ { "id": "a b", "type": "validate", "title": "x", "position": { "x": "1", "y": 0 }, "config": [] },
                         { "id": "n", "type": "validate", "title": "x", "position": { "x": 1, "y": 0 }, "config": {}, "extra": 1 },
                         { "id": "n", "type": "validate", "title": "x", "position": { "x": 1, "y": 0 }, "config": {} } ],
              "edges": [], "meta": {} }
            """);

        var (parsed, errors) = WorkflowDefinitionParser.Parse(doc.RootElement);

        Assert.Null(parsed);
        Assert.Contains("definition.meta", errors.Keys);
        Assert.Contains("definition.nodes[0].id", errors.Keys);
        Assert.Contains("definition.nodes[0].position", errors.Keys);
        Assert.Contains("definition.nodes[0].config", errors.Keys);
        Assert.Contains("definition.nodes[1].extra", errors.Keys);
        Assert.Contains("definition.nodes[2].id", errors.Keys); // duplicate
    }

    [Theory]
    [InlineData("samplePayload", "", true)]
    [InlineData("samplePayload", "{ \"invoice\": { \"number\": \"INV-1\" } }", true)]
    [InlineData("samplePayload", "[1, 2]", false)]
    [InlineData("samplePayload", "{ not json", false)]
    [InlineData("expectedSchema", "{ \"type\": \"object\" }", true)]
    [InlineData("expectedSchema", "\"object\"", false)]
    [InlineData("expectedSchema", "{ type: object }", false)]
    public void ApiTrigger_SchemaAndSample_MustBeBlankOrAJsonObject(string field, string text, bool valid)
    {
        var definition = InvoiceStandard(TemplateId);
        definition["nodes"]![0]!["config"]![field] = text;

        var result = Validate(definition);

        Assert.Equal(valid, !result.Issues.Any(i => i.NodeId == "trigger" && i.Code == "config.invalid" && i.Field == field));
    }

    [Theory]
    [InlineData(null, "config.required")]
    [InlineData("hooks/invoices", "config.invalid")]
    [InlineData("/hooks/in voices", "config.invalid")]
    public void ApiTrigger_Path_IsRequiredAndStartsWithASlash(string? path, string code)
    {
        var definition = InvoiceStandard(TemplateId);
        var config = definition["nodes"]![0]!["config"]!.AsObject();
        if (path is null) config.Remove("path");
        else config["path"] = path;

        var result = Validate(definition);

        Assert.Contains(result.Issues, i => i.NodeId == "trigger" && i.Code == code && i.Field == "path");
        Assert.False(result.Valid);
    }

    [Fact]
    public void ApiTrigger_Defaults_AreValid()
    {
        var definition = InvoiceStandard(TemplateId);
        definition["nodes"]![0]!["config"] = JsonNode.Parse(NodeCatalog.Find(NodeCatalog.ApiTrigger)!.Defaults.GetRawText());

        var result = Validate(definition);

        Assert.True(result.Valid);
        Assert.DoesNotContain(result.Issues, i => i.NodeId == "trigger");
    }


    private static JsonNode ErpTrigger(string id = "trigger", string entity = "Invoices") => JsonNode.Parse($$"""
        { "id": "{{id}}", "type": "erp-connector", "title": "ERP connector", "position": { "x": 0, "y": 80 },
          "config": { "system": "sap-s4hana", "entity": "{{entity}}", "mode": "nightly" } }
        """)!;

    [Fact]
    public void ErpConnector_CanBeTheTrigger()
    {
        var definition = InvoiceStandard(TemplateId);
        definition["nodes"]![0] = ErpTrigger();

        var result = Validate(definition);

        Assert.True(result.Valid);
        Assert.DoesNotContain(result.Issues, i => i.NodeId == "trigger");
    }

    [Fact]
    public void ErpConnector_NextToAnApiTrigger_IsASecondTrigger()
    {
        var definition = InvoiceStandard(TemplateId);
        definition["nodes"]!.AsArray().Add(ErpTrigger("erp"));

        Assert.Contains("erp:graph.multiple_triggers", Codes(Validate(definition)));
    }

    [Fact]
    public void ErpConnector_NeedsARecordType_AndAKnownSystem()
    {
        var definition = InvoiceStandard(TemplateId);
        var erp = ErpTrigger(entity: " ");
        erp["config"]!["system"] = "salesforce";
        definition["nodes"]![0] = erp;

        var result = Validate(definition);

        Assert.Contains(result.Issues, i => i.NodeId == "trigger" && i.Code == "config.required" && i.Field == "entity");
        Assert.Contains(result.Issues, i => i.NodeId == "trigger" && i.Field == "system");
    }

    [Fact]
    public void FileUpload_CanBeTheTrigger_AndNeedsAcceptedTypesAndKnownOptions()
    {
        var definition = InvoiceStandard(TemplateId);
        definition["nodes"]![0] = JsonNode.Parse("""
            { "id": "trigger", "type": "file-upload", "title": "File upload", "position": { "x": 0, "y": 80 },
              "config": { "accept": "PDF, XML, CSV", "maxSize": "25mb", "who": "staff" } }
            """);
        Assert.True(Validate(definition).Valid);

        definition["nodes"]![0]!["config"] = JsonNode.Parse("""{ "accept": "", "maxSize": "1gb", "who": "staff" }""");
        var result = Validate(definition);

        Assert.Contains(result.Issues, i => i.NodeId == "trigger" && i.Code == "config.required" && i.Field == "accept");
        Assert.Contains(result.Issues, i => i.NodeId == "trigger" && i.Field == "maxSize");
        Assert.False(result.Valid);
    }

    private static JsonObject SftpTrigger(string host, string folder = "/inbound", string pattern = "*.xml") => new JsonObject
    {
        ["id"] = "trigger", ["type"] = "sftp-folder", ["title"] = "SFTP folder", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 80 },
        ["config"] = new JsonObject { ["host"] = host, ["folder"] = folder, ["pattern"] = pattern, ["poll"] = "every-5-minutes" },
    };

    [Theory]
    [InlineData("sftp.example.com", "/inbound", "*.xml", true)]
    [InlineData("sftp.example.com", "/in bound/2026", "", true)]
    [InlineData("", "/inbound", "*.xml", false)]                       // host is required
    [InlineData("user:secret@sftp.example.com", "/inbound", "", false)] // no credentials in the host
    [InlineData("sftp://sftp.example.com", "/inbound", "", false)]     // no URL
    [InlineData("sftp.example.com", "inbound", "", false)]             // folder starts with /
    [InlineData("sftp.example.com", "/inbound", "../*.xml", false)]    // file pattern has no path parts
    public void SftpFolder_NeedsAPlainHostAndAFolder(string host, string folder, string pattern, bool valid)
    {
        var definition = InvoiceStandard(TemplateId);
        definition["nodes"]![0] = SftpTrigger(host, folder, pattern);

        var result = Validate(definition);

        Assert.Equal(valid, !result.Issues.Any(i => i.NodeId == "trigger" && i.Severity == GraphValidator.Error));
    }

    [Theory]
    [InlineData("accounts@example.com", "Inbox", true)]
    [InlineData("accounts@example.com", "Accounts/Invoices", true)]
    [InlineData("", "Inbox", false)]                              // mailbox is required
    [InlineData("accounts", "Inbox", false)]                      // not an address
    [InlineData("Accounts <accounts@example.com>", "Inbox", false)] // no display name
    [InlineData("accounts@example.com", "", false)]               // folder is required
    public void EmailInbox_NeedsAMailboxAddressAndAFolder(string mailbox, string folder, bool valid)
    {
        var definition = InvoiceStandard(TemplateId);
        definition["nodes"]![0] = new JsonObject
        {
            ["id"] = "trigger", ["type"] = "email-inbox", ["title"] = "Email inbox", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 80 },
            ["config"] = new JsonObject { ["mailbox"] = mailbox, ["folder"] = folder, ["attachments"] = true },
        };

        var result = Validate(definition);

        Assert.Equal(valid, !result.Issues.Any(i => i.NodeId == "trigger" && i.Severity == GraphValidator.Error));
    }

    [Theory]
    [InlineData("0 1 * * *", "Europe/Nicosia", true)]
    [InlineData("*/15 8-18 * * 1-5", "UTC", true)]
    [InlineData("0 1 * *", "UTC", false)]            // four fields
    [InlineData("0 1 * * * *", "UTC", false)]        // six fields
    [InlineData("0 1 * * *; rm -rf /", "UTC", false)] // no other characters
    [InlineData("", "UTC", false)]                   // required
    [InlineData("0 1 * * *", "Mars/Olympus", false)] // known time zones only
    public void ScheduledTrigger_NeedsFiveCronFieldsAndAKnownTimeZone(string cron, string tz, bool valid)
    {
        var definition = InvoiceStandard(TemplateId);
        definition["nodes"]![0] = new JsonObject
        {
            ["id"] = "trigger", ["type"] = "scheduled-trigger", ["title"] = "Scheduled trigger", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 80 },
            ["config"] = new JsonObject { ["cron"] = cron, ["tz"] = tz },
        };

        var result = Validate(definition);

        Assert.Equal(valid, !result.Issues.Any(i => i.NodeId == "trigger" && i.Severity == GraphValidator.Error));
    }

    [Theory]
    [InlineData("template-leads", "Invoice number", true)]
    [InlineData("workspace", "", true)]                // "ask" is optional
    [InlineData("credit-control", null, true)]
    [InlineData("everyone", "Invoice number", false)]  // known groups only
    [InlineData("", "Invoice number", false)]          // required
    public void ManualTrigger_NeedsAKnownStarterGroup(string who, string? ask, bool valid)
    {
        var definition = InvoiceStandard(TemplateId);
        var config = new JsonObject { ["who"] = who };
        if (ask is not null) config["ask"] = ask;
        definition["nodes"]![0] = new JsonObject
        {
            ["id"] = "trigger", ["type"] = "manual-trigger", ["title"] = "Manual trigger", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 80 },
            ["config"] = config,
        };

        var result = Validate(definition);

        Assert.Equal(valid, !result.Issues.Any(i => i.NodeId == "trigger" && i.Severity == GraphValidator.Error));
    }

    private static JsonObject WithEmailStep(string label, string url)
    {
        var definition = InvoiceStandard(TemplateId);
        var step = definition["nodes"]!.AsArray().Single(n => (string?)n!["id"] == "pdf")!;
        step["type"] = "generate-email";
        step["config"] = JsonNode.Parse($$"""
            { "layout": "invoice", "subject": "Invoice", "preheader": "", "body": "Hello", "buttonLabel": "{{label}}", "buttonUrl": "{{url}}", "footer": "", "fileName": "x.html", "darkMode": true }
            """);
        return definition;
    }

    [Fact]
    public void EmailStep_NeedsNoTemplate_AndAnEmptyButtonIsFine()
    {
        var result = Validate(WithEmailStep("", ""), new Dictionary<Guid, TemplateRef>());

        Assert.True(result.Valid);
        Assert.Empty(Codes(result));
    }

    [Theory]
    [InlineData("Pay", "", "pdf:config.invalid")]
    [InlineData("", "https://pay.example.test", "pdf:config.invalid")]
    [InlineData("Pay", "javascript:alert(1)", "pdf:config.invalid")]
    [InlineData("Pay", "https://pay.example.test/{{invoice.number}}", null)] // a link built from data is checked when the run happens
    [InlineData("Pay", "mailto:billing@example.test", null)]
    public void EmailStep_ButtonNeedsBothParts_AndALiteralLinkMustBeHttpOrMailto(string label, string url, string? expected)
    {
        var result = Validate(WithEmailStep(label, url), new Dictionary<Guid, TemplateRef>());

        Assert.Equal(expected is null ? [] : [expected], Codes(result));
    }

    [Theory]
    [InlineData("sftp.example.com", "/in", true)]
    [InlineData("", "/in", false)]                                // host is required
    [InlineData("user:secret@sftp.example.com", "/in", false)]    // no credentials in the host
    [InlineData("sftp://sftp.example.com", "/in", false)]         // no URL
    [InlineData("sftp.example.com", "in", false)]                 // folder starts with /
    [InlineData("sftp.example.com", "", false)]                   // folder is required
    public void UploadSftp_NeedsAPlainHostAndAFolder(string host, string folder, bool valid)
    {
        var definition = InvoiceStandard(TemplateId);
        definition["nodes"]!.AsArray().Add(JsonNode.Parse($$"""
            { "id": "sftp", "type": "upload-sftp", "title": "Upload to SFTP", "position": { "x": 780, "y": 260 }, "config": { "host": "{{host}}", "folder": "{{folder}}" } }
            """));
        var edges = definition["edges"]!.AsArray();
        edges.Single(e => (string?)e!["id"] == "e3")!["target"] = "sftp";
        edges.Add(JsonNode.Parse("""{ "id": "e4", "source": "sftp", "sourcePort": "success", "target": "archive", "targetPort": "in" }"""));

        var result = Validate(definition);

        var errors = result.Issues.Where(i => i.Severity == GraphValidator.Error).Select(i => $"{i.NodeId}:{i.Code}").ToList();
        Assert.True(valid == (errors.Count == 0), string.Join("; ", errors));
    }

    [Theory]
    [InlineData("https://api.partner.com/documents", true)]
    [InlineData("https://api.partner.com:8443/v1/documents", true)]
    [InlineData("", false)]                                          // required
    [InlineData("http://api.partner.com/documents", false)]          // https only
    [InlineData("https://user:secret@api.partner.com/documents", false)] // no credentials in the URL
    [InlineData("https://api.partner.com/documents?key=abc", false)] // no query string
    [InlineData("api.partner.com/documents", false)]                 // needs the scheme
    public void DeliverApi_NeedsAnHttpsAddress(string url, bool valid)
    {
        var definition = InvoiceStandard(TemplateId);
        definition["nodes"]!.AsArray().Add(JsonNode.Parse($$"""
            { "id": "api", "type": "deliver-api", "title": "Deliver via API", "position": { "x": 780, "y": 260 }, "config": { "url": "{{url}}" } }
            """));
        var edges = definition["edges"]!.AsArray();
        edges.Single(e => (string?)e!["id"] == "e3")!["target"] = "api";
        edges.Add(JsonNode.Parse("""{ "id": "e4", "source": "api", "sourcePort": "delivered", "target": "archive", "targetPort": "in" }"""));

        var result = Validate(definition);

        var errors = result.Issues.Where(i => i.Severity == GraphValidator.Error).Select(i => $"{i.NodeId}:{i.Code}").ToList();
        Assert.True(valid == (errors.Count == 0), string.Join("; ", errors));
    }

    [Fact]
    public void Catalog_DefaultsSatisfyTheirOwnSchemas_ExceptRequiredUserInput()
    {
        foreach (var type in NodeCatalog.All)
        {
            var errors = ConfigSchemaValidator.Validate(type.ConfigSchema, type.Defaults);
            // These need user input: generate-pdf and generate-docx a template, sftp-folder a host, merge-data its two key fields, print-queue the key of a configured queue. (Folder input needs its paths typed; that is checked by the graph rules.)
            // Everything else is valid out of the box.
            var expected = type.Type switch
            {
                NodeCatalog.GeneratePdf or NodeCatalog.GenerateDocx => ["templateId"],
                NodeCatalog.SftpFolder or NodeCatalog.UploadSftp => ["host"],
                NodeCatalog.MergeData => ["key", "lookupKey"],
                NodeCatalog.IfElse => ["conditions[0].field"],
                NodeCatalog.SwitchRoute => ["field"],
                NodeCatalog.ForEach => ["field"],
                NodeCatalog.PrintQueue => ["queue"],
                NodeCatalog.DeliverApi => ["url"],
                _ => Array.Empty<string>(),
            };
            Assert.Equal(expected, errors.Select(e => e.Field).ToArray());
        }
    }
}
