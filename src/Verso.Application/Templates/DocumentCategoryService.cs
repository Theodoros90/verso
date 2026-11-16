using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Verso.Domain.Audit;
using Verso.Domain.Templates;

namespace Verso.Application.Templates;

/// <summary>Document category use cases: create, rename, deactivate and activate. Every change is audited.</summary>
public sealed class DocumentCategoryService(IVersoDbContext db, ICurrentUser user, TimeProvider clock)
{
    public const string EntityType = "DocumentCategory";
    private const int MaxNameLength = 100;

    public static class Actions
    {
        public const string Created = "CategoryCreated";
        public const string Renamed = "CategoryRenamed";
        public const string Deactivated = "CategoryDeactivated";
        public const string Activated = "CategoryActivated";
    }

    public async Task<Result<DocumentCategoryDto>> CreateAsync(CreateCategoryRequest request, CancellationToken ct)
    {
        if (ValidateName(request.Name) is { } errors)
        {
            return AppError.Validation(errors);
        }

        var name = request.Name!.Trim();
        if (await NameTakenAsync(name, null, ct))
        {
            return NameTaken(name);
        }

        var now = clock.GetUtcNow();
        var category = DocumentCategory.Create(name, user.Id, now);
        db.DocumentCategories.Add(category);
        Audit(Actions.Created, category.Id, now, new { name });
        if (await TrySaveAsync(name, ct) is { } conflict)
        {
            return conflict;
        }

        return new DocumentCategoryDto(category.Id, category.Name, category.IsActive, 0);
    }

    public async Task<IReadOnlyList<DocumentCategoryDto>> ListAsync(bool includeInactive, CancellationToken ct)
    {
        var query = db.DocumentCategories.AsNoTracking();
        if (!includeInactive)
        {
            query = query.Where(c => c.IsActive);
        }

        return await query
            .OrderBy(c => c.Name)
            .Select(c => new DocumentCategoryDto(c.Id, c.Name, c.IsActive, db.Templates.Count(t => t.CategoryId == c.Id)))
            .ToListAsync(ct);
    }

    public async Task<Result<DocumentCategoryDto>> RenameAsync(Guid id, RenameCategoryRequest request, CancellationToken ct)
    {
        if (await db.DocumentCategories.SingleOrDefaultAsync(c => c.Id == id, ct) is not { } category)
        {
            return NotFound(id);
        }

        if (ValidateName(request.Name) is { } errors)
        {
            return AppError.Validation(errors);
        }

        var name = request.Name!.Trim();
        if (await NameTakenAsync(name, id, ct))
        {
            return NameTaken(name);
        }

        if (category.Name != name)
        {
            var from = category.Name;
            category.Rename(name);
            Audit(Actions.Renamed, id, clock.GetUtcNow(), new { from, to = name });
            if (await TrySaveAsync(name, ct) is { } conflict)
            {
                return conflict;
            }
        }

        return await ToDtoAsync(category, ct);
    }

    public Task<Result<DocumentCategoryDto>> DeactivateAsync(Guid id, CancellationToken ct) => SetActiveAsync(id, false, ct);

    public Task<Result<DocumentCategoryDto>> ActivateAsync(Guid id, CancellationToken ct) => SetActiveAsync(id, true, ct);

    private async Task<Result<DocumentCategoryDto>> SetActiveAsync(Guid id, bool active, CancellationToken ct)
    {
        if (await db.DocumentCategories.SingleOrDefaultAsync(c => c.Id == id, ct) is not { } category)
        {
            return NotFound(id);
        }

        if (category.IsActive != active) // repeating the same change is a no-op: no audit row
        {
            if (active)
            {
                category.Activate();
            }
            else
            {
                category.Deactivate();
            }

            Audit(active ? Actions.Activated : Actions.Deactivated, id, clock.GetUtcNow(), new { name = category.Name });
            await db.SaveChangesAsync(ct);
        }

        return await ToDtoAsync(category, ct);
    }

    private static Dictionary<string, string[]>? ValidateName(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength
            ? new Dictionary<string, string[]> { ["name"] = [$"A name of 1–{MaxNameLength} characters is required."] }
            : null;

    private Task<bool> NameTakenAsync(string name, Guid? exceptId, CancellationToken ct) =>
        // Runs in SQL, whose default collation is case-insensitive (the unique index compares the same way).
        db.DocumentCategories.AnyAsync(c => c.Id != exceptId && c.Name == name, ct);

    /// <summary>The unique index is the backstop for two requests racing past the name check.</summary>
    private async Task<AppError?> TrySaveAsync(string name, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException)
        {
            return NameTaken(name);
        }
    }

    private async Task<DocumentCategoryDto> ToDtoAsync(DocumentCategory c, CancellationToken ct) =>
        new(c.Id, c.Name, c.IsActive, await db.Templates.CountAsync(t => t.CategoryId == c.Id, ct));

    private void Audit(string action, Guid id, DateTimeOffset now, object details) =>
        db.AuditEntries.Add(AuditEntry.Record(
            action, EntityType, id.ToString(), user.Id, user.Name, now,
            JsonSerializer.Serialize(details), Activity.Current?.TraceId.ToHexString()));

    private static AppError NotFound(Guid id) => AppError.NotFound("category.not_found", $"Category {id} was not found.");

    private static AppError NameTaken(string name) => AppError.Conflict("category.name_taken", $"A category named '{name}' already exists.");
}
