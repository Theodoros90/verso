using System.Text.Json;
using System.Text.Json.Nodes;
using Verso.Application.Workflows;

namespace Verso.UnitTests.Workflows;

/// <summary>The Fetch from API node (catalog entry and the design-time checks of the graph validator).</summary>
public sealed class FetchApiNodeTests
{
    private static JsonObject Config(string? path = "/v1/latest", string? query = "base = invoice.currency", string? connection = "rates", string? resultName = "api", int maxResponseKB = 64, int timeoutSeconds = 15)
    {
        var config = new JsonObject { ["maxResponseKB"] = maxResponseKB, ["timeoutSeconds"] = timeoutSeconds };
        if (connection is not null) config["connection"] = connection;
        if (path is not null) config["path"] = path;
        if (query is not null) config["query"] = query;
        if (resultName is not null) config["resultName"] = resultName;
        return config;
    }

    private static WorkflowValidationDto Validate(JsonObject config, string type = "fetch-api", string port = "found")
    {
        var definition = new JsonObject
        {
            ["nodes"] = new JsonArray(
                new JsonObject { ["id"] = "trigger", ["type"] = "api-trigger", ["title"] = "Trigger", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 }, ["config"] = new JsonObject { ["path"] = "/hooks/x", ["auth"] = "none" } },
                new JsonObject { ["id"] = "f", ["type"] = type, ["title"] = "Fetch", ["position"] = new JsonObject { ["x"] = 200, ["y"] = 0 }, ["config"] = config },
                new JsonObject { ["id"] = "archive", ["type"] = "archive", ["title"] = "Archive", ["position"] = new JsonObject { ["x"] = 400, ["y"] = 0 }, ["config"] = new JsonObject { ["folderPattern"] = "x", ["retention"] = "7y", ["metadata"] = new JsonArray() } }),
            ["edges"] = new JsonArray(
                new JsonObject { ["id"] = "e1", ["source"] = "trigger", ["sourcePort"] = "out", ["target"] = "f", ["targetPort"] = "in" },
                new JsonObject { ["id"] = "e2", ["source"] = "f", ["sourcePort"] = port, ["target"] = "archive", ["targetPort"] = "in" }),
        };
        using var doc = JsonDocument.Parse(definition.ToJsonString());
        var (parsed, errors) = WorkflowDefinitionParser.Parse(doc.RootElement);
        Assert.True(parsed is not null, string.Join("; ", errors.Select(e => $"{e.Key}: {e.Value[0]}")));
        return GraphValidator.Validate(parsed!, new Dictionary<Guid, TemplateRef>());
    }

    private static List<WorkflowIssueDto> ErrorsOnFetch(JsonObject config) =>
        [.. Validate(config).Issues.Where(i => i.Severity == GraphValidator.Error && i.NodeId == "f")];

    [Fact]
    public void Catalog_HasTheNode_WithItsPortsAndSettings()
    {
        var type = Assert.Single(NodeCatalog.All, t => t.Type == "fetch-api");

        Assert.Equal(NodeCatalog.FetchApi, type.Type);
        Assert.Equal("process", type.Category);
        Assert.Equal(["in"], type.Inputs.Select(p => p.Id));
        Assert.Equal(["found", "not-found", "failed"], type.Outputs.Select(p => p.Id));
        Assert.True(type.Outputs.Single(p => p.Id == "found").Required);
        Assert.False(type.Outputs.Single(p => p.Id == "not-found").Required);
        Assert.False(type.Outputs.Single(p => p.Id == "failed").Required);
        Assert.Contains("one GET request", type.Description, StringComparison.Ordinal);
        var properties = type.ConfigSchema.GetProperty("properties");
        Assert.Equal("api-connection", properties.GetProperty("connection").GetProperty("x-widget").GetString());
        Assert.Equal("mono", properties.GetProperty("path").GetProperty("x-widget").GetString());
        Assert.Equal(500, properties.GetProperty("path").GetProperty("maxLength").GetInt32());
        Assert.Equal("multiline", properties.GetProperty("query").GetProperty("x-widget").GetString());
        Assert.Equal(["connection", "path", "resultName"], type.ConfigSchema.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal("", type.Defaults.GetProperty("connection").GetString());
        Assert.Equal("/v1/latest", type.Defaults.GetProperty("path").GetString());
        Assert.Equal("base = invoice.currency", type.Defaults.GetProperty("query").GetString());
        Assert.Equal("api", type.Defaults.GetProperty("resultName").GetString());
        Assert.Equal(64, type.Defaults.GetProperty("maxResponseKB").GetInt32());
        Assert.Equal(15, type.Defaults.GetProperty("timeoutSeconds").GetInt32());
    }

    [Fact]
    public void AGoodNode_HasNoErrors() => Assert.Empty(ErrorsOnFetch(Config()));

    [Fact]
    public void ThePathWithAPiece_IsAccepted() => Assert.Empty(ErrorsOnFetch(Config(path: "/customers/{{customer.number}}/rate")));

    [Fact]
    public void AMissingConnection_IsAnError_OnTheConnectionField()
    {
        var issue = Assert.Single(ErrorsOnFetch(Config(connection: null)));
        Assert.Equal("connection", issue.Field);
    }

    [Fact]
    public void ABadConnectionKey_IsAnError()
    {
        var issue = Assert.Single(ErrorsOnFetch(Config(connection: "Rates API!")));
        Assert.Equal("connection", issue.Field);
    }

    [Theory]
    [InlineData("//x")]
    [InlineData("x")]
    [InlineData("/a b")]
    [InlineData("/a?b=1")]
    [InlineData("/a#b")]
    [InlineData("/a/../b")]
    [InlineData("/a/%2e%2e/b")]
    [InlineData("/a/{{ 1bad }}")]
    [InlineData("/a/{{b")]
    public void ABadPath_IsReported_OnThePathField_WithTheHelpersMessage(string path)
    {
        var issue = Assert.Single(ErrorsOnFetch(Config(path: path)));

        Assert.Equal("path", issue.Field);
        Assert.Equal("config.invalid", issue.Code);
        Assert.Equal(Verso.Application.Executions.ApiPathTemplate.Validate(path), issue.Message);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void ABlankPath_IsReportedOnce_ByTheSchemaCheck(string path)
    {
        var issue = Assert.Single(ErrorsOnFetch(Config(path: path)));

        Assert.Equal("path", issue.Field);
        Assert.Equal("config.required", issue.Code);
    }

    [Fact]
    public void AMissingPath_IsReportedOnce()
    {
        var issue = Assert.Single(ErrorsOnFetch(Config(path: null)));
        Assert.Equal("path", issue.Field);
    }

    [Theory]
    [InlineData("base invoice.currency")]
    [InlineData("a = x\na = y")]
    [InlineData("a = x..y")]
    [InlineData("n = 1x")]
    public void ABadQueryLine_IsReported_OnTheQueryField(string query)
    {
        var issue = Assert.Single(ErrorsOnFetch(Config(query: query)));

        Assert.Equal("query", issue.Field);
        Assert.Equal("config.invalid", issue.Code);
    }

    [Fact]
    public void AnEmptyQuery_IsFine() => Assert.Empty(ErrorsOnFetch(Config(query: "")));

    [Theory]
    [InlineData("1api")]
    [InlineData("has space")]
    [InlineData("a.b")]
    public void ABadResultName_IsAnError(string name)
    {
        var issue = Assert.Single(ErrorsOnFetch(Config(resultName: name)));
        Assert.Equal("resultName", issue.Field);
    }

    [Fact]
    public void TheResultName_Iteration_IsReserved_ForForEachLoops()
    {
        var issue = Assert.Single(ErrorsOnFetch(Config(resultName: "iteration")));

        Assert.Equal("resultName", issue.Field);
        Assert.Contains("reserved", issue.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 15, "maxResponseKB")]
    [InlineData(257, 15, "maxResponseKB")]
    [InlineData(64, 0, "timeoutSeconds")]
    [InlineData(64, 61, "timeoutSeconds")]
    public void ALimit_OutOfRange_IsAnError(int maxResponseKB, int timeoutSeconds, string field)
    {
        var issue = Assert.Single(ErrorsOnFetch(Config(maxResponseKB: maxResponseKB, timeoutSeconds: timeoutSeconds)));
        Assert.Equal(field, issue.Field);
    }

    [Fact]
    public void ThePublishedDefinition_NeedsNoConnectionToExist() => Assert.Empty(ErrorsOnFetch(Config(connection: "not-created-yet")));

    [Fact]
    public void ThePortNotes_ExplainWhereTheRunEnds()
    {
        var failed = Validate(Config(), port: "found").Issues;
        Assert.Contains(failed, i => i.NodeId == "f" && i.Port == "failed"
            && i.Message == "Data that can't make a valid request (for example a missing value for the path or a query parameter) will end the run as Rejected.");
        Assert.Contains(failed, i => i.NodeId == "f" && i.Port == "not-found"
            && i.Message == "A request that finds nothing (HTTP 404, an empty answer or a JSON null) ends the run here.");
    }

    [Fact]
    public void TheQueryNode_IsUnaffected_ByTheFetchRules()
    {
        // A query-database node with a result name and a connection a fetch node would also accept: same outcome as before this node existed.
        var config = new JsonObject
        {
            ["connection"] = "core-banking", ["sql"] = "SELECT name FROM customers WHERE id = @id", ["parameters"] = "id = customer.id",
            ["resultName"] = "iteration", ["maxRows"] = 1, ["timeoutSeconds"] = 15,
        };

        var issues = Validate(config, "query-database").Issues.Where(i => i.Severity == GraphValidator.Error && i.NodeId == "f").ToList();

        var issue = Assert.Single(issues);
        Assert.Equal("resultName", issue.Field);
    }
}
