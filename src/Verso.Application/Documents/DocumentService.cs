using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Verso.Application.Templates;
using Verso.Domain.Audit;
using Verso.Domain.Documents;

namespace Verso.Application.Documents;

public sealed record ArchivedDocumentDto(
    Guid Id, Guid ExecutionId, string NodeId, Guid TemplateVersionId, string FileName, string ContentType, long SizeBytes, string Sha256,
    string Status, DateTimeOffset CreatedAt, DateTimeOffset ArchivedAt, string FolderPath, string Retention, DateTimeOffset? RetentionUntil,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>An archived document's bytes, already checked against the recorded SHA-256.</summary>
public sealed record DocumentContent(string FileName, string ContentType, byte[] Bytes);

/// <summary>Archive search, document details and (audited) content download (plan §5.4).</summary>
public sealed class DocumentService(IVersoDbContext db, IDocumentStore store, ICurrentUser user, TimeProvider clock)
{
    public const string EntityType = "Document";
    public const string DownloadedAction = "DocumentDownloaded";

    public async Task<Result<PageResult<ArchivedDocumentDto>>> SearchAsync(
        string? text, DateTimeOffset? from, DateTimeOffset? to, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        if (from is { } f && to is { } t && t <= f)
        {
            return AppError.Validation(new Dictionary<string, string[]> { ["to"] = ["Must be after 'from'."] });
        }

        var query =
            from d in db.Documents.AsNoTracking()
            join a in db.ArchiveRecords.AsNoTracking() on d.Id equals a.DocumentId
            where d.Status == DocumentStatus.Archived
            select new { Document = d, Archive = a };
        if (!string.IsNullOrWhiteSpace(text))
        {
            // Matches the file name and metadata values (not keys), case-insensitively, as plain text.
            var pattern = "%" + EscapeLike(text.Trim()) + "%";
            query = query.Where(x => EF.Functions.Like(x.Archive.SearchText, pattern, "\\"));
        }

        if (from is { } archivedFrom)
        {
            query = query.Where(x => x.Archive.ArchivedAt >= archivedFrom);
        }

        if (to is { } archivedBefore)
        {
            query = query.Where(x => x.Archive.ArchivedAt < archivedBefore);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.Archive.ArchivedAt).ThenByDescending(x => x.Document.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PageResult<ArchivedDocumentDto>([.. rows.Select(x => ToDto(x.Document, x.Archive))], page, pageSize, total);
    }

    public async Task<Result<ArchivedDocumentDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await (
            from d in db.Documents.AsNoTracking()
            join a in db.ArchiveRecords.AsNoTracking() on d.Id equals a.DocumentId
            where d.Id == id && d.Status == DocumentStatus.Archived
            select new { Document = d, Archive = a }).SingleOrDefaultAsync(ct);
        return row is null ? NotArchived(id) : ToDto(row.Document, row.Archive);
    }

    /// <summary>
    /// Reads the archived bytes and verifies their SHA-256 (plan §7) before releasing them. The download is audited
    /// (actor, document, hash), and only once the bytes are verified and about to be sent.
    /// </summary>
    public async Task<Result<DocumentContent>> OpenContentAsync(Guid id, CancellationToken ct)
    {
        var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null)
        {
            return AppError.NotFound("document.not_found", $"Document {id} was not found.");
        }

        if (document.Status != DocumentStatus.Archived)
        {
            return AppError.Conflict("document.not_archived", "This document isn't archived yet.");
        }

        var bytes = await store.ReadAsync(document.StorageKey, ct);
        if (bytes is null)
        {
            return AppError.Conflict("document.content_missing", "The archived file is missing from storage. Contact an administrator.");
        }

        if (DocumentKeys.Sha256Hex(bytes) != document.Sha256)
        {
            return AppError.Conflict("document.integrity_failed", "The archived file doesn't match its recorded SHA-256, so it is not served. Contact an administrator.");
        }

        var now = clock.GetUtcNow();
        db.AuditEntries.Add(AuditEntry.Record(
            DownloadedAction, EntityType, document.Id.ToString(), user.Id, user.Name, now,
            JsonSerializer.Serialize(new { executionId = document.ExecutionId, fileName = document.FileName, sha256 = document.Sha256 }),
            Activity.Current?.TraceId.ToHexString()));
        await db.SaveChangesAsync(ct);
        return new DocumentContent(document.FileName, document.ContentType, bytes);
    }

    private static ArchivedDocumentDto ToDto(Document d, ArchiveRecord a) => new(
        d.Id, d.ExecutionId, d.NodeId, d.TemplateVersionId, d.FileName, d.ContentType, d.SizeBytes, d.Sha256, d.Status.ToString(), d.CreatedAt,
        a.ArchivedAt, a.FolderPath, a.Retention, a.RetentionUntil, JsonSerializer.Deserialize<Dictionary<string, string>>(a.Metadata) ?? []);

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal);

    private static AppError NotArchived(Guid id) => AppError.NotFound("document.not_found", $"Archived document {id} was not found.");
}
