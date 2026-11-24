namespace Verso.Domain.Ingestion;

/// <summary>Staged (file kept, waiting for a run) → Taken (a run consumed it, bytes dropped) or Expired (never used, bytes dropped).</summary>
public enum UploadStatus
{
    Staged,
    Taken,
    Expired,
}

/// <summary>
/// One file uploaded for a workflow's File upload step (ADR 0023). The bytes are kept only while it is
/// <see cref="UploadStatus.Staged"/>; the record (name, size, hash) stays as the trace.
/// </summary>
public sealed class Upload
{
    public const int MaxFileNameLength = 200;
    public const int MaxExtensionLength = 16;
    public const int MaxContentTypeLength = 100;

    private Upload() { } // EF Core

    public Guid Id { get; private set; }
    public Guid WorkflowId { get; private set; }

    /// <summary>Sanitised (no path, at most 200 characters).</summary>
    public string FileName { get; private set; } = "";

    /// <summary>Lowercase with the dot, e.g. <c>.pdf</c>.</summary>
    public string Extension { get; private set; } = "";

    public string ContentType { get; private set; } = "";
    public long Size { get; private set; }

    /// <summary>SHA-256 of the bytes, lowercase hex (64 characters).</summary>
    public string Sha256 { get; private set; } = "";

    /// <summary>The file's bytes while Staged; null once Taken or Expired.</summary>
    public byte[]? Content { get; private set; }

    public UploadStatus Status { get; private set; }
    public Guid? ExecutionId { get; private set; }
    public string UploadedBy { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }

    public static Upload Stage(Guid workflowId, string fileName, string extension, string contentType, byte[] content, string sha256, string uploadedBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadedBy);
        if (fileName.Length > MaxFileNameLength)
        {
            throw new ArgumentException($"File name is longer than {MaxFileNameLength} characters.", nameof(fileName));
        }

        return new Upload
        {
            Id = Guid.CreateVersion7(now),
            WorkflowId = workflowId,
            FileName = fileName,
            Extension = extension,
            ContentType = contentType,
            Size = content.LongLength,
            Sha256 = sha256,
            Content = content,
            Status = UploadStatus.Staged,
            UploadedBy = uploadedBy,
            CreatedAt = now,
        };
    }

    public void AttachExecution(Guid executionId) => ExecutionId = executionId;

    /// <summary>A run consumed the file. The bytes are dropped; calling it again changes nothing.</summary>
    public void MarkTaken()
    {
        Status = UploadStatus.Taken;
        Content = null;
    }

    /// <summary>The file was never used. Only a Staged upload expires.</summary>
    public void Expire()
    {
        if (Status != UploadStatus.Staged)
        {
            throw new DomainRuleException("upload.state", $"Upload {Id} is {Status}, not {UploadStatus.Staged}.");
        }

        Status = UploadStatus.Expired;
        Content = null;
    }
}
