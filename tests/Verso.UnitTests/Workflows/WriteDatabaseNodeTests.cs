using System.Text.Json;
using System.Text.Json.Nodes;
using Verso.Application.Executions;
using Verso.Application.Workflows;

namespace Verso.UnitTests.Workflows;

/// <summary>The Write to database node (catalog entry and the design-time checks of the graph validator).</summary>
public sealed class WriteDatabaseNodeTests
{
    private const string GoodSql = "UPDATE customers SET status = @status WHERE id = @id";
    private const string GoodParameters = "status = customer.status\nid = customer.id";

    private static JsonObject Config(string? sql = GoodSql, string? parameters = GoodParameters, string? connection = "core-banking", int maxRows = 1, int timeoutSeconds = 15)
    {
        var config = new JsonObject { ["maxRows"] = maxRows, ["timeoutSeconds"] = timeoutSeconds };
        if (connection is not null) config["connection"] = connection;
        if (sql is not null) config["sql"] = sql;
        if (parameters is not null) config["parameters"] = parameters;
        return config;
    }

    private static JsonObject QueryConfig(string sql, string parameters) =>
        new() { ["connection"] = "core-banking", ["sql"] = sql, ["parameters"] = parameters, ["resultName"] = "db", ["maxRows"] = 1, ["timeoutSeconds"] = 15 };

    private static JsonObject Node(string id, string type, JsonObject config, int x) =>
        new() { ["id"] = id, ["type"] = type, ["title"] = id, ["position"] = new JsonObject { ["x"] = x, ["y"] = 0 }, ["config"] = config };

    private static WorkflowValidationDto Validate(params (string Id, string Type, JsonObject Config)[] steps)
    {
        var nodes = new JsonArray(Node("trigger", "api-trigger", new JsonObject { ["path"] = "/hooks/x", ["auth"] = "none" }, 0));
        var edges = new JsonArray();
        var (previous, previousPort) = ("trigger", "out");
        var x = 200;
        foreach (var (id, type, config) in steps)
        {
            nodes.Add(Node(id, type, config, x));
            edges.Add(new JsonObject { ["id"] = "e-" + id, ["source"] = previous, ["sourcePort"] = previousPort, ["target"] = id, ["targetPort"] = "in" });
            (previous, previousPort) = (id, type == "query-database" ? "found" : "written");
            x += 200;
        }

        nodes.Add(Node("archive", "archive", new JsonObject { ["folderPattern"] = "x", ["retention"] = "7y", ["metadata"] = new JsonArray() }, x));
        edges.Add(new JsonObject { ["id"] = "e-archive", ["source"] = previous, ["sourcePort"] = previousPort, ["target"] = "archive", ["targetPort"] = "in" });
        using var doc = JsonDocument.Parse(new JsonObject { ["nodes"] = nodes, ["edges"] = edges }.ToJsonString());
        var (parsed, errors) = WorkflowDefinitionParser.Parse(doc.RootElement);
        Assert.True(parsed is not null, string.Join("; ", errors.Select(e => $"{e.Key}: {e.Value[0]}")));
        return GraphValidator.Validate(parsed!, new Dictionary<Guid, TemplateRef>());
    }

    private static List<WorkflowIssueDto> ErrorsOn(WorkflowValidationDto result, string nodeId) =>
        [.. result.Issues.Where(i => i.Severity == GraphValidator.Error && i.NodeId == nodeId)];

    private static List<WorkflowIssueDto> ErrorsOnWrite(JsonObject config) => ErrorsOn(Validate(("w", "write-database", config)), "w");

