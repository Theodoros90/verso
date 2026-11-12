using System.Text.RegularExpressions;

namespace Verso.Application.Executions;

/// <summary>One print job: the PDF and how to print it. <see cref="JobId"/> is the same for every attempt of the same step, so a retry rewrites the same files.</summary>
public sealed record PrintJob(string JobId, string Queue, string FileName, byte[] Pdf, string Sha256, string Envelope, bool Duplex, DateTimeOffset CreatedAt);

/// <summary>A job that can't be spooled. <see cref="Retryable"/>: a later attempt may work (folder unavailable); otherwise it won't (queue not configured).</summary>
public sealed class PrintSpoolException(string code, string message, bool retryable, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

/// <summary>
/// Hands a print job to the print system (ADR 0007): writes the PDF and a small job file into the folder of a queue the
/// administrator configured. Implemented in Infrastructure, registered in the Worker only. Workflow authors name a queue
/// by key and never type a path.
/// </summary>
public interface IPrintSpooler
{
    Task SpoolAsync(PrintJob job, CancellationToken ct);
}

/// <summary>Rules for the print queue step's settings.</summary>
public static partial class PrintQueues
{
    public const string DefaultEnvelope = "dl-window";

    public static readonly IReadOnlyDictionary<string, string> Envelopes = new Dictionary<string, string>
    {
        ["dl-window"] = "DL window",
        ["c4"] = "C4 large",
    };

    /// <summary>A queue key as typed in the step: lower-case letters, digits and hyphens.</summary>
    public const string KeyPattern = "^[a-z0-9][a-z0-9-]{1,63}$";

    public static bool IsValidKey(string? key) => key is not null && KeyRegex().IsMatch(key);

    [GeneratedRegex(KeyPattern, RegexOptions.CultureInvariant)]
    private static partial Regex KeyRegex();
}
