using System.Text;
using Verso.Application.Documents;
using Verso.Application.Executions;
using Verso.Application.Uploads;
using Verso.Domain.Ingestion;

namespace Verso.Api.Tests;

/// <summary>
/// The run payload of an upload (ADR 0023) and the memory it takes: a text file that cannot fit the data limit is never decoded,
/// and the content checks read the bytes without building strings. Synthetic data only; no database.
/// </summary>
public sealed class UploadServicePayloadTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const int OneMb = 1_048_576;

    private static readonly UploadType Csv = UploadRules.Types["CSV"];

    private static Upload Staged(byte[] content) =>
        Upload.Stage(Guid.NewGuid(), "customers.csv", ".csv", "text/csv", content, DocumentKeys.Sha256Hex(content), "synthetic.operator", DateTimeOffset.UtcNow);

    private static byte[] SyntheticCsv(int bytes)
    {
        var line = Encoding.UTF8.GetBytes("42,Synthetic customer,XX00SYNTHETIC\n");
        var content = new byte[bytes];
        for (var i = 0; i < bytes; i++)
        {
            content[i] = line[i % line.Length];
        }

        return content;
    }

    [Fact]
    public void A_text_file_more_than_three_times_the_data_limit_is_never_decoded()
    {
        var content = SyntheticCsv(3 * ExecutionService.MaxDataChars + 1);
        var upload = Staged(content);
        using (UploadService.Payload(upload, content, Csv)) { } // warm up

        var before = GC.GetAllocatedBytesForCurrentThread();
        using var payload = UploadService.Payload(upload, content, Csv);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.False(payload.RootElement.TryGetProperty("content", out _));
        Assert.Equal(content.Length, payload.RootElement.GetProperty("upload").GetProperty("size").GetInt64());
        output.WriteLine($"Reference-only payload of a {content.Length:N0}-byte CSV: allocated {allocated:N0} bytes.");
        Assert.True(allocated < 64 * 1024,$"Allocated {allocated:N0} bytes for the reference-only payload of a {content.Length:N0}-byte file.");
    }

    [Fact]
    public void A_text_file_within_the_data_limit_still_carries_its_text()
    {
        var content = SyntheticCsv(500_000);
        using var payload = UploadService.Payload(Staged(content), content, Csv);

        Assert.Equal(Encoding.UTF8.GetString(content).Trim(), payload.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public void Checking_and_building_the_payload_of_a_5_MB_csv_allocates_under_4_times_its_size()
    {
        var content = SyntheticCsv(5 * OneMb);
        var accepted = UploadRules.ParseAccept("CSV");
        var upload = Staged(content);
        Assert.Null(UploadRules.Check("customers.csv", content, accepted)); // warm up
        using (UploadService.Payload(upload, content, Csv)) { }

        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(UploadRules.Check("customers.csv", content, accepted));
        using (UploadService.Payload(upload, content, Csv)) { }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        output.WriteLine($"Check + payload of a {content.Length:N0}-byte CSV: allocated {allocated:N0} bytes.");
        Assert.True(allocated < 4L * content.Length,$"Allocated {allocated:N0} bytes for a {content.Length:N0}-byte CSV.");
    }

    [Theory]
    [InlineData(900_000)] // fits: the text is carried
    [InlineData(3_000_000)] // the largest file still decoded; its text is too long and dropped (the worst case)
    public void Checking_and_building_the_payload_of_a_text_file_up_to_3_MB_allocates_under_8_times_its_size(int size)
    {
        var content = SyntheticCsv(size);
        var accepted = UploadRules.ParseAccept("CSV");
        var upload = Staged(content);
        Assert.Null(UploadRules.Check("customers.csv", content, accepted)); // warm up
        using (UploadService.Payload(upload, content, Csv)) { }

        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(UploadRules.Check("customers.csv", content, accepted));
        using (var payload = UploadService.Payload(upload, content, Csv))
        {
            Assert.Equal(size < ExecutionService.MaxDataChars, payload.RootElement.TryGetProperty("content", out _));
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        output.WriteLine($"Check + payload of a {content.Length:N0}-byte CSV: allocated {allocated:N0} bytes.");
        Assert.True(allocated < 8L * content.Length,$"Allocated {allocated:N0} bytes for a {content.Length:N0}-byte CSV.");
    }
}
