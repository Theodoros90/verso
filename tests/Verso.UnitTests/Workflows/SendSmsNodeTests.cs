using System.Text.Json;
using System.Text.Json.Nodes;
using Verso.Application.Workflows;

namespace Verso.UnitTests.Workflows;

/// <summary>The Send SMS node (catalog entry and the design-time checks of the graph validator).</summary>
public sealed class SendSmsNodeTests
{
    private static JsonObject Config(string? connection = "brevo-bank", string? to = "{{customer.mobile}}", string? text = "Dear {{customer.name}}, your statement is ready.")
    {
        var config = new JsonObject();
        if (connection is not null) config["connection"] = connection;
        if (to is not null) config["to"] = to;
        if (text is not null) config["text"] = text;
        return config;
    }

    private static WorkflowValidationDto Validate(JsonObject config, string type = "send-sms", string port = "sent")
    {
        var definition = new JsonObject
        {
            ["nodes"] = new JsonArray(
                new JsonObject { ["id"] = "trigger", ["type"] = "api-trigger", ["title"] = "Trigger", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 }, ["config"] = new JsonObject { ["path"] = "/hooks/x", ["auth"] = "none" } },
                new JsonObject { ["id"] = "s", ["type"] = type, ["title"] = "SMS", ["position"] = new JsonObject { ["x"] = 200, ["y"] = 0 }, ["config"] = config },
                new JsonObject { ["id"] = "archive", ["type"] = "archive", ["title"] = "Archive", ["position"] = new JsonObject { ["x"] = 400, ["y"] = 0 }, ["config"] = new JsonObject { ["folderPattern"] = "x", ["retention"] = "7y", ["metadata"] = new JsonArray() } }),
            ["edges"] = new JsonArray(
                new JsonObject { ["id"] = "e1", ["source"] = "trigger", ["sourcePort"] = "out", ["target"] = "s", ["targetPort"] = "in" },
                new JsonObject { ["id"] = "e2", ["source"] = "s", ["sourcePort"] = port, ["target"] = "archive", ["targetPort"] = "in" }),
        };
        using var doc = JsonDocument.Parse(definition.ToJsonString());
        var (parsed, errors) = WorkflowDefinitionParser.Parse(doc.RootElement);
        Assert.True(parsed is not null, string.Join("; ", errors.Select(e => $"{e.Key}: {e.Value[0]}")));
        return GraphValidator.Validate(parsed!, new Dictionary<Guid, TemplateRef>());
    }

    private static List<WorkflowIssueDto> ErrorsOnSms(JsonObject config) =>
        [.. Validate(config).Issues.Where(i => i.Severity == GraphValidator.Error && i.NodeId == "s")];

    [Fact]
    public void Catalog_HasTheNode_WithItsPortsAndSettings()
    {
        var type = Assert.Single(NodeCatalog.All, t => t.Type == "send-sms");

        Assert.Equal(NodeCatalog.SendSms, type.Type);
        Assert.Equal("output", type.Category);
        Assert.Equal(["in"], type.Inputs.Select(p => p.Id));
        Assert.Equal(["sent", "failed"], type.Outputs.Select(p => p.Id));
        Assert.True(type.Outputs.Single(p => p.Id == "sent").Required);
        Assert.False(type.Outputs.Single(p => p.Id == "failed").Required);
        Assert.Equal(
            "Sends ONE real SMS through an SMS connection an administrator switched on for workflows, to a number and text taken from the run's data, within the countries the administrator allowed. A sent message cannot be recalled. The step is never retried automatically and never repeated on resume.",
            type.Description);
        var properties = type.ConfigSchema.GetProperty("properties");
        var connection = properties.GetProperty("connection");
        Assert.Equal("sms-connection", connection.GetProperty("x-widget").GetString());
        Assert.True(connection.GetProperty("x-requires-sending").GetBoolean());
        Assert.Equal(Verso.Application.Connections.DatabaseServers.KeyPattern, connection.GetProperty("pattern").GetString());
        var to = properties.GetProperty("to");
        Assert.Equal("mono", to.GetProperty("x-widget").GetString());
        Assert.Equal(1, to.GetProperty("minLength").GetInt32());
        Assert.Equal(100, to.GetProperty("maxLength").GetInt32());
        Assert.Equal("The recipient's number in international format, usually a field such as {{customer.mobile}}. The number must start with a country the administrator allowed for the connection.", to.GetProperty("description").GetString());
        var text = properties.GetProperty("text");
        Assert.Equal("multiline", text.GetProperty("x-widget").GetString());
        Assert.Equal(1, text.GetProperty("minLength").GetInt32());
        Assert.Equal(480, text.GetProperty("maxLength").GetInt32());
        Assert.Equal("The message. Fields are written as {{customer.name}}. At most 480 characters (three messages). Plain text only.", text.GetProperty("description").GetString());
        Assert.Equal(["connection", "to", "text"], type.ConfigSchema.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal("", type.Defaults.GetProperty("connection").GetString());
        Assert.Equal("{{customer.mobile}}", type.Defaults.GetProperty("to").GetString());
        Assert.Equal("Dear {{customer.name}}, your statement is ready.", type.Defaults.GetProperty("text").GetString());
    }

    [Fact]
    public void TheNode_SitsNextToTheOtherOutputsOfItsKind_BeforeIfElse()
    {
        var types = NodeCatalog.All.Select(t => t.Type).ToList();

        Assert.Equal(types.IndexOf("write-database") + 1, types.IndexOf("send-sms"));
        Assert.Equal(types.IndexOf("send-sms") + 1, types.IndexOf("if-else"));
    }

    [Fact]
    public void TheDefaults_ValidateExceptTheEmptyConnection()
    {
        var type = NodeCatalog.All.Single(t => t.Type == "send-sms");
        var defaults = JsonNode.Parse(type.Defaults.GetRawText())!.AsObject();

        var issues = ErrorsOnSms(defaults);

        var issue = Assert.Single(issues);
        Assert.Equal("connection", issue.Field);
    }

    [Fact]
    public void AGoodNode_HasNoErrors() => Assert.Empty(ErrorsOnSms(Config()));

    [Fact]
    public void AFixedNumber_IsAccepted() => Assert.Empty(ErrorsOnSms(Config(to: "+357 99 000000")));

    [Fact]
    public void AMissingConnection_IsAnError_OnTheConnectionField()
    {
        var issue = Assert.Single(ErrorsOnSms(Config(connection: null)));
        Assert.Equal("connection", issue.Field);
    }

    [Theory]
    [InlineData("Brevo SMS!")]
    public void ABadConnectionKey_IsAnError(string key)
    {
        var issue = Assert.Single(ErrorsOnSms(Config(connection: key)));
        Assert.Equal("connection", issue.Field);
    }

    [Theory]
    [InlineData("{{ 1bad }}")]
    [InlineData("{{a..b}}")]
    [InlineData("{{a")]
    [InlineData("a}}")]
    [InlineData("call me")]
    public void ABadNumber_IsReported_OnTheToField_WithTheHelpersMessage(string to)
    {
        var issue = Assert.Single(ErrorsOnSms(Config(to: to)));

        Assert.Equal("to", issue.Field);
        Assert.Equal("config.invalid", issue.Code);
        Assert.Equal(Verso.Application.Executions.SmsTemplates.ValidateTo(to), issue.Message);
    }

    [Fact]
    public void ANumberOver100Characters_IsReportedOnce_OnTheToField()
    {
        var issue = Assert.Single(ErrorsOnSms(Config(to: new string('1', 101))));
        Assert.Equal("to", issue.Field);
    }

    [Theory]
    [InlineData("Hi {{ 1bad }}")]
    [InlineData("Hi {{a..b}}")]
    [InlineData("Hi {{a")]
    [InlineData("Hi a}}")]
    [InlineData("Hi\tthere")]
    [InlineData("Hi ‮there")]
    public void ABadText_IsReported_OnTheTextField_WithTheHelpersMessage(string text)
    {
        var issue = Assert.Single(ErrorsOnSms(Config(text: text)));

        Assert.Equal("text", issue.Field);
        Assert.Equal("config.invalid", issue.Code);
        Assert.Equal(Verso.Application.Executions.SmsTemplates.ValidateText(text), issue.Message);
    }

    [Fact]
    public void ATextOver480Characters_IsReportedOnce_OnTheTextField()
    {
        var issue = Assert.Single(ErrorsOnSms(Config(text: new string('a', 481))));
        Assert.Equal("text", issue.Field);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void ABlankNumberOrText_IsReportedOnce_ByTheSchemaCheck(string blank)
    {
        var toIssue = Assert.Single(ErrorsOnSms(Config(to: blank)));
        Assert.Equal("to", toIssue.Field);
        Assert.Equal("config.required", toIssue.Code);

        var textIssue = Assert.Single(ErrorsOnSms(Config(text: blank)));
        Assert.Equal("text", textIssue.Field);
        Assert.Equal("config.required", textIssue.Code);
    }

    [Fact]
    public void AMissingNumberOrText_IsReportedOnce()
    {
        Assert.Equal("to", Assert.Single(ErrorsOnSms(Config(to: null))).Field);
        Assert.Equal("text", Assert.Single(ErrorsOnSms(Config(text: null))).Field);
    }

    [Fact]
    public void ThePublishedDefinition_NeedsNoConnectionToExist() => Assert.Empty(ErrorsOnSms(Config(connection: "not-created-yet")));

    [Fact]
    public void ThePortNote_ExplainsWhereTheRunEnds()
    {
        var issues = Validate(Config(), port: "sent").Issues;

        Assert.Contains(issues, i => i.NodeId == "s" && i.Port == "failed"
            && i.Message == "Data that can't make a valid message (a missing value, an invalid number or a country that isn't allowed) will end the run as Rejected.");
    }

    [Fact]
    public void TheOtherNodes_AreUnaffected_ByTheSmsRules()
    {
        // A fetch-api node with a path and fields that the SMS rules would refuse behaves as before.
        var fetch = new JsonObject { ["connection"] = "rates", ["path"] = "/v1/{{customer.number}}", ["query"] = "", ["resultName"] = "api", ["maxResponseKB"] = 64, ["timeoutSeconds"] = 15 };
        Assert.DoesNotContain(Validate(fetch, "fetch-api", "found").Issues, i => i.Severity == GraphValidator.Error && i.NodeId == "s");

        var write = new JsonObject { ["connection"] = "core-banking", ["sql"] = "UPDATE customers SET status = @status WHERE id = @id", ["parameters"] = "status = a.b\nid = a.c", ["maxRows"] = 1, ["timeoutSeconds"] = 15 };
        Assert.DoesNotContain(Validate(write, "write-database", "written").Issues, i => i.Severity == GraphValidator.Error && i.NodeId == "s");

        var query = new JsonObject { ["connection"] = "core-banking", ["sql"] = "SELECT name FROM customers WHERE id = @id", ["parameters"] = "id = a.c", ["resultName"] = "db", ["maxRows"] = 1, ["timeoutSeconds"] = 15 };
        Assert.DoesNotContain(Validate(query, "query-database", "found").Issues, i => i.Severity == GraphValidator.Error && i.NodeId == "s");
    }
}
