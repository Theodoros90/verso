using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Verso.Application.Executions;

namespace Verso.Infrastructure.Print;

public sealed class PrintQueueOptions
{
    public string Name { get; set; } = "";
    /// <summary>The folder the print system watches, as the Worker's server sees it.</summary>
    public string Path { get; set; } = "";
}

/// <summary>Configuration section <c>Print</c>: the queues the administrator allows, by key. Authors in the designer only name a key.</summary>
public sealed class PrintOptions
{
    public const string Section = "Print";

    public Dictionary<string, PrintQueueOptions> Queues { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Writes <c>{jobId}.pdf</c> and then <c>{jobId}.json</c> into the queue's folder, each under a temporary name and renamed when
/// complete, so the print system never sees half a file and sees the job file only when the PDF is already there. The job file
/// holds how to print it (queue, envelope, duplex, file name, size, SHA-256), no customer data besides the file name.
/// The Worker's service account needs Modify on the folder; no credentials are stored.
/// </summary>
public sealed class FolderPrintSpooler(IOptions<PrintOptions> options) : IPrintSpooler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task SpoolAsync(PrintJob job, CancellationToken ct)
    {
        if (!options.Value.Queues.TryGetValue(job.Queue, out var queue) || string.IsNullOrWhiteSpace(queue.Path))
        {
            throw new PrintSpoolException("print.queue_unknown", $"The print queue '{job.Queue}' isn't set up on this server (Print:Queues).", retryable: false);
        }

        try
        {
            if (!Directory.Exists(queue.Path))
            {
                throw new DirectoryNotFoundException();
            }

            var pdf = Path.Combine(queue.Path, job.JobId + ".pdf");
            await File.WriteAllBytesAsync(pdf + ".tmp", job.Pdf, ct);
            File.Move(pdf + ".tmp", pdf, overwrite: true);

            var descriptor = new
            {
                jobId = job.JobId,
                queue = job.Queue,
                file = job.JobId + ".pdf",
                fileName = job.FileName,
                sizeBytes = job.Pdf.LongLength,
                sha256 = job.Sha256,
                envelope = job.Envelope,
                duplex = job.Duplex,
                createdAt = job.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            };
            var jobFile = Path.Combine(queue.Path, job.JobId + ".json");
            await File.WriteAllTextAsync(jobFile + ".tmp", JsonSerializer.Serialize(descriptor, Json), ct);
            File.Move(jobFile + ".tmp", jobFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The message names the queue key, never the folder path or the content.
            throw new PrintSpoolException("print.queue_unavailable", $"The folder of print queue '{job.Queue}' can't be written to.", retryable: true, ex);
        }
    }
}
