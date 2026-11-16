using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Verso.Application.Executions;
using Verso.Domain.Audit;
using Verso.Domain.Mail;

namespace Verso.Application.Mail;

public sealed record EmailRelayTestRequest(string? ToAddress);

/// <summary>The state of a relay test. Never carries the recipient.</summary>
public sealed record EmailRelayTestDto(Guid Id, string RelayKey, string Status, string? Code, string? Message, DateTimeOffset RequestedAt, DateTimeOffset? CompletedAt);

/// <summary>Queues the Worker job that runs a relay test (Hangfire in Infrastructure).</summary>
public interface IEmailRelayTestJobs
{
    void Enqueue(Guid testId);
}

/// <summary>
/// API side of "Test connection" for an email relay (ADR 0015): validates the request, records it (audited, without the address)
/// and queues the Worker. The API never sends mail itself.
/// </summary>
public sealed class EmailRelayTests(IVersoDbContext db, ICurrentUser user, TimeProvider clock, IEmailRelayTestJobs jobs)
{
    public const string EntityType = "EmailRelayTest";
    public const string RequestedAction = "EmailRelayTestRequested";
    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromHours(24);

    public async Task<Result<EmailRelayTestDto>> RequestAsync(Guid relayId, EmailRelayTestRequest request, CancellationToken ct)
    {
        if (await db.EmailRelays.AsNoTracking().SingleOrDefaultAsync(r => r.Id == relayId, ct) is not { } relay)
        {
            return AppError.NotFound("email_relay.not_found", $"Email relay {relayId} was not found.");
        }

        var (addresses, problem) = EmailAddresses.Parse(request.ToAddress);
        if (problem is not null || addresses.Count != 1)
        {
            return AppError.Validation(new Dictionary<string, string[]> { ["toAddress"] = ["Enter one email address to send the test to."] });
        }

        if (!relay.IsActive)
        {
            return AppError.Conflict("email_relay.inactive", "This email connection is switched off. Turn it on to test it.");
        }

        var now = clock.GetUtcNow();
        var test = EmailRelayTest.Create(relay.Id, relay.Key, addresses[0], user.Id, now);
        db.EmailRelayTests.Add(test);
        db.AuditEntries.Add(AuditEntry.Record(
            RequestedAction, EntityType, test.Id.ToString(), user.Id, user.Name, now,
            JsonSerializer.Serialize(new { relay = relay.Key }), Activity.Current?.TraceId.ToHexString()));
        await db.SaveChangesAsync(ct);

        try
        {
            jobs.Enqueue(test.Id);
        }
        catch
        {
            test.Fail("email.test_not_queued", "The test could not be queued. Is the job storage reachable?", now);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        return ToDto(test);
    }

    public async Task<Result<EmailRelayTestDto>> GetAsync(Guid testId, CancellationToken ct) =>
        await db.EmailRelayTests.AsNoTracking().SingleOrDefaultAsync(t => t.Id == testId, ct) is { } test
            ? ToDto(test)
            : AppError.NotFound("email_relay_test.not_found", $"Email relay test {testId} was not found (tests are kept for 24 hours).");

    internal static EmailRelayTestDto ToDto(EmailRelayTest t) => new(t.Id, t.RelayKey, t.Status.ToString(), t.Code, t.Message, t.RequestedAt, t.CompletedAt);
}

/// <summary>
/// Worker side: sends one fixed test message through the saved relay and records the outcome. A relay that refuses or can't be reached
/// is a failed test (with the same error codes as Send email), not an error of the job. The recipient is cleared when the test ends.
/// </summary>
public sealed class EmailRelayTestRunner(IVersoDbContext db, IEmailSender email, TimeProvider clock)
{
    public async Task RunAsync(Guid testId, CancellationToken ct)
    {
        if (await db.EmailRelayTests.SingleOrDefaultAsync(t => t.Id == testId, ct) is not { IsFinished: false } test)
        {
            return; // gone (cleaned up) or already run
        }

        test.Start();
        await db.SaveChangesAsync(ct);

        var now = clock.GetUtcNow();
        try
        {
            var message = new EmailMessage(
                [test.ToAddress ?? ""], "Verso test email",
                $"This is a test message from Verso.\n\nIt was sent through the email connection '{test.RelayKey}'. If you can read it, the connection works.\n\nNothing needs to be done.",
                null, [], test.RelayKey);
            await email.SendAsync(message, ct);
            test.Succeed(clock.GetUtcNow());
        }
        catch (EmailSendException ex)
        {
            test.Fail(ex.Code, ex.Message, clock.GetUtcNow());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            test.Fail("email.test_failed", "The test could not be run.", now);
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Deletes tests older than 24 hours.</summary>
    public Task<int> CleanupAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - EmailRelayTests.RetentionPeriod;
        return db.EmailRelayTests.Where(t => t.RequestedAt < cutoff).ExecuteDeleteAsync(ct);
    }
}
