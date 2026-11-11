using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Verso.Domain.Audit;
using Verso.Domain.Ingestion;

namespace Verso.Application.Ingestion;

/// <summary>Queues the Worker's access test. Implemented with Hangfire in Infrastructure.</summary>
public interface IFolderAccessJobs
{
    void Enqueue(Guid testId);
}

/// <param name="Role"><c>source</c> (read and move files out), <c>processed</c> or <c>failed</c> (create files).</param>
public sealed record FolderAccessTargetRequest(string? Role, string? Location, string? Subfolder);

public sealed record FolderAccessTestRequest(IReadOnlyList<FolderAccessTargetRequest>? Targets);

public sealed record FolderAccessTargetDto(string Role, string Location, string Subfolder);

public sealed record FolderCheckDto(string Name, bool Ok, string Message);

public sealed record FolderAccessResultDto(string Role, string Location, string Subfolder, bool Ok, IReadOnlyList<FolderCheckDto> Checks);

/// <param name="Status"><c>Queued</c>, <c>Running</c>, <c>Completed</c> (see each result's <c>ok</c>) or <c>Failed</c> (the test couldn't run; see <c>error</c>).</param>
/// <param name="Identity">The account the Worker checked the folders as.</param>
public sealed record FolderAccessTestDto(
    Guid Id, string Status, IReadOnlyList<FolderAccessTargetDto> Targets, IReadOnlyList<FolderAccessResultDto> Results,
    string? Identity, string? Machine, string? Error, DateTimeOffset RequestedAt, DateTimeOffset? CompletedAt);

/// <summary>
/// "Test access" (API side): validates the targets against the approved locations, stores the request and queues
/// it for the Worker, which checks the folders under its own service identity (<see cref="FolderAccessTestRunner"/>).
/// </summary>
public sealed class FolderAccessTests(IVersoDbContext db, IFolderAccessJobs jobs, ICurrentUser user, TimeProvider clock)
{
    public const string EntityType = "FolderAccessTest";
    public const string RequestedAction = "FolderAccessTestRequested";
    public const string SourceRole = "source";
    public const string ProcessedRole = "processed";
    public const string FailedRole = "failed";

    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromHours(24);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Result<FolderAccessTestDto>> RequestAsync(FolderAccessTestRequest? request, CancellationToken ct)
    {
        var targets = request?.Targets ?? [];
        if (targets.Count is < 1 or > 3)
        {
            return AppError.Validation(new Dictionary<string, string[]> { ["targets"] = ["Between 1 and 3 folders."] });
        }

        var locations = await db.FolderLocations.AsNoTracking().ToDictionaryAsync(l => l.Key, StringComparer.Ordinal, ct);
        var errors = new Dictionary<string, string[]>();
        var accepted = new List<FolderAccessTargetDto>();
        for (var i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            var role = t?.Role ?? "";
            var key = t?.Location?.Trim() ?? "";
            var subfolder = FolderPaths.NormalizeSubfolder(t?.Subfolder);
            if (role is not (SourceRole or ProcessedRole or FailedRole))
            {
                errors[$"targets[{i}].role"] = ["One of: source, processed, failed."];
            }

            if (!locations.TryGetValue(key, out var location))
            {
                errors[$"targets[{i}].location"] = ["Choose an approved location."];
            }
            else if (role == SourceRole ? !location.AllowInput : !location.AllowOutput)
            {
                errors[$"targets[{i}].location"] = [$"'{location.Name}' isn't approved {(role == SourceRole ? "as a source" : "for processed or failed files")}."];
            }

            if (FolderPaths.SubfolderProblem(subfolder) is { } problem)
            {
                errors[$"targets[{i}].subfolder"] = [problem];
            }

            accepted.Add(new FolderAccessTargetDto(role, key, subfolder));
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var now = clock.GetUtcNow();
        var test = new FolderAccessTest(JsonSerializer.Serialize(accepted, Json), user.Id, now);
        db.FolderAccessTests.Add(test);
        db.AuditEntries.Add(AuditEntry.Record(
            RequestedAction, EntityType, test.Id.ToString(), user.Id, user.Name, now,
            JsonSerializer.Serialize(new { targets = accepted }, Json), Activity.Current?.TraceId.ToHexString()));
        await db.SaveChangesAsync(ct);

        try
        {
            jobs.Enqueue(test.Id);
        }
        catch (Exception)
        {
            test.Fail("The test could not be queued for the Worker.", clock.GetUtcNow());
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        return ToDto(test);
    }

    public async Task<Result<FolderAccessTestDto>> GetAsync(Guid id, CancellationToken ct) =>
        await db.FolderAccessTests.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct) is { } test
            ? ToDto(test)
            : AppError.NotFound("folder_access_test.not_found", $"Access test {id} was not found.");

    internal static FolderAccessTestDto ToDto(FolderAccessTest t) => new(
        t.Id, t.Status.ToString(),
        JsonSerializer.Deserialize<List<FolderAccessTargetDto>>(t.Targets, Json) ?? [],
        t.Results is null ? [] : JsonSerializer.Deserialize<List<FolderAccessResultDto>>(t.Results, Json) ?? [],
        t.Identity, t.Machine, t.Error, t.RequestedAt, t.CompletedAt);
}

/// <summary>Runs an access test in the Worker: each folder is checked as the Worker's own identity.</summary>
public sealed class FolderAccessTestRunner(IVersoDbContext db, IFolderAccess folders, TimeProvider clock)
{
    public async Task RunAsync(Guid testId, CancellationToken ct)
    {
        var test = await db.FolderAccessTests.SingleOrDefaultAsync(t => t.Id == testId, ct);
        if (test is null || test.IsFinished)
        {
            return; // cleaned up, or a duplicate delivery
        }

        test.Start();
        await db.SaveChangesAsync(ct);

        var targets = JsonSerializer.Deserialize<List<FolderAccessTargetDto>>(test.Targets, FolderAccessTests.Json) ?? [];
        var results = new List<FolderAccessResultDto>();
        foreach (var target in targets)
        {
            var mode = target.Role == FolderAccessTests.SourceRole ? FolderAccessMode.Input : FolderAccessMode.Output;
            var checks = folders.Test(new FolderRef(target.Location, target.Subfolder), mode);
            results.Add(new FolderAccessResultDto(target.Role, target.Location, target.Subfolder, checks.Count > 0 && checks.All(c => c.Ok),
                [.. checks.Select(c => new FolderCheckDto(c.Name, c.Ok, c.Message))]));
        }

        test.Complete(JsonSerializer.Serialize(results, FolderAccessTests.Json), folders.Identity, Environment.MachineName, clock.GetUtcNow());
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Deletes tests older than <see cref="FolderAccessTests.RetentionPeriod"/>.</summary>
    public Task<int> CleanupAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - FolderAccessTests.RetentionPeriod;
        return db.FolderAccessTests.Where(t => t.RequestedAt < cutoff).ExecuteDeleteAsync(ct);
    }
}
