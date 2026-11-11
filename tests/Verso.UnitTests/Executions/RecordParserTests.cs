using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Verso.Application.Executions;

namespace Verso.UnitTests.Executions;

public sealed class RecordParserTests
{
    private static ParseResult Run(string content, string config = "{}", string field = "content")
    {
        using var configDoc = JsonDocument.Parse(config);
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { [field] = content }));
        var result = RecordParser.Parse(configDoc.RootElement.Clone(), data.RootElement.Clone());
        return result;
    }

    private static string Json(ParseResult r) => r.Data!.Value.GetRawText();

    [Fact]
    public void Json_IsUsedAsIs_AndDetected()
    {
        var r = Run("""  { "invoice": { "number": "INV-1", "total": 12.5 } } """);
        Assert.Equal("json", r.Format);
        Assert.Equal("""{"invoice":{"number":"INV-1","total":12.5}}""", Json(r));
    }

    [Fact]
    public void Json_SingleEntryList_IsUnwrapped_ManyEntries_AreRejected()
    {
        Assert.Equal("""{"a":1}""", Json(Run("""[ { "a": 1 } ]""")));
        var many = Run("""[ { "a": 1 }, { "a": 2 } ]""");
        Assert.Null(many.Data);
        Assert.Equal("parse.not_single", many.Issue!.Code);
        Assert.Contains("2 records", many.Issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordPath_SelectsTheRecord_AndReportsAMissingOne()
    {
        var content = """{ "invoices": { "invoice": { "number": "INV-1" } }, "other": 1 }""";
        Assert.Equal("""{"number":"INV-1"}""", Json(Run(content, """{ "recordPath": "invoices.invoice" }""")));
        Assert.Equal("parse.record_not_found", Run(content, """{ "recordPath": "invoices.nope" }""").Issue!.Code);
    }

    [Fact]
    public void Json_Scalar_IsNotARecord()
    {
        Assert.Equal("parse.not_record", Run("[1]").Issue!.Code);
        Assert.Equal("parse.not_record", Run("""{ "a": 1 }""", """{ "recordPath": "a" }""").Issue!.Code);
    }

    [Fact]
    public void Json_Invalid_ReportsTheLine_NotTheContent()
    {
        var r = Run("{\n  \"secret\": nope\n}");
        Assert.Equal("parse.invalid", r.Issue!.Code);
        Assert.Contains("line 2", r.Issue.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", r.Issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Xml_KeepsRoot_GroupsRepeats_ReadsAttributes_AndNumbers()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!-- a comment -->
            <invoice currency="EUR" xmlns="urn:example">
              <number>INV-2026-1</number><zip>00123</zip><total>4797.28</total>
              <customer><name>Kestrel Logistics Ltd</name></customer>
              <line><qty>2</qty></line><line><qty>3</qty></line>
              <note lang="el">Ευχαριστούμε</note><empty/>
            </invoice>
            """;
        var r = Run(xml, """{ "recordPath": "invoice" }""");
        Assert.Equal("xml", r.Format);
        var note = r.Data!.Value.GetProperty("note");
        Assert.Equal("el", note.GetProperty("@lang").GetString());
        Assert.Equal("Ευχαριστούμε", note.GetProperty("#text").GetString());
        var rest = JsonSerializer.SerializeToNode(r.Data!.Value)!.AsObject();
        rest.Remove("note");
        Assert.Equal(
            """{"@currency":"EUR","number":"INV-2026-1","zip":"00123","total":4797.28,"customer":{"name":"Kestrel Logistics Ltd"},"line":[{"qty":2},{"qty":3}],"empty":""}""",
            rest.ToJsonString());
    }

    [Fact]
    public void Numbers_CanBeLeftAsText()
    {
        var r = Run("<a><total>12.50</total></a>", """{ "numbers": false, "recordPath": "a" }""");
        Assert.Equal("""{"total":"12.50"}""", Json(r));
    }

    [Fact]
    public void Xml_WithDtd_IsRefused_AndInvalidXml_ReportsPosition()
    {
        var dtd = Run("""<!DOCTYPE a [ <!ENTITY x "boom"> ]><a>&x;</a>""");
        Assert.Equal("parse.invalid", dtd.Issue!.Code);
        var broken = Run("<a>\n<b></a>");
        Assert.Equal("parse.invalid", broken.Issue!.Code);
        Assert.Contains("line 2", broken.Issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Xml_TooDeep_IsRefused()
    {
        var deep = string.Concat(Enumerable.Repeat("<a>", 60)) + string.Concat(Enumerable.Repeat("</a>", 60));
        Assert.Equal("parse.too_deep", Run(deep).Issue!.Code);
    }

    [Fact]
    public void Csv_OneRow_BecomesTheRecord_WithQuotedFields()
    {
        var csv = "number,customer,total,note\r\nINV-1,\"Kestrel, Logistics \"\"Ltd\"\"\",4797.28,\"line1\nline2\"\r\n\r\n";
        var r = Run(csv);
        Assert.Equal("csv", r.Format);
        var record = r.Data!.Value;
        Assert.Equal("INV-1", record.GetProperty("number").GetString());
        Assert.Equal("Kestrel, Logistics \"Ltd\"", record.GetProperty("customer").GetString());
        Assert.Equal(4797.28m, record.GetProperty("total").GetDecimal());
        Assert.Equal("line1\nline2", record.GetProperty("note").GetString());
    }

    [Theory]
    [InlineData("a;b\n1;x,y")]
    [InlineData("a\tb\n1\tx,y")]
    [InlineData("a|b\n1|x,y")]
    public void Csv_DetectsTheDelimiter(string csv) => Assert.Equal("""{"a":1,"b":"x,y"}""", Json(Run(csv)));

    [Fact]
    public void Csv_ManyRows_AreRejected_BadShapes_AreReported()
    {
        Assert.Equal("parse.not_single", Run("a,b\n1,2\n3,4").Issue!.Code);
        Assert.Contains("row 2 has 1 columns", Run("a,b\n1").Issue!.Message, StringComparison.Ordinal);
        Assert.Equal("parse.invalid", Run("a,a\n1,2").Issue!.Code);
        Assert.Equal("parse.invalid", Run("a,\n1,2").Issue!.Code);
        Assert.Equal("parse.invalid", Run("a,b\n\"1,2").Issue!.Code);
        Assert.Equal("parse.not_single", Run("a,b").Issue!.Code); // header only: no record
    }

    [Fact]
    public void ExplicitFormat_IsHonoured()
    {
        Assert.Equal("parse.invalid", Run("""{ "a": 1 }""", """{ "format": "xml" }""").Issue!.Code);
        // As CSV the single line is a header without rows: there is no record.
        Assert.Equal("parse.not_single", Run("""{ "a": 1 }""", """{ "format": "csv" }""").Issue!.Code);
    }

    [Fact]
    public void MissingOrEmptyField_IsReported()
    {
        Assert.Equal("parse.no_content", Run("x", field: "other").Issue!.Code);
        Assert.Equal("parse.no_content", Run("   ").Issue!.Code);
        Assert.Equal("""{"a":1}""", Json(Run("""{"a":1}""", """{ "source": "file" }""", field: "file")));
    }

    [Fact]
    public void LegacyEncodings_ReadBase64Bytes()
    {
        var greek = CodePagesEncodingProvider.Instance.GetEncoding("windows-1253")!;
        var xml = "<a><name>Καλημέρα</name></a>";
        var r = Run(Convert.ToBase64String(greek.GetBytes(xml)), """{ "encoding": "windows-1253", "recordPath": "a" }""");
        Assert.Equal("Καλημέρα", r.Data!.Value.GetProperty("name").GetString());

        var latin = Run(Convert.ToBase64String(Encoding.Latin1.GetBytes("n\nCafé")), """{ "encoding": "iso-8859-1" }""");
        Assert.Equal("Café", latin.Data!.Value.GetProperty("n").GetString());

        Assert.Equal("parse.encoding", Run("not base64!", """{ "encoding": "iso-8859-1" }""").Issue!.Code);
    }

    [Fact]
    public void Bom_IsIgnored() => Assert.Equal("""{"a":1}""", Json(Run("﻿{\"a\":1}")));
}
