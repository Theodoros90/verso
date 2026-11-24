using System.Text;
using Verso.Application.Uploads;

namespace Verso.UnitTests.Uploads;

public sealed class UploadRulesTests
{
    private static IReadOnlyList<UploadType> All => UploadRules.ParseAccept("PDF, XML, CSV, JSON, TXT, PNG, JPG");
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Check_accepts_pdf_with_header()
    {
        Assert.Null(UploadRules.Check("a.pdf", Utf8("%PDF-1.7 body"), All));
    }

    [Fact]
    public void Check_rejects_pdf_extension_without_header()
    {
        Assert.Equal("upload.content_mismatch", UploadRules.Check("a.pdf", Utf8("hello"), All)!.Code);
    }

    [Fact]
    public void Check_rejects_csv_that_starts_with_PDF_header()
    {
        Assert.Equal("upload.content_mismatch", UploadRules.Check("a.csv", Utf8("%PDF-1.7 a,b"), All)!.Code);
    }

    [Fact]
    public void Check_rejects_xml_with_invalid_utf8()
    {
        Assert.Equal("upload.content_mismatch", UploadRules.Check("a.xml", [0x3C, 0x61, 0xFF, 0xFE, 0x3E], All)!.Code);
    }

    [Fact]
    public void Check_rejects_text_with_NUL_byte()
    {
        Assert.Equal("upload.content_mismatch", UploadRules.Check("a.txt", [0x61, 0x00, 0x62], All)!.Code);
    }

    [Fact]
    public void Check_accepts_text_types_and_rejects_malformed_json()
    {
        Assert.Null(UploadRules.Check("a.json", Utf8("""{"a":1}"""), All));
        Assert.Null(UploadRules.Check("a.csv", Utf8("a,b\n1,2"), All));
        Assert.Equal("upload.content_mismatch", UploadRules.Check("a.json", Utf8("{not json"), All)!.Code);
    }

    [Fact]
    public void Check_rejects_empty()
    {
        Assert.Equal("upload.empty", UploadRules.Check("a.pdf", [], All)!.Code);
    }

    [Theory]
    [InlineData("a.exe")]
    [InlineData("a.docx")]
    [InlineData("report.pdf.exe")]
    [InlineData("noextension")]
    [InlineData("")]
    public void Check_rejects_extension_not_in_accept(string name)
    {
        Assert.Equal("upload.type_not_allowed", UploadRules.Check(name, Utf8("%PDF-1"), UploadRules.ParseAccept("PDF"))!.Code);
    }

    [Fact]
    public void Check_extension_is_case_insensitive_and_accept_limits_types()
    {
        Assert.Null(UploadRules.Check("A.PDF", Utf8("%PDF-1"), UploadRules.ParseAccept("pdf")));
        Assert.Equal("upload.type_not_allowed", UploadRules.Check("a.xml", Utf8("<a/>"), UploadRules.ParseAccept("PDF"))!.Code);
    }

    [Fact]
    public void Check_accepts_png_and_jpg_headers()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2];
        byte[] jpg = [0xFF, 0xD8, 0xFF, 0xE0, 1];
        Assert.Null(UploadRules.Check("a.png", png, All));
        Assert.Null(UploadRules.Check("a.jpg", jpg, All));
        Assert.Equal("upload.content_mismatch", UploadRules.Check("a.png", jpg, All)!.Code);
        Assert.Equal("upload.content_mismatch", UploadRules.Check("a.jpg", png, All)!.Code);
    }

    [Fact]
    public void MaxBytes_clamps_to_ceiling()
    {
        var ceiling = UploadRules.DefaultCeilingBytes;
        Assert.Equal(26_214_400L, ceiling);
        Assert.Equal(ceiling, UploadRules.MaxBytes("100mb", ceiling));
        Assert.Equal(ceiling, UploadRules.MaxBytes("garbage", ceiling));
        Assert.Equal(1_048_576L, UploadRules.MaxBytes("1mb", ceiling));
        Assert.Equal(10_485_760L, UploadRules.MaxBytes("10mb", ceiling));
        Assert.Equal(ceiling, UploadRules.MaxBytes("25mb", ceiling));
        Assert.Equal(2_000_000L, UploadRules.MaxBytes("25mb", 2_000_000L));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("PDF,,XML", false)]
    [InlineData("DOCX", false)]
    [InlineData("PDF, PDF", false)]
    [InlineData("pdf, xml", true)]
    [InlineData("PDF, XML, CSV", true)]
    public void ValidateAccept_cases(string accept, bool valid)
    {
        Assert.Equal(valid, UploadRules.ValidateAccept(accept) is null);
    }

    [Theory]
    [InlineData("..\\..\\evil.pdf")]
    [InlineData("con.pdf")]
    [InlineData("a/b\\c\u0001\u007f.pdf")]
    [InlineData("a..b.pdf")]
    public void SafeFileName_is_plain(string raw)
    {
        var name = UploadRules.SafeFileName(raw, ".pdf");
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain("..", name, StringComparison.Ordinal);
        Assert.DoesNotContain(name, c => char.IsControl(c));
        Assert.EndsWith(".pdf", name);
    }

    [Fact]
    public void SafeFileName_is_at_most_200_characters()
    {
        var name = UploadRules.SafeFileName(new string('a', 300) + ".pdf", ".pdf");
        Assert.True(name.Length <= 200);
        Assert.EndsWith(".pdf", name);
    }
}
