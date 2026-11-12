using System.Text.Json;
using Verso.Application.Documents;
using Verso.Rendering.Binding;

namespace Verso.UnitTests.Executions;

public sealed class DocumentNamesTests
{
    private static readonly JsonElement Data = Parse("""
        { "invoice": { "number": "INV-2026-003901", "date": "2026-11-02" }, "customer": { "name": "Kestrel/../Logistics <Ltd>" } }
        """);

    [Fact]
    public void PatternText_ResolvesTokensAndFilters_AndReportsMissingValues()
    {
        var resolved = PatternText.Resolve("invoices/{{invoice.date|year}}/{{invoice.date|month}}/{{invoice.number}}-{{customer.id}}", Data);

        Assert.Equal("invoices/2026/11/INV-2026-003901-", resolved.Text);
        Assert.Equal(["customer.id"], resolved.Missing);
    }

    [Theory]
    [InlineData("{{invoice.number}}.pdf", "INV-2026-003901.pdf")]
    [InlineData("{{invoice.number}}", "INV-2026-003901.pdf")]
    [InlineData("{{customer.name}}.PDF", "Kestrel_.._Logistics _Ltd_.pdf")]
    [InlineData("../../etc/passwd", "_.._etc_passwd.pdf")]
    [InlineData("{{missing.value}}.pdf", "document.pdf")]
    [InlineData("C:\\temp\\x.pdf", "C__temp_x.pdf")]
    public void FileName_IsSanitized(string pattern, string expected)
    {
        Assert.Equal(expected, DocumentNames.FileName(PatternText.Resolve(pattern, Data).Text));
    }

    [Theory]
    [InlineData("invoices/{{invoice.date|year}}/{{invoice.date|month}}", "invoices/2026/11")]
    [InlineData("customers/{{customer.name}}", "customers/Kestrel/Logistics _Ltd_")]
    [InlineData("../../{{invoice.number}}//x/./", "INV-2026-003901/x")]
    [InlineData("a\\b", "a/b")]
    [InlineData("{{missing.value}}", "")]
    public void FolderPath_IsSanitized_AndCannotClimb(string pattern, string expected)
    {
        Assert.Equal(expected, DocumentNames.FolderPath(PatternText.Resolve(pattern, Data).Text));
    }

    [Theory]
    [InlineData("{{invoice.number}}.docx", "INV-2026-003901.docx")]
    [InlineData("{{invoice.number}}", "INV-2026-003901.docx")]
    [InlineData("{{invoice.number}}.DOCX", "INV-2026-003901.docx")]
    [InlineData("{{invoice.number}}.pdf", "INV-2026-003901.pdf.docx")]
    [InlineData("{{missing.value}}", "document.docx")]
    public void FileName_Docx_AlwaysEndsInDocx(string pattern, string expected)
    {
        Assert.Equal(expected, DocumentNames.FileName(PatternText.Resolve(pattern, Data).Text, DocumentNames.Docx));
    }

    [Fact]
    public void Keys_FollowTheDocumentType()
    {
        var execution = Guid.Parse("0193a6c2-0000-7000-8000-000000000001");
        Assert.Equal("staging/0193a6c2000070008000000000000001/word.docx", DocumentKeys.Staging(execution, "word", 0, DocumentNames.Docx));
        Assert.Equal("archive/2026/11/0193a6c2000070008000000000000001.docx",
            DocumentKeys.Archive(execution, new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero), DocumentNames.ExtensionFor(Verso.Domain.Documents.Document.DocxContentType)));
        Assert.Equal(".pdf", DocumentNames.ExtensionFor(Verso.Domain.Documents.Document.PdfContentType));
    }

    [Fact]
    public void Keys_AreDeterministic()
    {
        var execution = Guid.Parse("0193a6c2-0000-7000-8000-000000000001");
        Assert.Equal("staging/0193a6c2000070008000000000000001/pdf.pdf", DocumentKeys.Staging(execution, "pdf"));
        Assert.Equal("archive/2026/11/0193a6c2000070008000000000000001.pdf",
            DocumentKeys.Archive(execution, new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void ArchiveKey_UsesUtcDate()
    {
        var id = Guid.Parse("0193a6c2-0000-7000-8000-000000000001");
        // 30 Nov 23:30 at UTC−2 is 1 Dec in UTC.
        Assert.Equal("archive/2026/12/0193a6c2000070008000000000000001.pdf",
            DocumentKeys.Archive(id, new DateTimeOffset(2026, 11, 30, 23, 30, 0, TimeSpan.FromHours(-2))));
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
