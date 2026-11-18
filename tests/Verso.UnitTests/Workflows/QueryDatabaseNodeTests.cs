using System.Text.Json;
using System.Text.Json.Nodes;
using Verso.Application.Executions;
using Verso.Application.Workflows;

namespace Verso.UnitTests.Workflows;

/// <summary>The Query database node (catalog entry, parameter lines and the design-time checks of the graph validator).</summary>
public sealed class QueryDatabaseNodeTests
{
    private const string GoodSql = "SELECT name FROM customers WHERE id = @id";

    private static JsonObject Config(string? sql = GoodSql, string? parameters = "id = customer.id", string? connection = "core-banking", string? resultName = "db", int maxRows = 1, int timeoutSeconds = 15)
    {
        var config = new JsonObject { ["maxRows"] = maxRows, ["timeoutSeconds"] = timeoutSeconds };
        if (connection is not null) config["connection"] = connection;
        if (sql is not null) config["sql"] = sql;
        if (parameters is not null) config["parameters"] = parameters;
        if (resultName is not null) config["resultName"] = resultName;
        return config;
    }

    private static WorkflowValidationDto Validate(JsonObject config)
    {
        var definition = new JsonObject
        {
            ["nodes"] = new JsonArray(
                new JsonObject { ["id"] = "trigger", ["type"] = "api-trigger", ["title"] = "Trigger", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 }, ["config"] = new JsonObject { ["path"] = "/hooks/x", ["auth"] = "none" } },
                new JsonObject { ["id"] = "q", ["type"] = "query-database", ["title"] = "Query", ["position"] = new JsonObject { ["x"] = 200, ["y"] = 0 }, ["config"] = config },
                new JsonObject { ["id"] = "archive", ["type"] = "archive", ["title"] = "Archive", ["position"] = new JsonObject { ["x"] = 400, ["y"] = 0 }, ["config"] = new JsonObject { ["folderPattern"] = "x", ["retention"] = "7y", ["metadata"] = new JsonArray() } }),
            ["edges"] = new JsonArray(
                new JsonObject { ["id"] = "e1", ["source"] = "trigger", ["sourcePort"] = "out", ["target"] = "q", ["targetPort"] = "in" },
                new JsonObject { ["id"] = "e2", ["source"] = "q", ["sourcePort"] = "found", ["target"] = "archive", ["targetPort"] = "in" }),
        };
        using var doc = JsonDocument.Parse(definition.ToJsonString());
        var (parsed, errors) = WorkflowDefinitionParser.Parse(doc.RootElement);
        Assert.True(parsed is not null, string.Join("; ", errors.Select(e => $"{e.Key}: {e.Value[0]}")));
        return GraphValidator.Validate(parsed!, new Dictionary<Guid, TemplateRef>());
    }

    private static List<WorkflowIssueDto> ErrorsOnQuery(JsonObject config) =>
        [.. Validate(config).Issues.Where(i => i.Severity == GraphValidator.Error && i.NodeId == "q")];

    [Fact]
    public void Catalog_HasTheNode_WithItsPortsAndSettings()
    {
        var type = Assert.Single(NodeCatalog.All, t => t.Type == "query-database");

        Assert.Equal(NodeCatalog.QueryDatabase, type.Type);
        Assert.Equal("process", type.Category);
        Assert.Equal(["in"], type.Inputs.Select(p => p.Id));
        Assert.Equal(["found", "not-found", "failed"], type.Outputs.Select(p => p.Id));
        Assert.False(type.Outputs.Single(p => p.Id == "failed").Required);
        var properties = type.ConfigSchema.GetProperty("properties");
        Assert.Equal("database-connection", properties.GetProperty("connection").GetProperty("x-widget").GetString());
        Assert.Equal("sql", properties.GetProperty("sql").GetProperty("x-widget").GetString());
        Assert.Equal(4000, properties.GetProperty("sql").GetProperty("maxLength").GetInt32());
        Assert.Equal("multiline", properties.GetProperty("parameters").GetProperty("x-widget").GetString());
        Assert.Equal(["connection", "sql", "resultName"], type.ConfigSchema.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal("db", type.Defaults.GetProperty("resultName").GetString());
        Assert.Equal(1, type.Defaults.GetProperty("maxRows").GetInt32());
        Assert.Equal(15, type.Defaults.GetProperty("timeoutSeconds").GetInt32());
    }

    [Fact]
    public void ParseParameters_ReadsOneMappingPerLine_IgnoringBlankLines()
    {
        var (items, problem) = DatabaseQueryStep.ParseParameters("id = customer.id\n\n  region=address.region  \r\n");

        Assert.Null(problem);
        Assert.Equal([("id", "customer.id"), ("region", "address.region")], items);
    }

    [Theory]
    [InlineData("id customer.id", "id customer.id")]
    [InlineData("id = a\nid = b", "id")]
    [InlineData("1id = a", "1id")]
    [InlineData("@id = a", "@id")]
    [InlineData("id = ", "id")]
    [InlineData("id = a..b", "id")]
    [InlineData("= a", "")]
    public void ParseParameters_ReportsAProblem_ForABadLine(string text, string mentions)
    {
        var (_, problem) = DatabaseQueryStep.ParseParameters(text);

        Assert.NotNull(problem);
        Assert.Contains(mentions, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseParameters_AcceptsEmptyText_AndLimitsTheLines()
    {
        Assert.Null(DatabaseQueryStep.ParseParameters("").Problem);
        var twentyOne = string.Join('\n', Enumerable.Range(1, 21).Select(i => $"p{i} = a.b"));

        Assert.NotNull(DatabaseQueryStep.ParseParameters(twentyOne).Problem);
        Assert.Null(DatabaseQueryStep.ParseParameters(string.Join('\n', Enumerable.Range(1, 20).Select(i => $"p{i} = a.b"))).Problem);
    }

    [Fact]
    public void AGoodNode_HasNoErrors()
    {
        Assert.Empty(ErrorsOnQuery(Config()));
    }

    [Fact]
    public void AMissingConnection_IsAnError_OnTheConnectionField()
    {
        var issue = Assert.Single(ErrorsOnQuery(Config(connection: null)));
        Assert.Equal("connection", issue.Field);
    }

    [Fact]
    public void ABadConnectionKey_IsAnError()
    {
        var issue = Assert.Single(ErrorsOnQuery(Config(connection: "Core Banking!")));
        Assert.Equal("connection", issue.Field);
    }

    [Theory]
    [InlineData("DELETE FROM customers WHERE id = @id")]
    [InlineData("SELECT 1; SELECT 2")]
    public void ASqlGuardProblem_IsReported_WithTheGuardsMessage(string sql)
    {
        var issue = Assert.Single(ErrorsOnQuery(Config(sql: sql, parameters: "")));

        Assert.Equal("sql", issue.Field);
        Assert.Equal(SqlGuard.Check(sql).Message, issue.Message);
    }

    [Fact]
    public void APlaceholderWithoutAMapping_IsAnError_NamingIt()
    {
        var issue = Assert.Single(ErrorsOnQuery(Config(sql: "SELECT name FROM customers WHERE id = @id AND region = @region")));

        Assert.Equal("parameters", issue.Field);
        Assert.Contains("@region", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMappingWithoutAPlaceholder_IsAnError_NamingIt()
    {
        var issue = Assert.Single(ErrorsOnQuery(Config(parameters: "id = customer.id\nregion = address.region")));

        Assert.Equal("parameters", issue.Field);
        Assert.Contains("region", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateMappingNames_AreAnError()
    {
        var issue = Assert.Single(ErrorsOnQuery(Config(parameters: "id = customer.id\nid = other.id")));

        Assert.Equal("parameters", issue.Field);
        Assert.Contains("id", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMalformedMappingLine_IsAnError()
    {
        var issue = Assert.Single(ErrorsOnQuery(Config(parameters: "id customer.id")));

        Assert.Equal("parameters", issue.Field);
    }

    [Theory]
    [InlineData("1db")]
    [InlineData("has space")]
    [InlineData("a.b")]
    public void ABadResultName_IsAnError(string name)
    {
        var issue = Assert.Single(ErrorsOnQuery(Config(resultName: name)));
        Assert.Equal("resultName", issue.Field);
    }

    [Theory]
    [InlineData(0, 15, "maxRows")]
    [InlineData(101, 15, "maxRows")]
    [InlineData(1, 0, "timeoutSeconds")]
    [InlineData(1, 61, "timeoutSeconds")]
    public void ARangeSetting_OutOfRange_IsAnError(int maxRows, int timeoutSeconds, string field)
    {
        var issue = Assert.Single(ErrorsOnQuery(Config(maxRows: maxRows, timeoutSeconds: timeoutSeconds)));
        Assert.Equal(field, issue.Field);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void WhitespaceOnlySql_IsAnError_OnTheSqlField(string sql)
    {
        // The schema check trims before testing "required", so whitespace-only text is reported exactly once, on the sql field.
        var issue = Assert.Single(ErrorsOnQuery(Config(sql: sql, parameters: "")));

        Assert.Equal("sql", issue.Field);
        Assert.Equal("config.required", issue.Code);
    }

    [Fact]
    public void TheResultName_Iteration_IsReserved_ForForEachLoops()
    {
        var issue = Assert.Single(ErrorsOnQuery(Config(resultName: "iteration")));

        Assert.Equal("resultName", issue.Field);
        Assert.Contains("iteration", issue.Message, StringComparison.Ordinal);
        Assert.Contains("reserved", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASystemVariable_LikeAtAtVersion_IsNotAPlaceholder()
    {
        Assert.Empty(ErrorsOnQuery(Config(sql: "SELECT @@version AS v", parameters: "")));
    }

    [Fact]
    public void PlaceholderNames_AreCaseSensitive_SoAtIdNeedsAMappingCalledId()
    {
        var issues = ErrorsOnQuery(Config(sql: "SELECT name FROM customers WHERE id = @Id", parameters: "id = customer.id"));

        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, i => i.Message.Contains("@Id", StringComparison.Ordinal) && i.Message.Contains("no parameter line", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Message.Contains("'id' isn't used", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOracleStyleColonName_IsPlainText_NotAPlaceholder()
    {
        // Authors always write @name (the Worker rewrites it for Oracle). A :name is not seen as a placeholder: it needs no mapping,
        // and a mapping for it is reported as unused.
        Assert.Empty(ErrorsOnQuery(Config(sql: "SELECT name FROM customers WHERE id = :id", parameters: "")));
        var issue = Assert.Single(ErrorsOnQuery(Config(sql: "SELECT name FROM customers WHERE id = :id", parameters: "id = customer.id")));
        Assert.Contains("isn't used", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFailedPort_ExplainsThatBadDataEndsTheRunAsRejected()
    {
        var result = Validate(Config());

        Assert.Contains(result.Issues, i => i.NodeId == "q" && i.Port == "failed"
            && i.Message == "Data that can't make a valid query (for example a missing value for a parameter) will end the run as Rejected.");
    }

    [Fact]
    public void ThePublishedDefinition_NeedsNoConnectionToExist()
    {
        // Like the email relay: the key's shape is checked, its existence is not (connections can be created after the workflow).
        Assert.Empty(ErrorsOnQuery(Config(connection: "not-created-yet")));
    }
}