    [Fact]
    public void Catalog_HasTheNode_WithItsPortsAndSettings()
    {
        var type = Assert.Single(NodeCatalog.All, t => t.Type == "write-database");

        Assert.Equal(NodeCatalog.WriteDatabase, type.Type);
        Assert.Equal("output", type.Category);
        Assert.Equal(["in"], type.Inputs.Select(p => p.Id));
        Assert.Equal(["written", "failed"], type.Outputs.Select(p => p.Id));
        Assert.True(type.Outputs.Single(p => p.Id == "written").Required);
        Assert.False(type.Outputs.Single(p => p.Id == "failed").Required);
        var properties = type.ConfigSchema.GetProperty("properties");
        var connection = properties.GetProperty("connection");
        Assert.Equal("database-connection", connection.GetProperty("x-widget").GetString());
        Assert.True(connection.GetProperty("x-requires-writes").GetBoolean());
        Assert.Equal("sql", properties.GetProperty("sql").GetProperty("x-widget").GetString());
        Assert.Equal(4000, properties.GetProperty("sql").GetProperty("maxLength").GetInt32());
        Assert.Equal("multiline", properties.GetProperty("parameters").GetProperty("x-widget").GetString());
        Assert.False(properties.TryGetProperty("resultName", out _));
        Assert.Equal(1000, properties.GetProperty("maxRows").GetProperty("maximum").GetInt32());
        Assert.Equal(["connection", "sql"], type.ConfigSchema.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(1, type.Defaults.GetProperty("maxRows").GetInt32());
        Assert.Equal(15, type.Defaults.GetProperty("timeoutSeconds").GetInt32());
        Assert.Contains("never retried automatically and never repeated on resume", type.Description, StringComparison.Ordinal);
        Assert.Contains("A failed run may have partly happened", type.Description, StringComparison.Ordinal); // spec Design 6 helper text
    }

    [Theory]
    [InlineData("INSERT INTO audit_log (customer_id, note) VALUES (@id, @note)", "id = customer.id\nnote = customer.note")]
    [InlineData(GoodSql, GoodParameters)]
    [InlineData("DELETE FROM customers WHERE id = @id", "id = customer.id")]
    public void AValidWriteNode_HasNoErrors(string sql, string parameters)
    {
        Assert.Empty(ErrorsOnWrite(Config(sql: sql, parameters: parameters)));
    }

    [Fact]
    public void AMissingConnection_IsAnError_OnTheConnectionField()
    {
        var issue = Assert.Single(ErrorsOnWrite(Config(connection: null)));
        Assert.Equal("connection", issue.Field);
    }

    [Fact]
    public void ABadConnectionKey_IsAnError()
    {
        var issue = Assert.Single(ErrorsOnWrite(Config(connection: "Core Banking!")));
        Assert.Equal("connection", issue.Field);
    }

    [Theory]
    [InlineData("SELECT name FROM customers WHERE id = @id", "id = customer.id")]
    [InlineData("UPDATE customers SET status = 'x' WHERE id = 1; DELETE FROM customers WHERE id = 2", "")]
    [InlineData("UPDATE customers SET status = @status", "status = customer.status")]
    [InlineData("UPDATE customers SET status = 'x' WHERE id = 1; COMMIT", "")]
    public void ASqlGuardWriteProblem_IsReported_WithTheGuardsMessage(string sql, string parameters)
    {
        var issue = Assert.Single(ErrorsOnWrite(Config(sql: sql, parameters: parameters)));

        Assert.Equal("sql", issue.Field);
        Assert.Equal(SqlGuard.CheckWrite(sql).Message, issue.Message);
    }

    [Fact]
    public void APlaceholderWithoutAMapping_IsAnError_NamingIt()
    {
        var issue = Assert.Single(ErrorsOnWrite(Config(parameters: "id = customer.id")));

        Assert.Equal("parameters", issue.Field);
        Assert.Contains("@status", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMappingWithoutAPlaceholder_IsAnError_NamingIt()
    {
        var issue = Assert.Single(ErrorsOnWrite(Config(parameters: GoodParameters + "\nregion = address.region")));

        Assert.Equal("parameters", issue.Field);
        Assert.Contains("region", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateMappingNames_AreAnError_IgnoringCase()
    {
        var issue = Assert.Single(ErrorsOnWrite(Config(parameters: GoodParameters + "\nID = other.id")));

        Assert.Equal("parameters", issue.Field);
        Assert.Contains("ID", issue.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 15, "maxRows")]
    [InlineData(1001, 15, "maxRows")]
    [InlineData(1, 0, "timeoutSeconds")]
    [InlineData(1, 61, "timeoutSeconds")]
    public void ARangeSetting_OutOfRange_IsAnError(int maxRows, int timeoutSeconds, string field)
    {
        var issue = Assert.Single(ErrorsOnWrite(Config(maxRows: maxRows, timeoutSeconds: timeoutSeconds)));
        Assert.Equal(field, issue.Field);
    }

    [Fact]
    public void ThousandRows_IsTheMostAWriteMayChange()
    {
        Assert.Empty(ErrorsOnWrite(Config(maxRows: 1000)));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void WhitespaceOnlySql_IsReportedExactlyOnce_OnTheSqlField(string sql)
    {
        var issue = Assert.Single(ErrorsOnWrite(Config(sql: sql, parameters: "")));

        Assert.Equal("sql", issue.Field);
        Assert.Equal("config.required", issue.Code);
    }

    [Fact]
    public void TheFailedPort_ExplainsThatBadDataEndsTheRunAsRejected()
    {
        var result = Validate(("w", "write-database", Config()));

        Assert.Contains(result.Issues, i => i.NodeId == "w" && i.Port == "failed"
            && i.Message == "Data that can't make a valid statement (for example a missing value for a parameter) will end the run as Rejected.");
    }

    [Fact]
    public void AQueryNodeAndAWriteNode_AreEachValidatedByTheirOwnRules()
    {
        var clean = Validate(("q", "query-database", QueryConfig("SELECT name FROM customers WHERE id = @id", "id = customer.id")), ("w", "write-database", Config()));
        Assert.Empty(ErrorsOn(clean, "q"));
        Assert.Empty(ErrorsOn(clean, "w"));

        var swapped = Validate(
            ("q", "query-database", QueryConfig("INSERT INTO t (a) VALUES (@a)", "a = x.a")),
            ("w", "write-database", Config(sql: "SELECT name FROM customers WHERE id = @id", parameters: "id = customer.id")));
        Assert.Equal("sql", Assert.Single(ErrorsOn(swapped, "q")).Field);
        Assert.Equal("sql", Assert.Single(ErrorsOn(swapped, "w")).Field);
    }
}
