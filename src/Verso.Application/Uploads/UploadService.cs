using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Verso.Application.Documents;
using Verso.Application.Executions;
using Verso.Application.Workflows;
using Verso.Domain.Audit;
using Verso.Domain.Ingestion;

namespace Verso.Application.Uploads;

/// <summary>
/// A person uploads one file to a workflow that starts with File upload (ADR 0023). In order: the workflow exists; it is active,
/// published and its latest published version starts with a File upload step for signed-in staff; the file is within the size
/// limit and passes <see cref="UploadRules.Check"/>; the same file (SHA-256) was not uploaded to this workflow before. Then the
/// bytes are staged in <c>ingestion.Uploads</c> and a run is submitted whose input is a reference to the file (plus the text of a
/// small text file), never the bytes. The staged row, the run and the <c>FileUploaded</c> audit entry are committed together.
/// The API never writes the document store: the Worker copies the bytes there when the run starts.
/// </summary>
public sealed partial class UploadService(
    IVersoDbContext db, ExecutionService executions, ICurrentUser user, TimeProvider clock, IOptions<UploadOptions> options, ILogger<UploadService> logger)
{
    public const string EntityType = "Upload";

    /// <summary>The run's idempotency key is this prefix plus the upload id (<c>N</c> format).</summary>
    public const string KeyPrefix = "upload:";

    public static class Actions
    {
        /// <summary><c>{ uploadId, workflowKey, fileName (sanitised), size, sha256 }</c>, never content or the name as sent.</summary>
        public const string FileUploaded = "FileUploaded";

        /// <summary><c>{ workflowKey, rule }</c>: only the code of the rule that failed.</summary>
        public const string FileUploadRejected = "FileUploadRejected";
    }

    /// <summary>The payload keeps the text as it is (no <c><</c> for every <c>&lt;</c>), so an XML file is not inflated past the data limit.</summary>
    private static readonly JsonSerializerOptions PayloadJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly UTF8Encoding Utf8 = new(false, false);

    public async Task<Result<UploadSettingsDto>> SettingsAsync(string workflowKey, CancellationToken ct)
    {
        var target = await FindTargetAsync(workflowKey, ct);
        if (target.Error is not null)
        {
            return target.Error;
        }

        List<string> accept = [];
        foreach (var type in target.Types)
        {
            accept.Add(type.Extension);
            if (type.Extension == ".jpg")
            {
                accept.Add(".jpeg"); // accepted as JPG too, so the dialog refuses exactly what the server refuses
            }
        }

        return new UploadSettingsDto(accept, target.MaxBytes);
    }

    /// <summary>
    /// The endpoint stopped reading a file larger than <see cref="UploadSettingsDto.MaxBytes"/> (or a body over the request limit):
    /// audits <c>FileUploadRejected</c> with the rule and returns the same <c>upload.too_large</c> error <see cref="AcceptAsync"/> gives.
    /// </summary>
    public async Task<AppError> TooLargeAsync(string workflowKey, CancellationToken ct)
    {
        var target = await FindTargetAsync(workflowKey, ct);
        if (target.Error is not null)
        {
            return target.Error;
        }

        var problem = TooLarge(target.MaxBytes);
        await RejectAsync(target.WorkflowId, workflowKey, problem.Code, ct);
        return ToError(problem);
    }

    private static UploadProblem TooLarge(long maxBytes) =>
        new("upload.too_large", $"The file is larger than the largest file this workflow accepts ({maxBytes:N0} bytes).");

    private static AppError ToError(UploadProblem problem) =>
        new(ErrorKind.Validation, problem.Code, problem.Message, new Dictionary<string, string[]> { ["file"] = [problem.Message] });

    public async Task<Result<Submission>> AcceptAsync(string workflowKey, string fileName, byte[] content, string? correlationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var target = await FindTargetAsync(workflowKey, ct);
        if (target.Error is not null)
        {
            return target.Error;
        }

        var problem = content.LongLength > target.MaxBytes ? TooLarge(target.MaxBytes) : UploadRules.Check(fileName, content, target.Types);
        if (problem is not null)
        {
            await RejectAsync(target.WorkflowId, workflowKey, problem.Code, ct);
            return ToError(problem);
        }

        var type = UploadRules.Match(fileName, target.Types)!; // Check passed, so the extension matched
        var sha256 = DocumentKeys.Sha256Hex(content);
        if (await FindDuplicateAsync(target.WorkflowId, sha256, ct) is { } duplicate)
        {
            return duplicate;
        }

        var now = clock.GetUtcNow();
        var upload = Upload.Stage(target.WorkflowId, StoredName(fileName, type.Extension), type.Extension, type.ContentType, content, sha256, user.Id, now);
        var audit = AuditEntry.Record(Actions.FileUploaded, EntityType, upload.Id.ToString(), user.Id, user.Name, now,
            JsonSerializer.Serialize(new { uploadId = upload.Id, workflowKey, fileName = upload.FileName, size = upload.Size, sha256 }),
            Activity.Current?.TraceId.ToHexString());

        // Tracked now, saved by the submission's own SaveChanges: the row, the run and the audit entry commit together or not at all.
        db.Uploads.Add(upload);
        db.AuditEntries.Add(audit);
        Result<Submission> submitted;
        try
        {
            using var payload = Payload(upload, content, type);
            submitted = await executions.SubmitAsync(workflowKey, KeyPrefix + upload.Id.ToString("N"), correlationId ?? upload.Id.ToString("N"),
                new SubmitExecutionRequest(payload.RootElement), ct);
        }
        catch (DbUpdateException)
        {
            // The same file was uploaded to this workflow at the same moment and that upload won (unique index on workflow + SHA-256):
            // nothing of this one was saved; answer with the winner's run.
            db.ChangeTracker.Clear();
            if (await FindDuplicateAsync(target.WorkflowId, sha256, CancellationToken.None) is { } winner)
            {
                return winner;
            }

            throw;
        }

        if (!submitted.IsSuccess)
        {
            // Nothing was saved: let go of the staged row and its audit entry so a later save cannot pick them up.
            db.Uploads.Remove(upload);
            db.AuditEntries.Remove(audit);
            return submitted;
        }

        // The run is already committed and queued: linking the row to it is best effort and never turns the upload into an error.
        // The link can always be recovered by the run key upload:<id:N> (the Worker repairs it when it takes the file).
        var executionId = submitted.Value!.Execution.ExecutionId;
        upload.AttachExecution(executionId);
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // any failure here must not undo an accepted upload; logged with ids only
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            foreach (var entry in db.ChangeTracker.Entries<Upload>().Where(e => ReferenceEquals(e.Entity, upload)).ToList())
            {
                entry.State = EntityState.Detached; // a later save in this scope must not retry it
            }

            LogAttachFailed(logger, upload.Id, executionId, ex.GetType().Name);        }

        return submitted;
    }

    /// <summary>Ids and the exception type only: never the file name, content or the exception message (it may quote values).</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Linking upload {UploadId} to execution {ExecutionId} failed ({ErrorType}); the run is queued and the link is recovered by its key")]
    private static partial void LogAttachFailed(ILogger logger, Guid uploadId, Guid executionId, string errorType);

    private sealed record Target(Guid WorkflowId, IReadOnlyList<UploadType> Types, long MaxBytes, AppError? Error = null);

    /// <summary>The workflow and its File upload step's settings, or why it does not take uploads.</summary>
    private async Task<Target> FindTargetAsync(string workflowKey, CancellationToken ct)
    {
        var workflow = await db.Workflows.AsNoTracking().Include(w => w.Versions).SingleOrDefaultAsync(w => w.Key == workflowKey, ct);
        if (workflow is null)
        {
            return new(Guid.Empty, [], 0, AppError.NotFound("workflow.not_found", $"Workflow '{workflowKey}' was not found."));
        }

        if (!workflow.IsActive)
        {
            return NotAvailable(workflowKey, "is inactive");
        }

        if (workflow.LatestPublished is not { } version)
        {
            return NotAvailable(workflowKey, "has no published version");
        }

        var step = ReadStep(version.Definition);
        if (step.Reason is not null)
        {
            return NotAvailable(workflowKey, step.Reason);
        }

        return new(workflow.Id, step.Types, UploadRules.MaxBytes(Text(step.Config, "maxSize") ?? "", options.Value.CeilingBytes));
    }

    /// <summary>
    /// True when a published definition starts with a File upload step for signed-in staff that accepts at least one type: the
    /// same test the upload endpoint applies (the workflow must also be active). Used for <c>acceptsUpload</c> on the workflow list.
    /// </summary>
    public static bool StartsWithStaffUpload(string definition) => ReadStep(definition).Reason is null;

    private sealed record StartStep(IReadOnlyList<UploadType> Types, JsonElement Config, string? Reason);

    private static StartStep ReadStep(string definition)
    {
        var trigger = WorkflowDefinitionParser.Parse(definition).Definition?.Nodes
            .SingleOrDefault(n => NodeCatalog.Find(n.Type)?.Category == "input");
        if (trigger is null || trigger.Type != NodeCatalog.FileUpload)
        {
            return new([], default, "does not start with a File upload step");
        }

        if (Text(trigger.Config, "who") != "staff")
        {
            return new([], default, "lets someone other than signed-in staff upload, which Verso does not support");
        }

        var types = UploadRules.ParseAccept(Text(trigger.Config, "accept") ?? "");
        return types.Count == 0 ? new([], default, "accepts no file types") : new(types, trigger.Config, null);
    }

    private static Target NotAvailable(string workflowKey, string reason) =>
        new(Guid.Empty, [], 0, AppError.Conflict("upload.not_available", $"Workflow '{workflowKey}' {reason}, so files cannot be uploaded to it."));

    private static string? Text(JsonElement config, string name) =>
        config.ValueKind == JsonValueKind.Object && config.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// The run of an earlier upload of the same bytes to this workflow, answered as not created. An expired upload (its run failed
    /// with <c>upload.expired</c>) is deleted instead, so the file can be uploaded again as a new run; a taken one still counts.
    /// </summary>
    private async Task<Result<Submission>?> FindDuplicateAsync(Guid workflowId, string sha256, CancellationToken ct)
    {
        var existing = await db.Uploads.AsNoTracking()
            .Where(u => u.WorkflowId == workflowId && u.Sha256 == sha256)
            .Select(u => new { u.Id, u.Status })
            .SingleOrDefaultAsync(ct);
        if (existing is null)
        {
            return null;
        }

        if (existing.Status != UploadStatus.Expired)
        {
            var key = KeyPrefix + existing.Id.ToString("N");
            var run = await db.Executions.AsNoTracking()
                .Where(e => e.WorkflowId == workflowId && e.IdempotencyKey == key)
                .Select(e => new { e.Id, e.Status, e.WorkflowKey, e.WorkflowVersionNumber, e.CorrelationId })
                .SingleOrDefaultAsync(ct);
            if (run is not null)
            {
                return new Submission(new SubmissionDto(run.Id, run.Status.ToString(), run.WorkflowKey, run.WorkflowVersionNumber, run.CorrelationId), Created: false);
            }

            // A row without its run cannot be written by this service (both commit together); treat it like an expired one.
        }

        await db.Uploads.Where(u => u.Id == existing.Id).ExecuteDeleteAsync(ct);
        return null;
    }

    private async Task RejectAsync(Guid workflowId, string workflowKey, string rule, CancellationToken ct)
    {
        db.AuditEntries.Add(AuditEntry.Record(
            Actions.FileUploadRejected, "Workflow", workflowId.ToString(), user.Id, user.Name, clock.GetUtcNow(),
            JsonSerializer.Serialize(new { workflowKey, rule }), Activity.Current?.TraceId.ToHexString()));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The name kept and shown: the last part of what the browser sent (no folders), its own extension replaced by the accepted
    /// type's (so <c>photo.jpeg</c> becomes <c>photo.jpg</c>, never <c>photo.jpeg.jpg</c>), then sanitised.
    /// </summary>
    internal static string StoredName(string raw, string extension)
    {
        var name = raw ?? "";
        name = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
        var dot = name.LastIndexOf('.');
        return UploadRules.SafeFileName(dot >= 0 ? name[..dot] : name, extension);
    }

    /// <summary>
    /// <c>{ "upload": { id, fileName, contentType, size, sha256 } }</c>, plus <c>"content"</c> with the text of an XML, CSV, TXT or JSON
    /// file (UTF-8 without byte-order mark, trimmed, as Folder input does) when the whole payload stays within the data limit.
    /// </summary>
    private static JsonDocument Payload(Upload upload, byte[] content, UploadType type)
    {
        var reference = new JsonObject
        {
            ["id"] = upload.Id,
            ["fileName"] = upload.FileName,
            ["contentType"] = upload.ContentType,
            ["size"] = upload.Size,
            ["sha256"] = upload.Sha256,
        };

        if (type.IsText)
        {
            var bytes = content.AsSpan();
            if (bytes.StartsWith<byte>([0xEF, 0xBB, 0xBF]))
            {
                bytes = bytes[3..];
            }

            var withText = new JsonObject { ["upload"] = reference.DeepClone(), ["content"] = Utf8.GetString(bytes).Trim() }.ToJsonString(PayloadJson);
            if (withText.Length <= ExecutionService.MaxDataChars)
            {
                return JsonDocument.Parse(withText);
            }
        }

        return JsonDocument.Parse(new JsonObject { ["upload"] = reference }.ToJsonString(PayloadJson));
    }
}
