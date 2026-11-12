using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Verso.Application.Executions;
using Verso.Infrastructure.Print;

namespace Verso.Api.Tests;

/// <summary>The folder spooler writes the PDF and then the job file into the queue's folder, and refuses queues the administrator didn't set up.</summary>
public sealed class FolderPrintSpoolerTests : IDisposable
{
    private const string Id = "0123456789abcdef0123456789abcdef";
    private readonly string _dir = Directory.CreateTempSubdirectory("verso-spool-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private FolderPrintSpooler Spooler(string? path = null)
    {
        var options = new PrintOptions();
        options.Queues["print-a"] = new PrintQueueOptions { Name = "A", Path = path ?? _dir };
        return new FolderPrintSpooler(Options.Create(options));
    }

    private static PrintJob Job(string queue = "print-a") =>
        new(Id, queue, "INV-1.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 test"), "abc", "dl-window", true, DateTimeOffset.Parse("2026-11-12T10:00:00Z", CultureInfo.InvariantCulture));

    [Fact]
    public async Task WritesThePdfAndTheJobFile_AndLeavesNoTemporaryFiles()
    {
        await Spooler().SpoolAsync(Job(), CancellationToken.None);

        Assert.Equal([Id + ".json", Id + ".pdf"], Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_dir, Id + ".json")));
        var root = json.RootElement;
        Assert.Equal("print-a", root.GetProperty("queue").GetString());
        Assert.Equal(Id + ".pdf", root.GetProperty("file").GetString());
        Assert.Equal("dl-window", root.GetProperty("envelope").GetString());
        Assert.True(root.GetProperty("duplex").GetBoolean());
        Assert.Equal("%PDF-1.7 test", await File.ReadAllTextAsync(Path.Combine(_dir, Id + ".pdf")));
    }

    [Fact]
    public async Task SpoolingTheSameJobAgain_ReplacesTheFiles_InsteadOfAddingAJob()
    {
        var spooler = Spooler();
        await spooler.SpoolAsync(Job(), CancellationToken.None);
        await spooler.SpoolAsync(Job(), CancellationToken.None);

        Assert.Equal(2, Directory.GetFiles(_dir).Length);
    }

    [Fact]
    public async Task AQueueThatIsNotConfigured_IsRefused_WithoutRetry()
    {
        var ex = await Assert.ThrowsAsync<PrintSpoolException>(() => Spooler().SpoolAsync(Job("other-queue"), CancellationToken.None));

        Assert.Equal("print.queue_unknown", ex.Code);
        Assert.False(ex.Retryable);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task AFolderThatDoesNotExist_IsRetryable_AndTheMessageHidesThePath()
    {
        var missing = Path.Combine(_dir, "nope");

        var ex = await Assert.ThrowsAsync<PrintSpoolException>(() => Spooler(missing).SpoolAsync(Job(), CancellationToken.None));

        Assert.Equal("print.queue_unavailable", ex.Code);
        Assert.True(ex.Retryable);
        Assert.DoesNotContain(missing, ex.Message, StringComparison.Ordinal);
    }
}
