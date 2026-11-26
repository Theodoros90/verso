using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Verso.Application.Connections;
using Verso.Application.Documents;
using Verso.Application.Templates;
using Verso.Application.Workflows;
using Verso.Domain.Audit;
using Verso.Domain.Documents;
using Verso.Domain.Executions;
using Verso.Rendering;
using Verso.Rendering.Binding;
using Verso.Rendering.Docx;
using Verso.Rendering.Email;
using Verso.Rendering.Html;
using Verso.Rendering.Layout;

namespace Verso.Application.Executions;

/// <summary>A step failure with a stable code. <see cref="Retryable"/> false: retrying can't help (e.g. a broken graph).</summary>
public sealed class StepException(string code, string message, bool retryable) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

/// <summary>
/// Runs an execution in the Worker (plan §6.2 steps 2–7). Walks the pinned workflow version from the trigger,
/// persisting each step before and after it runs, and follows the edge of the port the step chose. Steps that
/// already succeeded are not run again (their results are reused), so a re-delivered or retried job resumes at
/// the first unfinished step. Technical failures are retried by rescheduling the job (engine-owned retries;
/// Hangfire's own retry is off for this job); data problems end the run as Rejected.
/// </summary>
public sealed partial class ExecutionEngine(
    IVersoDbContext db, IPdfPrinter printer, IDocxWriter docx, IPdfCombiner combiner, IEmailSender email, IPrintSpooler spooler, IDatabaseQuery query, IDatabaseWrite write, IApiFetch apiFetch, ISmsSend sms, IDocumentStore store, IExecutionJobs jobs, TimeProvider clock, ILogger<ExecutionEngine> logger)
{
    /// <summary>Attempts per step (plan §6.3), within one execution attempt.</summary>
    public const int MaxStepAttempts = 3;

    public const string ActorId = "verso.worker";
    public const string ActorName = "Verso Worker";

    /// <summary>How long a run may take (plan §6.3, assumption: 5 min). Exceeding it is a technical failure.</summary>
    public static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The run's lock; renewed at every step, so it only runs out when a Worker died mid-run.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(6);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Exponential backoff before attempt <paramref name="nextAttempt"/>: 5 s, then 10 s.</summary>
    public static TimeSpan RetryDelay(int nextAttempt) => TimeSpan.FromSeconds(5 * Math.Pow(2, Math.Max(0, nextAttempt - 2)));

    public async Task RunAsync(Guid executionId, CancellationToken ct)
    {
        var execution = await db.Executions.Include(e => e.Steps).SingleOrDefaultAsync(e => e.Id == executionId, ct);
        if (execution is null)
        {
            return; // a job without a row does nothing
        }

        // Every log line of this run carries the execution and the correlation id it was submitted with (plan §7, M9).
        // Logs never contain payload values: only ids, step names, codes and sanitized messages.
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["ExecutionId"] = execution.Id,
            ["CorrelationId"] = execution.CorrelationId,
            ["WorkflowKey"] = execution.WorkflowKey,
        });

        var wasQueued = execution.Status == ExecutionStatus.Queued;
        var now = clock.GetUtcNow();
        if (!execution.TryClaim(now, Lease))
        {
            return; // finished, or another run holds the lock (a duplicate delivery)
        }

        if (wasQueued)
        {
            Event(execution, ExecutionEventTypes.Started, now, data: new { attempt = execution.Attempt });
        }

        LogRunStarted(logger, execution.Id, execution.Attempt);

        if (!await SaveAsync(execution, ct))
        {
            return;
        }

        var version = await db.WorkflowVersions.AsNoTracking().SingleAsync(v => v.Id == execution.WorkflowVersionId, ct);
        var plan = ExecutionPlan.Create(WorkflowDefinitionParser.Parse(version.Definition).Definition
            ?? throw new InvalidOperationException($"Published workflow version {version.Id} has an invalid definition."));
        using var input = JsonDocument.Parse(execution.InputPayload);
        var run = new Run(execution, plan, input.RootElement,
            JsonSerializer.Deserialize<Dictionary<string, Guid>>(execution.ResolvedTemplateVersions) ?? []);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RunTimeout);

        var node = plan.Trigger;
        while (true)
        {
            var step = await StepAsync(run, node, timeout.Token, ct);
            if (step is null)
            {
                return; // retry scheduled, execution failed, or the lock was lost
            }

            var port = step.Port;
            if (node.Type == NodeCatalog.ForEach && port == ForEach.Each)
            {
                // The loop runs the steps after 'each' once per item; when the last item is done the run is over.
                if (!await RunLoopAsync(run, node, step.Step, timeout.Token, ct))
                {
                    return;
                }

                await FinishAsync(run, node, step, ct);
                return;
            }

            if (node.Type == NodeCatalog.ParallelBranches)
            {
                // Both paths run, A then B; the run is over when the second one ends (ADR 0009).
                if (!await RunBranchesAsync(run, node, timeout.Token, ct))
                {
                    return;
                }

                await FinishAsync(run, node, step, ct);
                return;
            }

            var next = port is null ? null : plan.Next(node.Id, port);
            if (next is null)
            {
                await FinishAsync(run, node, step, ct);
                return;
            }

            node = next;
        }
    }

    /// <summary>
    /// Runs the steps after a For each's <c>each</c> output once per item. An item ends where its path ends or returns to the For each.
    /// The first item that is rejected or fails ends the whole run (and no later item runs). False when the run ended or stopped.
    /// Items already done are found again by their step rows and skipped over, so a retried or redelivered run resumes in the right item.
    /// <para>
    /// Step rows are matched to items by position, and the list is rebuilt from the data on every resume (a Query database step before the loop reads again).
    /// When the loop contains a step that changes the outside world (<see cref="NodeCatalog.ChangesOutsideWorld"/>: Write to database, Send SMS), a list that changed would skip an item whose position already has a succeeded write, or write an item
    /// again at a later position. So before any item runs, the list is compared with the fingerprint the first pass recorded; on any difference (count, order or
    /// content) the run fails with <c>loop.items_changed</c> and nothing in the loop runs. A recorded result without a fingerprint can't be verified and fails the
    /// same way (only a run started before this check existed could have one, and write steps are newer than the check).
    /// </para>
    /// </summary>
    private async Task<bool> RunLoopAsync(Run run, Workflows.WorkflowNode forEach, ExecutionStep loopStep, CancellationToken timeoutToken, CancellationToken shutdown)
    {
        var items = ForEach.Items(forEach.Config, run.Data);
        if (LoopChangesOutsideWorld(run.Plan, forEach.Id) && RecordedFingerprint(loopStep) != ForEach.Fingerprint(run.Execution.Id, forEach.Id, items))
        {
            var before = RecordedCount(loopStep) is { } c ? c.ToString(System.Globalization.CultureInfo.InvariantCulture) : "an unknown number of";
            var changed = $"The list of step '{forEach.Title}' is not the same as when the run started ({before} item(s) then, {items.Count} now, or the same number in another order or with other content)";
            await FailRestoreAsync(run, forEach, loopStep, new StepException(
                "loop.items_changed",
                LoopContains(run.Plan, forEach.Id, NodeCatalog.WriteDatabase)
                    ? changed + ", and the loop contains a Write to database step. " +
                      "The run can't continue: matching the list again could skip an item or write one twice. Writes already made for earlier items are kept and were not repeated; no later item ran. " +
                      "Check the database, then submit the data again if needed (a new run writes every item again)."
                    : changed + ", and the loop contains a Send SMS step. " +
                      "The run can't continue: matching the list again could skip an item or send one twice. Messages already sent for earlier items were not sent again; no later item ran. " +
                      "Check the SMS provider's log, then submit the data again if needed (a new run sends every item again).",
                retryable: false));
            return false;
        }

        var baseData = run.Data;
        var first = run.Plan.Next(forEach.Id, ForEach.Each)
            ?? throw new StepException("foreach.not_connected", $"Step '{forEach.Title}' has no steps connected to repeat.", retryable: false);
        for (var i = 1; i <= items.Count; i++)
        {
            run.Iteration = i;
            run.Document = null;
            run.Data = ForEach.IterationData(forEach.Config, baseData, items[i - 1]);
            var current = first;
            while (true)
            {
                var step = await StepAsync(run, current, timeoutToken, shutdown);
                if (step is null)
                {
                    return false;
                }

                var next = step.Port is null ? null : run.Plan.Next(current.Id, step.Port);
                // A declined approval with a path connected continues down it; every other rejecting output ends the run.
                if (Rejects(current.Type, step.Port) && (current.Type != NodeCatalog.ApprovalStep || next is null))
                {
                    await FinishAsync(run, current, step, shutdown);
                    return false;
                }

                if (next is null || next.Id == forEach.Id)
                {
                    break; // this item is done
                }

                current = next;
            }
        }

        run.Iteration = 0;
        run.Document = null;
        run.Data = baseData;
        return true;
    }

    /// <summary>
    /// Runs the two paths of a Parallel branches step one after the other (ADR 0009), each from the data and document as they were at the fork.
    /// A path ends where its steps end. The first step that is rejected or fails ends the whole run (the other path doesn't run if it hadn't yet).
    /// Steps already done are found again by their rows and skipped over, so a retried or redelivered run resumes in the right path. False when the run ended or stopped.
    /// </summary>
    private async Task<bool> RunBranchesAsync(Run run, Workflows.WorkflowNode fork, CancellationToken timeoutToken, CancellationToken shutdown)
    {
        var baseData = run.Data;
        var baseDocument = run.Document;

        // Walks a path from its first step. Stops before a Wait for branches step when asked (the path has arrived at the join).
        // Ok false: the run ended or stopped.
        async Task<(bool Ok, Workflows.WorkflowNode? Join)> WalkAsync(Workflows.WorkflowNode? first, bool stopAtJoin)
        {
            var current = first;
            while (current is not null)
            {
                if (stopAtJoin && current.Type == NodeCatalog.WaitBranches)
                {
                    return (true, current);
                }

                var step = await StepAsync(run, current, timeoutToken, shutdown);
                if (step is null)
                {
                    return (false, null);
                }

                var next = step.Port is null ? null : run.Plan.Next(current.Id, step.Port);
                // A declined approval with a path connected continues down it; every other rejecting output ends the run.
                if (Rejects(current.Type, step.Port) && (current.Type != NodeCatalog.ApprovalStep || next is null))
                {
                    await FinishAsync(run, current, step, shutdown);
                    return (false, null);
                }

                current = next;
            }

            return (true, null);
        }

        Workflows.WorkflowNode? join = null;
        var arrived = 0;
        foreach (var path in Fanout.Paths)
        {
            run.Data = baseData;
            run.Document = baseDocument;
            var (ok, reached) = await WalkAsync(run.Plan.Next(fork.Id, path), stopAtJoin: true);
            if (!ok)
            {
                return false;
            }

            if (reached is not null)
            {
                join = reached;
                arrived++;
            }
        }

        if (join is not null && arrived == Fanout.Paths.Count)
        {
            // Both paths arrived: the join and what follows it run from the state the second path left (it ran last).
            return (await WalkAsync(join, stopAtJoin: false)).Ok;
        }

        run.Data = baseData;
        run.Document = baseDocument;
        return true;
    }

    /// <summary>The row of this node for the current item, and either its saved result (already succeeded) or the result of running it. Null: the run stopped.</summary>
    private async Task<StepResult?> StepAsync(Run run, Workflows.WorkflowNode node, CancellationToken timeoutToken, CancellationToken shutdown)
    {
        var known = run.Execution.Steps.Count;
        var step = run.Execution.StepFor(new PlannedStep(node.Id, node.Type, node.Title, run.Iteration), clock.GetUtcNow());
        if (run.Execution.Steps.Count > known)
        {
            db.ExecutionSteps.Add(step); // a row for a later item: its id is already set, so EF must be told it is new
        }

        if (step.Status == StepStatus.Succeeded)
        {
            try
            {
                await RestoreAsync(run, node, step, shutdown);
            }
            catch (StepException ex) when (node.Type is NodeCatalog.QueryDatabase or NodeCatalog.FetchApi)
            {
                // A query read again (or an API fetched again) on resume can fail for reasons outside the run (the database is down, the row is gone). The step stays Succeeded
                // (it did succeed); the run fails technically, so an operator retry resumes here and reads again.
                await FailRestoreAsync(run, node, step, ex);
                return null;
            }

            return new StepResult(step.OutputPort, step);
        }

        if (NodeCatalog.ChangesOutsideWorld(node.Type) && step.Status == StepStatus.Running)
        {
            // A write (or an SMS) that an earlier run started and never recorded as finished (the Worker crashed, or stopped mid-step: see the shutdown catch in
            // RunStepAsync). It may or may not have happened, so the engine never sends it again: the run fails and a person decides (an operator retry runs it).
            await FailUnfinishedAsync(run, node, step);
            return null;
        }

        if (node.Type == NodeCatalog.ApprovalStep)
        {
            await RequestApprovalAsync(run, node, step, shutdown);
            return null; // the run stops until a person decides (the decision queues it again)
        }

        var outcome = await RunStepAsync(run, node, step, timeoutToken, shutdown);
        return outcome is null ? null : new StepResult(outcome.Port, step);
    }

    /// <summary>
    /// Parks the run at an Approval step: the step waits, the execution becomes AwaitingApproval and releases its lock.
    /// Nothing about the data is stored; the request holds who may decide and when it becomes overdue.
    /// </summary>
    private async Task RequestApprovalAsync(Run run, Workflows.WorkflowNode node, ExecutionStep step, CancellationToken shutdown)
    {
        var execution = run.Execution;
        if (step.Status == StepStatus.Waiting)
        {
            return; // already asked (a re-delivered job); nothing to do
        }

        var now = clock.GetUtcNow();
        var (approver, escalation) = Approval.Settings(node.Config);
        var dueAt = Approval.Escalations.TryGetValue(escalation, out var e) ? now + e.After : (DateTimeOffset?)null;
        step.Start(now);
        step.Wait(JsonSerializer.Serialize(new { approver, escalateAfter = escalation, requestedAt = now, dueAt }, Json));
        execution.AwaitApproval();
        Event(execution, ExecutionEventTypes.StepStarted, now, node.Id, Data(step, ("attempt", step.Attempt)));
        Event(execution, ExecutionEventTypes.ApprovalRequested, now, node.Id, Data(step, ("approver", approver), ("dueAt", dueAt)));
        db.AuditEntries.Add(AuditEntry.Record(
            ExecutionService.Actions.ApprovalRequested, ExecutionService.EntityType, execution.Id.ToString(), ActorId, ActorName, now,
            JsonSerializer.Serialize(new { nodeId = node.Id, step = node.Title, approver, iteration = run.Iteration }), execution.CorrelationId));
        await SaveAsync(execution, shutdown);
    }

    private sealed record StepResult(string? Port, ExecutionStep Step);

    /// <summary>Event data: the given values, plus the loop item when the step runs inside a For each.</summary>
    private static Dictionary<string, object?> Data(ExecutionStep step, params (string Key, object? Value)[] values)
    {
        var data = new Dictionary<string, object?>();
        foreach (var (key, value) in values)
        {
            data[key] = value;
        }

        if (step.Iteration > 0)
        {
            data["iteration"] = step.Iteration;
        }

        return data;
    }

    /// <summary>Whether a path ending at this step by this output ends the run as Rejected (see <see cref="FinishAsync"/>).</summary>
    private static bool Rejects(string nodeType, string? port) => (nodeType, port) is
        (NodeCatalog.Validate, "invalid") or (NodeCatalog.ParseContent, "failed") or (NodeCatalog.MapFields, "failed")
        or (NodeCatalog.TransformData, "failed") or (NodeCatalog.MergeData, "failed") or (NodeCatalog.GeneratePdf or NodeCatalog.GenerateDocx or NodeCatalog.GenerateEmail or NodeCatalog.CombinePdfs or NodeCatalog.SendEmail, "failed")
        or (NodeCatalog.QueryDatabase, "failed") or (NodeCatalog.WriteDatabase, "failed") or (NodeCatalog.FetchApi, "failed") or (NodeCatalog.SendSms, "failed")
        or (NodeCatalog.ApprovalStep, Approval.Rejected);

    private sealed record Outcome(string? Port, object? Result, Func<Task>? AfterSave = null);

    /// <summary>State of one run: the execution, its plan and data, and the document produced so far.</summary>
    private sealed class Run(Execution execution, ExecutionPlan plan, JsonElement data, IReadOnlyDictionary<string, Guid> templates)
    {
        public Execution Execution { get; } = execution;
        public ExecutionPlan Plan { get; } = plan;
        /// <summary>The submitted data; a Parse step replaces it with the parsed record.</summary>
        public JsonElement Data { get; set; } = data;
        public IReadOnlyDictionary<string, Guid> Templates { get; } = templates;
        public Document? Document { get; set; }
        /// <summary>0 outside a For each loop; inside one, the 1-based item being run.</summary>
        public int Iteration { get; set; }
    }

    private async Task<Outcome?> RunStepAsync(Run run, Workflows.WorkflowNode node, ExecutionStep step, CancellationToken timeoutToken, CancellationToken shutdown)
    {
        var execution = run.Execution;
        var now = clock.GetUtcNow();
        if (step.Attempt >= MaxStepAttempts)
        {
            // Only after Worker crashes mid-step (a crash still counts as an attempt).
            execution.Fail("step.attempts_exhausted", $"Step '{node.Title}' was interrupted {step.Attempt} times.", now);
            Event(execution, ExecutionEventTypes.Failed, now, node.Id, Data(step, ("code", execution.FailureCode)));
            Completed(execution, now);
            await SaveAsync(execution, CancellationToken.None);
            return null;
        }

        step.Start(now);
        execution.RenewLock(now, Lease);
        Event(execution, ExecutionEventTypes.StepStarted, now, node.Id, Data(step, ("attempt", step.Attempt)));
        if (!await SaveAsync(execution, shutdown))
        {
            return null;
        }

        try
        {
            var outcome = await ExecuteNodeAsync(run, node, timeoutToken);
            now = clock.GetUtcNow();
            step.Succeed(outcome.Port, outcome.Result is null ? null : JsonSerializer.Serialize(outcome.Result, Json), now);
            execution.RenewLock(now, Lease);
            Event(execution, ExecutionEventTypes.StepSucceeded, now, node.Id, Data(step, ("port", outcome.Port)));
            if (!await SaveAsync(execution, CancellationToken.None))
            {
                return null;
            }

            if (outcome.AfterSave is not null)
            {
                await outcome.AfterSave();
            }

            return outcome;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // The Worker is stopping. Leave the run resumable: Hangfire re-queues the job and the next run
            // continues here (or the dispatcher re-queues it if that doesn't happen).
            // A step that changes the outside world (Write to database, Send SMS) stays Running: its statement or message may already have left, so the next run
            // must not send it again (StepAsync fails the run with write.outcome_unknown / sms.outcome_unknown when it finds the step Running).
            if (step.Status == StepStatus.Running && !NodeCatalog.ChangesOutsideWorld(node.Type))
            {
                step.Interrupt();
            }

            execution.ReleaseLock();
            await SaveAsync(execution, CancellationToken.None);
            throw;
        }
#pragma warning disable CA1031 // every other failure of a step is recorded on it; nothing escapes unrecorded
        catch (Exception ex)
#pragma warning restore CA1031
        {
            now = clock.GetUtcNow();
            var (code, message, retryable) = Describe(ex, node.Type, timeoutToken);
            step.Fail(message, now);
            LogStepFailed(logger, node.Id, execution.Id, step.Attempt, code);
            Event(execution, ExecutionEventTypes.StepFailed, now, node.Id, Data(step, ("attempt", step.Attempt), ("code", code)));
            if (retryable && step.Attempt < MaxStepAttempts)
            {
                var delay = RetryDelay(step.Attempt + 1);
                execution.ScheduleRetry(now + delay);
                Event(execution, ExecutionEventTypes.RetryScheduled, now, node.Id, Data(step, ("attempt", step.Attempt + 1), ("dueAt", now + delay)));
                if (await SaveAsync(execution, CancellationToken.None))
                {
                    ScheduleRetry(execution.Id, delay);
                }

                return null;
            }

            execution.Fail(code, $"Step '{node.Title}' failed after {step.Attempt} attempt(s): {message}", now);
            Event(execution, ExecutionEventTypes.Failed, now, node.Id, Data(step, ("code", code)));
            Completed(execution, now);
            await SaveAsync(execution, CancellationToken.None);
            return null;
        }
    }

    private Task<Outcome> ExecuteNodeAsync(Run run, Workflows.WorkflowNode node, CancellationToken ct) => node.Type switch
    {
        // The payload arrived with the submission (for folder-input, the Worker read the file and submitted it).
        NodeCatalog.ApiTrigger or NodeCatalog.ErpConnector or NodeCatalog.SftpFolder or NodeCatalog.EmailInbox or NodeCatalog.DatabaseQuery or NodeCatalog.FormSubmission or NodeCatalog.ScheduledTrigger
            or NodeCatalog.ManualTrigger or NodeCatalog.FolderInput => Task.FromResult(new Outcome("out", null)),
        NodeCatalog.FileUpload => FileUploadAsync(run, node, ct),
        NodeCatalog.LookupEnrich => Task.FromResult(LookupEnrich(node)),
        NodeCatalog.Deduplicate => Task.FromResult(Deduplicate(node)),
        NodeCatalog.WaitBranches => Task.FromResult(new Outcome("out", new { branches = Fanout.Paths.Count })),
        NodeCatalog.WaitEvent => Task.FromResult(WaitEvent(node)),
        NodeCatalog.ParseContent =>Task.FromResult(Parse(run, node)),
        NodeCatalog.MapFields => Task.FromResult(Map(run, node)),
        NodeCatalog.TransformData => Task.FromResult(Transform(run, node)),
        NodeCatalog.MergeData => Task.FromResult(Merge(run, node)),
        NodeCatalog.IfElse => Task.FromResult(Branch(run, node)),
        NodeCatalog.SwitchRoute => Task.FromResult(Route(run, node)),
        NodeCatalog.ForEach => Task.FromResult(Repeat(run, node)),
        NodeCatalog.ParallelBranches => Task.FromResult(new Outcome(Fanout.A, new { branches = Fanout.Paths.Count, name = node.Config.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : "" })),
        NodeCatalog.Validate => Task.FromResult(Validate(run, node)),
        NodeCatalog.GeneratePdf or NodeCatalog.GenerateDocx => GenerateDocumentAsync(run, node, ct),
        NodeCatalog.GenerateEmail => GenerateEmailAsync(run, node, ct),
        NodeCatalog.CombinePdfs => CombinePdfsAsync(run, node, ct),
        NodeCatalog.SendEmail => SendEmailAsync(run, node, ct),
        NodeCatalog.QueryDatabase => QueryDatabaseAsync(run, node, ct),
        NodeCatalog.WriteDatabase => WriteDatabaseAsync(run, node, ct),
        NodeCatalog.SendSms => SendSmsAsync(run, node, ct),
        NodeCatalog.FetchApi => FetchApiAsync(run, node, ct),
        NodeCatalog.PrintQueue => PrintQueueAsync(run, node, ct),
        NodeCatalog.UploadSftp => Task.FromResult(UploadSftp(node)),
        NodeCatalog.DeliverApi => Task.FromResult(DeliverApi(node)),
        NodeCatalog.RequestSignature => Task.FromResult(RequestSignature(node)),
        NodeCatalog.AssemblePack => Task.FromResult(AssemblePack(node)),
        NodeCatalog.Archive => ArchiveAsync(run, node, ct),
        _ => throw new StepException("node.unsupported", $"Node type '{node.Type}' can't run in this version of Verso.", retryable: false),
    };

    /// <summary>
    /// File upload (ADR 0023): takes the file the run was started with. The run's input holds only a reference
    /// (<c>{ "upload": { "id", ... } }</c>); the bytes wait in <c>ingestion.Uploads</c>. They are copied to this step's staging key
    /// (the row's canonical extension, never a name or path from the payload), then the row is marked Taken and its bytes dropped in
    /// the same save that records the step as succeeded. A re-run finds the copy already in the store and does not write it again.
    /// The row must belong to this run (its workflow and its run key <c>upload:&lt;id&gt;</c>); when its link to the run was lost
    /// it is repaired here. A run without an <c>upload</c> object (submitted as JSON) passes through as before.
    /// Failures are final: <c>upload.missing</c> (no such upload for this run) and <c>upload.expired</c> (its bytes are gone).
    /// The result holds the name, type, size and hash: never the bytes or the storage key.
    /// </summary>
    private async Task<Outcome> FileUploadAsync(Run run, Workflows.WorkflowNode node, CancellationToken ct)
    {
        if (run.Data.ValueKind != JsonValueKind.Object || !run.Data.TryGetProperty("upload", out var reference) || reference.ValueKind != JsonValueKind.Object)
        {
            return new Outcome("out", null);
        }

        var execution = run.Execution;
        if (!reference.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String || !Guid.TryParse(idValue.GetString(), out var uploadId))
        {
            throw UploadMissing(node);
        }

        var upload = await db.Uploads.SingleOrDefaultAsync(u => u.Id == uploadId, ct);
        if (upload is null || upload.WorkflowId != execution.WorkflowId
            || execution.IdempotencyKey != Uploads.UploadService.KeyPrefix + upload.Id.ToString("N")
            || (upload.ExecutionId is { } linked && linked != execution.Id)
            || !Uploads.UploadRules.Types.Values.Any(t => t.Extension == upload.Extension))
        {
            throw UploadMissing(node);
        }

        if (upload.Status == Domain.Ingestion.UploadStatus.Expired)
        {
            throw UploadExpired(node);
        }

        var key = DocumentKeys.Staging(execution.Id, node.Id, run.Iteration, upload.Extension);
        var stored = await store.ReadAsync(key, ct);
        if (stored is null || DocumentKeys.Sha256Hex(stored) != upload.Sha256)
        {
            if (upload.Content is not { } content)
            {
                throw UploadExpired(node);
            }

            await store.WriteAsync(key, content, ct);
        }

        if (upload.ExecutionId is null)
        {
            upload.AttachExecution(execution.Id); // the upload's own link save failed; the run key proved it is this run's file
        }

        upload.MarkTaken(); // saved with the step's success
        return new Outcome("out", new { fileName = upload.FileName, contentType = upload.ContentType, size = upload.Size, sha256 = upload.Sha256 });
    }

    private static StepException UploadMissing(Workflows.WorkflowNode node) =>
        new("upload.missing", $"Step '{node.Title}' found no uploaded file for this run. Upload the file again.", retryable: false);

    private static StepException UploadExpired(Workflows.WorkflowNode node) =>
        new("upload.expired", $"Step '{node.Title}' can't take the uploaded file: it was kept too long without being used and has been removed. Upload the file again.", retryable: false);

    /// <summary>Unparseable content is a data problem (port 'failed', the run ends Rejected unless the workflow handles it). The result holds no content.</summary>
    private static Outcome Parse(Run run, Workflows.WorkflowNode node)
    {
        var result = RecordParser.Parse(node.Config, run.Data);
        if (result.Data is not { } parsed)
        {
            return new Outcome("failed", new { issues = new[] { new { code = result.Issue!.Code, message = result.Issue.Message } } });
        }

        run.Data = parsed;
        return new Outcome("success", new { format = result.Format });
    }

    /// <summary>A row that can't be applied is a data problem (port 'failed', the run ends Rejected unless the workflow handles it). The result holds no values.</summary>
    private static Outcome Map(Run run, Workflows.WorkflowNode node)
    {
        var result = FieldMapper.Apply(node.Config, run.Data);
        if (result.Data is not { } mapped)
        {
            return new Outcome("failed", new { issues = result.Issues.Select(i => new { code = i.Code, message = i.Message, field = i.Field }) });
        }

        run.Data = mapped;
        return new Outcome("success", null);
    }

    /// <summary>A rule that can't be applied is a data problem (port 'failed', the run ends Rejected unless the workflow handles it). The result holds no values.</summary>
    private static Outcome Transform(Run run, Workflows.WorkflowNode node)
    {
        var result = DataTransformer.Apply(node.Config, run.Data);
        if (result.Data is not { } transformed)
        {
            return new Outcome("failed", new { issues = result.Issues.Select(i => new { code = i.Code, message = i.Message, field = i.Field }) });
        }

        run.Data = transformed;
        return new Outcome("success", null);
    }

    /// <summary>A record that can't be merged (or has no match, under the 'stop' strategy) is a data problem: port 'failed', the run ends Rejected unless the workflow handles it. The result holds counts, no values.</summary>
    private static Outcome Merge(Run run, Workflows.WorkflowNode node)
    {
        var result = DataMerger.Apply(node.Config, run.Data);
        if (result.Data is not { } merged)
        {
            return new Outcome("failed", new { issues = result.Issues.Select(i => new { code = i.Code, message = i.Message, field = i.Field }) });
        }

        run.Data = merged;
        return new Outcome("success", new { matched = result.Matched, unmatched = result.Unmatched, dropped = result.Dropped });
    }

    /// <summary>Takes the 'yes' or the 'no' path. Nothing can fail here and the data is unchanged, so a resumed run has nothing to rebuild. The result says which way it went, never why (no values).</summary>
    private static Outcome Branch(Run run, Workflows.WorkflowNode node)
    {
        var yes = IfElse.Evaluate(node.Config, run.Data);
        return new Outcome(yes ? IfElse.Yes : IfElse.No, new { matched = yes });
    }

    /// <summary>Leaves by the first matching branch's output, by 'other', or (no match and no 'other') nowhere: the run ends there as Succeeded. Nothing can fail and the data is unchanged. The result holds the branch id (a name chosen by the author), never the record's value.</summary>
    private static Outcome Route(Run run, Workflows.WorkflowNode node)
    {
        var port = Switch.Pick(node.Config, run.Data);
        return new Outcome(port, new { branch = port });
    }

    /// <summary>
    /// Counts the list: <c>each</c> when there are items (the engine then runs the loop), <c>empty</c> when there are none. The data is
    /// unchanged, so a resumed run has nothing to rebuild. The result holds the count, never the items; when the loop contains a Write to database or Send SMS step it also
    /// holds the list's <see cref="ForEach.Fingerprint"/> (a keyed hash, no value), which <see cref="RunLoopAsync"/> compares on resume.
    /// </summary>
    private static Outcome Repeat(Run run, Workflows.WorkflowNode node)
    {
        var items = ForEach.Items(node.Config, run.Data);
        var count = items.Count;
        if (count > ForEach.MaxItems)
        {
            throw new StepException("foreach.too_many_items", $"The list has {count} items; at most {ForEach.MaxItems} are allowed in a For each.", retryable: false);
        }

        var port = count == 0 ? ForEach.Empty : ForEach.Each;
        return LoopChangesOutsideWorld(run.Plan, node.Id)
            ? new Outcome(port, new { count, itemsFingerprint = ForEach.Fingerprint(run.Execution.Id, node.Id, items) })
            : new Outcome(port, new { count });
    }

    /// <summary>
    /// Whether the loop of this For each contains, anywhere in its body (behind a branch too), a step that changes the outside world
    /// (<see cref="NodeCatalog.ChangesOutsideWorld"/>: Write to database, Send SMS).
    /// </summary>
    private static bool LoopChangesOutsideWorld(ExecutionPlan plan, string forEachId) =>
        plan.Loops.FirstOrDefault(l => l.ForEach.Id == forEachId) is { } loop
        && loop.Body.Any(id => NodeCatalog.ChangesOutsideWorld(plan.Node(id).Type));

    /// <summary>Whether the loop of this For each contains a step of <paramref name="type"/> anywhere in its body.</summary>
    private static bool LoopContains(ExecutionPlan plan, string forEachId, string type) =>
        plan.Loops.FirstOrDefault(l => l.ForEach.Id == forEachId) is { } loop
        && loop.Body.Any(id => plan.Node(id).Type == type);

    /// <summary>The fingerprint the first pass recorded in the For each step's result; null when there is none.</summary>
    private static string? RecordedFingerprint(ExecutionStep step)
    {
        if (step.Result is null)
        {
            return null;
        }

        using var result = JsonDocument.Parse(step.Result);
        return result.RootElement.ValueKind == JsonValueKind.Object
               && result.RootElement.TryGetProperty("itemsFingerprint", out var f) && f.ValueKind == JsonValueKind.String
            ? f.GetString()
            : null;
    }

    private static int? RecordedCount(ExecutionStep step)
    {
        if (step.Result is null)
        {
            return null;
        }

        using var result = JsonDocument.Parse(step.Result);
        return result.RootElement.ValueKind == JsonValueKind.Object
               && result.RootElement.TryGetProperty("count", out var c) && c.TryGetInt32(out var n)
            ? n
            : null;
    }

    private static Outcome Validate(Run run, Workflows.WorkflowNode node)
    {
        var issues = ValidationRules.Check(node.Config, run.Data);
        return new Outcome(issues.Count == 0 ? "valid" : "invalid", new { issues });
    }

    /// <summary>Generate PDF (Chromium prints the renderer's parts) or Generate DOCX (the resolved layout written as Word). Same pinning, issues, storage and idempotency.</summary>
    private async Task<Outcome> GenerateDocumentAsync(Run run, Workflows.WorkflowNode node, CancellationToken ct)
    {
        var execution = run.Execution;
        var isDocx = node.Type == NodeCatalog.GenerateDocx;

        // Idempotent: a document from an earlier, interrupted attempt of this step is reused.
        if (await FindDocumentAsync(execution.Id, node.Id, run.Iteration, ct) is { } existing)
        {
            run.Document = existing;
            return new Outcome("success", DocumentResult(existing, []));
        }

        if (!run.Templates.TryGetValue(node.Id, out var templateVersionId))
        {
            throw new StepException("template.not_resolved", "No template version was pinned for this step at submission.", retryable: false);
        }

        var version = await db.TemplateVersions.AsNoTracking().SingleAsync(v => v.Id == templateVersionId, ct);
        var templateName = await db.Templates.AsNoTracking().Where(t => t.Id == version.TemplateId).Select(t => t.Name).SingleAsync(ct);
        var (layout, _) = LayoutParser.Parse(version.Content.Layout);
        if (layout is null)
        {
            throw new StepException("template.layout_invalid", "The pinned template version has an invalid layout.", retryable: false);
        }

        var page = TemplatePreview.ToPageSetup(version.Content.PageSettings);
        ResolvedDocument? resolved = null;
        RenderResult? rendered = null;
        if (isDocx)
        {
            resolved = DocumentResolver.Resolve(layout, page, run.Data, templateName);
        }
        else
        {
            rendered = HtmlRenderer.Render(layout, page, run.Data, RenderMode.Final, templateName);
        }

        var issues = resolved?.Issues ?? rendered!.Issues;
        var errors = issues.Where(i => i.Severity == IssueSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            // The data can't make a complete document (e.g. a bound value is missing): a data problem, not technical.
            return new Outcome("failed", new { issues = errors.Select(i => new { i.Code, i.Path, i.ElementId, i.Message }) });
        }

        byte[] content;
        if (isDocx)
        {
            var protect = node.Config.TryGetProperty("protect", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : "allow";
            content = docx.Write(resolved!, protect switch
            {
                "readOnly" => DocxProtection.ReadOnly,
                "trackedChanges" => DocxProtection.TrackedChangesOnly,
                _ => DocxProtection.AllowEdits,
            });
        }
        else
        {
            content = (await printer.PrintAsync(rendered!.Print, ct)).Content;
        }

        var extension = isDocx ? DocumentNames.Docx : DocumentNames.Pdf;
        var key = DocumentKeys.Staging(execution.Id, node.Id, run.Iteration, extension);
        await store.WriteAsync(key, content, ct);

        var pattern = PatternText.Resolve(node.Config.GetProperty("fileName").GetString()!, run.Data);
        var document = Document.Generate(
            execution.Id, node.Id, templateVersionId, DocumentNames.FileName(pattern.Text, extension), content.LongLength,
            DocumentKeys.Sha256Hex(content), store.Provider, key, clock.GetUtcNow(), iteration: run.Iteration,
            contentType: isDocx ? Document.DocxContentType : Document.PdfContentType);
        db.Documents.Add(document);
        run.Document = document;
        var warnings = issues.Where(i => i.Severity == IssueSeverity.Warning).Select(i => new { i.Code, i.Path });
        return new Outcome("success", DocumentResult(document, [.. pattern.Missing.Select(p => $"fileName: no value for '{p}'")], warnings));
    }

    /// <summary>
    /// Generate HTML email: the step's own text and the run's data become one HTML file. The renderer also builds a plain-text
    /// alternative, which is not stored (the step result is history and must not carry customer data); a future Send step rebuilds it.
    /// No template version is involved. Nothing is sent. Same idempotency, storage and rejection as the other document steps.
    /// </summary>
    private async Task<Outcome> GenerateEmailAsync(Run run, Workflows.WorkflowNode node, CancellationToken ct)
    {
        var execution = run.Execution;
        if (await FindDocumentAsync(execution.Id, node.Id, run.Iteration, ct) is { } existing)
        {
            run.Document = existing;
            return new Outcome("success", DocumentResult(existing, []));
        }

        string Text(string name) => node.Config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var layout = Text("layout") switch
        {
            "statement" => EmailLayout.Statement,
            "reminder" => EmailLayout.Reminder,
            _ => EmailLayout.Invoice,
        };
        var dark = node.Config.TryGetProperty("darkMode", out var d) && d.ValueKind == JsonValueKind.True;
        var email = EmailRenderer.Render(
            new EmailSpec(layout, Text("subject"), Text("preheader"), Text("body"), Text("buttonLabel"), Text("buttonUrl"), Text("footer"), dark), run.Data);

        var errors = email.Issues.Where(i => i.Severity == IssueSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            return new Outcome("failed", new { issues = errors.Select(i => new { i.Code, i.Path, i.ElementId, i.Message }) });
        }

        var content = System.Text.Encoding.UTF8.GetBytes(email.Html);
        var key = DocumentKeys.Staging(execution.Id, node.Id, run.Iteration, DocumentNames.Html);
        await store.WriteAsync(key, content, ct);

        var pattern = PatternText.Resolve(Text("fileName"), run.Data);
        var document = Document.Generate(
            execution.Id, node.Id, templateVersionId: null, DocumentNames.FileName(pattern.Text, DocumentNames.Html), content.LongLength,
            DocumentKeys.Sha256Hex(content), store.Provider, key, clock.GetUtcNow(), iteration: run.Iteration, contentType: Document.HtmlContentType);
        db.Documents.Add(document);
        run.Document = document;
        return new Outcome("success", new
        {
            documentId = document.Id, fileName = document.FileName, sizeBytes = document.SizeBytes, sha256 = document.Sha256,
            warnings = pattern.Missing.Select(p => $"fileName: no value for '{p}'"),
        });
    }

    /// <summary>
    /// Combine PDFs: joins the PDFs this run generated earlier (same execution and, in a For each loop, same item; not this step's own)
    /// into one new PDF. Each part is read back from storage and checked against its recorded SHA-256 first. Fewer than two PDFs is a
    /// workflow problem found while running: port 'failed', the run ends Rejected unless the workflow handles it. No template is involved.
    /// </summary>
    private async Task<Outcome> CombinePdfsAsync(Run run, Workflows.WorkflowNode node, CancellationToken ct)
    {
        var execution = run.Execution;
        if (await FindDocumentAsync(execution.Id, node.Id, run.Iteration, ct) is { } existing)
        {
            run.Document = existing;
            return new Outcome("success", DocumentResult(existing, []));
        }

        var sources = await db.Documents
            .Where(d => d.ExecutionId == execution.Id && d.Iteration == run.Iteration && d.NodeId != node.Id && d.ContentType == Document.PdfContentType)
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
            .ToListAsync(ct);
        if (sources.Count < 2)
        {
            return new Outcome("failed", new { issues = new[] { new { code = "combine.not_enough_documents", message = $"Found {sources.Count} PDF(s) generated earlier in this run; at least two are needed." } } });
        }

        if (node.Config.TryGetProperty("order", out var order) && order.ValueKind == JsonValueKind.String && order.GetString() == "reverse")
        {
            sources.Reverse();
        }

        var parts = new List<PdfPart>(sources.Count);
        foreach (var source in sources)
        {
            var bytes = await store.ReadAsync(source.StorageKey, ct)
                ?? throw new StepException("combine.source_missing", $"The stored file of '{source.FileName}' is missing.", retryable: true);
            if (DocumentKeys.Sha256Hex(bytes) != source.Sha256)
            {
                throw new StepException("storage.integrity", $"The stored file of '{source.FileName}' doesn't match its recorded SHA-256.", retryable: true);
            }

            parts.Add(new PdfPart(source.FileName, bytes));
        }

        var bookmarks = !node.Config.TryGetProperty("bookmarks", out var b) || b.ValueKind != JsonValueKind.False;
        CombinedPdf combined;
        try
        {
            combined = combiner.Combine(parts, bookmarks);
        }
        catch (PdfCombineException ex)
        {
            throw new StepException("combine.invalid_pdf", ex.Message, retryable: false);
        }

        var key = DocumentKeys.Staging(execution.Id, node.Id, run.Iteration, DocumentNames.Pdf);
        await store.WriteAsync(key, combined.Content, ct);

        var pattern = PatternText.Resolve(node.Config.GetProperty("fileName").GetString()!, run.Data);
        var document = Document.Generate(
            execution.Id, node.Id, templateVersionId: null, DocumentNames.FileName(pattern.Text, DocumentNames.Pdf), combined.Content.LongLength,
            DocumentKeys.Sha256Hex(combined.Content), store.Provider, key, clock.GetUtcNow(), iteration: run.Iteration, contentType: Document.PdfContentType);
        db.Documents.Add(document);
        run.Document = document;
        return new Outcome("success", new
        {
            documentId = document.Id, fileName = document.FileName, sizeBytes = document.SizeBytes, sha256 = document.Sha256,
            documents = sources.Count, pages = combined.PageCount,
            warnings = pattern.Missing.Select(p => $"fileName: no value for '{p}'"),
        });
    }

    /// <summary>Largest file attached to an email (relays commonly refuse more).</summary>
    public const int MaxEmailAttachmentBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Send email (ADR 0006): sends the step's text to the recipients through the SMTP relay, optionally with the run's current
    /// document (an HTML email file becomes the HTML body; any other file is attached). A recipient or text the data can't fill is
    /// a data problem (port 'failed', the run ends Rejected unless handled). A relay that is down or refuses is a technical failure
    /// (retried when it may pass). The result holds counts and the attachment's file name, never an address or the text.
    /// Delivery is at-least-once: if the Worker stops after the relay accepted the mail but before the step is saved, a retry sends it again.
    /// </summary>
    private async Task<Outcome> SendEmailAsync(Run run, Workflows.WorkflowNode node, CancellationToken ct)
    {
        string Text(string name) => node.Config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var issues = new List<object>();

        var to = PatternText.Resolve(Text("to"), run.Data);
        var (recipients, problem) = EmailAddresses.Parse(to.Text);
        if (problem is not null || recipients.Count == 0)
        {
            issues.Add(new { code = "email.recipient_invalid", message = problem ?? "There is no recipient: the 'To' setting resolved to nothing." });
        }

        var subject = PatternText.Resolve(Text("subject"), run.Data);
        var subjectLine = EmailAddresses.OneLine(subject.Text);
        if (subjectLine.Length == 0)
        {
            issues.Add(new { code = "email.subject_empty", message = "The subject resolved to nothing." });
        }

        var body = PatternText.Resolve(Text("body"), run.Data);
        if (issues.Count > 0)
        {
            return new Outcome("failed", new { issues });
        }

        var warnings = subject.Missing.Select(p => $"subject: no value for '{p}'").Concat(body.Missing.Select(p => $"body: no value for '{p}'")).ToList();
        var attachments = new List<EmailAttachment>();
        string? html = null;
        string? attached = null;
        if (Text("attach") == "document")
        {
            var document = run.Document
                ?? throw new StepException("email.no_document", "There is no generated document to attach: put a Generate step before Send email on this path, or set 'Attach' to nothing.", retryable: false);
            if (document.SizeBytes > MaxEmailAttachmentBytes)
            {
                throw new StepException("email.attachment_too_large", $"'{document.FileName}' is larger than {MaxEmailAttachmentBytes / (1024 * 1024)} MB.", retryable: false);
            }

            var bytes = await store.ReadAsync(document.StorageKey, ct)
                ?? throw new StepException("storage.missing", "The generated document is missing from storage.", retryable: true);
            if (DocumentKeys.Sha256Hex(bytes) != document.Sha256)
            {
                throw new StepException("storage.integrity", "The stored document doesn't match its recorded SHA-256.", retryable: true);
            }

            if (document.ContentType == Document.HtmlContentType)
            {
                html = System.Text.Encoding.UTF8.GetString(bytes);
            }
            else
            {
                attachments.Add(new EmailAttachment(document.FileName, document.ContentType, bytes));
            }

            attached = document.FileName;
        }

        var relay = EmailRelays.Resolve(Text("relay"));
        if (!EmailRelays.IsValidKey(relay))
        {
            throw new StepException("email.relay_unknown", "The 'Email connection' setting is not a valid relay key (lower-case letters, digits and hyphens).", retryable: false);
        }

        string messageId;
        try
        {
            messageId = await email.SendAsync(new EmailMessage(recipients, subjectLine, body.Text, html, attachments, relay), ct);
        }
        catch (EmailSendException ex)
        {
            throw new StepException(ex.Code, ex.Message, ex.Retryable);
        }

        return new Outcome("sent", new { relay, recipients = recipients.Count, attachment = attached, htmlBody = html is not null, messageId, warnings });
    }

    /// <summary>
    /// Query database (ADR 0019): runs the step's one SELECT on the named Database connection, its @parameters bound from the run data, and merges what it
    /// read into the data under the result name (one row an object, several rows a list; an existing field of that name is replaced). A parameter whose path
    /// has no value, or holds an object or a list, is the data's fault: port 'failed' (the run ends Rejected unless handled) and the database is not called.
    /// A failure of the query itself is technical (the author's SQL, the connection or the database): retried when it may pass, otherwise the run fails.
    /// Zero rows leave by 'not-found' with the data unchanged. The result holds counts, column names and the connection key; never a value, never the SQL.
    /// </summary>
    private async Task<Outcome> QueryDatabaseAsync(Run run, Workflows.WorkflowNode node, CancellationToken ct)
    {
        var (bound, issues) = QueryParameters(node, run.Data);
        if (issues.Count > 0)
        {
            return new Outcome("failed", new { issues });
        }

        var connection = QuerySetting(node, "connection");
        var read = await RunQueryAsync(node, connection, bound, ct);
        if (read.Rows.Count == 0)
        {
            return new Outcome("not-found", QueryResult(read, connection));
        }

        run.Data = WithQueryResult(node, run.Data, read);
        return new Outcome("found", QueryResult(read, connection));
    }

    private static string QuerySetting(Workflows.WorkflowNode node, string name) =>
        node.Config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static int QueryNumber(Workflows.WorkflowNode node, string name, int fallback) =>
        node.Config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : fallback;

    /// <summary>The step result: counts and names only (the API returns step results to every reader of the execution).</summary>
    private static object QueryResult(DatabaseQueryResult read, string connection) =>
        new { rows = read.Rows.Count, columns = read.Columns, truncated = read.Truncated, durationMs = read.DurationMs, connection };

    /// <summary>
    /// The values for the step's parameters, read from <paramref name="data"/> with the same dotted paths as every other step. Only text, numbers and
    /// true/false can be bound. Issues name the parameter and its path, never a value.
    /// </summary>
    private static (Dictionary<string, object?> Bound, List<object> Issues) QueryParameters(Workflows.WorkflowNode node, JsonElement data)
    {
        var bound = new Dictionary<string, object?>(StringComparer.Ordinal);
        var issues = new List<object>();
        var (mappings, problem) = DatabaseQueryStep.ParseParameters(QuerySetting(node, "parameters"));
        if (problem is not null)
        {
            // Checked when the workflow was published, so only a definition changed outside Verso gets here: the author's mistake, not the data's.
            throw new StepException("sql.bad_parameter", problem, retryable: false);
        }

        foreach (var (name, path) in mappings)
        {
            if (!DataPath.TryResolve(data, path, out var value))
            {
                issues.Add(new { code = "query.parameter_missing", message = $"Parameter '{name}' has no value: '{path}' is missing or null in the data (an empty text is a value).", field = path });
                continue;
            }

            object? scalar = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when value.TryGetInt64(out var whole) => whole,
                JsonValueKind.Number when value.TryGetDecimal(out var exact) => exact,
                JsonValueKind.Number => value.GetDouble(),
                _ => null,
            };
            if (scalar is null)
            {
                issues.Add(new { code = "query.parameter_invalid", message = $"Parameter '{name}': '{path}' holds {(value.ValueKind == JsonValueKind.Array ? "a list" : "a group of fields")}; only text, a number or true/false can be passed.", field = path });
                continue;
            }

            bound[name] = scalar;
        }

        return (bound, issues);
    }

    /// <summary>Runs the query. A failure becomes a technical <see cref="StepException"/> with Verso's own message (never the driver's text or a value).</summary>
    private async Task<DatabaseQueryResult> RunQueryAsync(Workflows.WorkflowNode node, string connection, IReadOnlyDictionary<string, object?> bound, CancellationToken ct)
    {
        var request = new DatabaseQueryRequest(connection, QuerySetting(node, "sql"), bound,
            QueryNumber(node, "maxRows", 1), TimeSpan.FromSeconds(QueryNumber(node, "timeoutSeconds", 15)));
        DatabaseQueryResult read;
        try
        {
            read = await query.RunAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The service reports failures as results; anything else is unexpected and its text could name the server or the login, so it is not kept.
            throw new StepException("db.query_failed", DatabaseQueryMessages.For("db.query_failed"), retryable: false); // final, as in DatabaseQueryService: an unknown failure is not retried blindly
        }

        if (!read.Ok)
        {
            throw new StepException(read.Code!, read.Message ?? DatabaseQueryMessages.For(read.Code!), read.Retryable);
        }

        return read;
    }

    /// <summary>The data with the rows stored under the result name at the top level: one row an object, several a list. A field of that name is replaced.</summary>
    private static JsonElement WithQueryResult(Workflows.WorkflowNode node, JsonElement data, DatabaseQueryResult read)
    {
        EnsureRecord(data, "query.data_not_object");
        JsonNode? rows;
        try
        {
            rows = read.Rows.Count == 1
                ? JsonSerializer.SerializeToNode(read.Rows[0], Json)
                : JsonSerializer.SerializeToNode(read.Rows, Json);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException or ArgumentException)
        {
            throw new StepException("query.value_unsupported", "A column has a type that can't be added to the data. Convert it in the query, for example with CAST(... AS varchar).", retryable: false);
        }

        return WithResult(data, QuerySetting(node, "resultName"), rows, "query.data_not_object");
    }

    /// <summary>A step result can only be added to a record: data whose root is not a JSON object fails the step (not retryable) with <paramref name="code"/>.</summary>
    private static void EnsureRecord(JsonElement data, string code)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            throw new StepException(code, "The run's data isn't a record (a JSON object), so the result can't be added to it.", retryable: false);
        }
    }

    /// <summary>
    /// The data with <paramref name="value"/> stored under <paramref name="resultName"/> at the top level (a field of that name is replaced). Shared by Query
    /// database and Fetch from API so both merge identically. The same rebuild as the Map, Transform and Merge steps: parse to a mutable tree, change it, and
    /// read it back as an element that owns its memory.
    /// </summary>
    private static JsonElement WithResult(JsonElement data, string resultName, JsonNode? value, string notRecordCode)
    {
        EnsureRecord(data, notRecordCode);
        var root = (JsonObject)JsonNode.Parse(data.GetRawText())!;
        root[resultName] = value;
        return JsonSerializer.Deserialize<JsonElement>(root.ToJsonString());
    }

    /// <summary>
    /// Fetch from API: sends one GET to the named API connection, its path and query values filled from the run data, and merges the JSON answer into the data
    /// under the result name at the top level, exactly as Query database merges its rows (a field of that name is replaced). A value the data can't give
    /// (missing or null, a list or a group, text that can't be sent, a path piece that would be empty, . or ..) is the data's fault: port 'failed' (the run ends
    /// Rejected unless handled) and the API is not called. A failure of the call is technical: retried when it may pass (timeouts, unreachable, 5xx, 429),
    /// otherwise the run fails. HTTP 404, an empty answer or a JSON null leave by 'not-found' with the data unchanged. The result holds the HTTP status, the
    /// duration, the answer's size and the connection key; never the path, a query value, the answer or a secret.
    /// </summary>
    private async Task<Outcome> FetchApiAsync(Run run, Workflows.WorkflowNode node, CancellationToken ct)
    {
        var (request, issues) = FetchRequest(node, run.Data);
        if (request is null)
        {
            return new Outcome("failed", new { issues });
        }

        var fetched = await RunFetchAsync(request, ct);
        if (!HasAnswer(fetched))
        {
            return new Outcome("not-found", FetchResult(fetched, request.ConnectionKey));
        }

        run.Data = WithFetchResult(node, run.Data, fetched);
        return new Outcome("found", FetchResult(fetched, request.ConnectionKey));
    }

    /// <summary>The step result: the HTTP status, counts and the connection key only (the API returns step results to every reader of the execution).</summary>
    private static object FetchResult(ApiFetchResult fetched, string connection) =>
        new { status = fetched.HttpStatus, durationMs = fetched.DurationMs, responseBytes = fetched.ResponseBytes, connection };

    /// <summary>A JSON null answer counts as no answer: merging it would only blank the field (a 404, an empty answer and null all mean "nothing found").</summary>
    private static bool HasAnswer(ApiFetchResult fetched) => fetched.Found && fetched.Body!.Value.ValueKind != JsonValueKind.Null;

    /// <summary>
    /// The request for the step, its path and query values filled from <paramref name="data"/> (<see cref="ApiPathTemplate.Resolve"/> and
    /// <see cref="ApiFetchStep.ResolveQuery"/>). Null with the issues (path ones first) when the data can't give a value; issues name paths, never a value.
    /// </summary>
    private static (ApiFetchRequest? Request, List<object> Issues) FetchRequest(Workflows.WorkflowNode node, JsonElement data)
    {
        // Both are checked when the workflow was published, so only a definition changed outside Verso gets here: the author's mistake, not the data's.
        var template = QuerySetting(node, "path");
        if (ApiPathTemplate.Validate(template) is { } pathProblem)
        {
            throw new StepException("fetch.bad_path", pathProblem, retryable: false);
        }

        var (lines, queryProblem) = ApiFetchStep.ParseQuery(QuerySetting(node, "query"));
        if (queryProblem is not null)
        {
            throw new StepException("fetch.bad_query", queryProblem, retryable: false);
        }

        var (path, pathIssues) = ApiPathTemplate.Resolve(template, data);
        var (pairs, queryIssues) = ApiFetchStep.ResolveQuery(lines, data);
        var issues = pathIssues.Concat(queryIssues).ToList();
        if (issues.Count > 0 || path is null)
        {
            return (null, issues);
        }

        // The service clamps the limits again (1 KB..256 KB, 1..60 s); clamped here too so the request says what will happen.
        var maxBytes = Math.Clamp(QueryNumber(node, "maxResponseKB", 64), 1, 256) * 1024;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(QueryNumber(node, "timeoutSeconds", 15), 1, 60));
        return (new ApiFetchRequest(QuerySetting(node, "connection"), path, pairs, maxBytes, timeout), issues);
    }

    /// <summary>
    /// Sends the request. A failure becomes a <see cref="StepException"/> with the service's code, Verso's own message and its retryability (only timeouts,
    /// unreachable, 5xx and 429 are retryable). An exception escaping the service itself (it reports failures as results, and already maps a sender's surprise
    /// to a retryable api.unreachable) is unexpected and its text could carry the address or a secret: it is not kept, and the failure is final, as for the
    /// database steps (an unknown failure is not retried blindly). A cancellation is left to RunStepAsync.
    /// </summary>
    private async Task<ApiFetchResult> RunFetchAsync(ApiFetchRequest request, CancellationToken ct)
    {
        ApiFetchResult fetched;
        try
        {
            fetched = await apiFetch.RunAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StepException("api.unreachable", ApiFetchMessages.For("api.unreachable"), retryable: false);
        }

        if (!fetched.Ok)
        {
            throw new StepException(fetched.Code!, fetched.Message ?? ApiFetchMessages.For(fetched.Code!), fetched.Retryable);
        }

        return fetched;
    }

    /// <summary>The data with the answer merged under the result name (see <see cref="WithResult"/>). An answer nested so deep that one more level can't be kept fails the step.</summary>
    private static JsonElement WithFetchResult(Workflows.WorkflowNode node, JsonElement data, ApiFetchResult fetched)
    {
        EnsureRecord(data, "fetch.data_not_object");
        try
        {
            return WithResult(data, QuerySetting(node, "resultName"), JsonNode.Parse(fetched.Body!.Value.GetRawText()), "fetch.data_not_object");
        }
        catch (JsonException)
        {
            // The sender accepts answers up to 64 levels deep; under the result name the data would be one level deeper than JSON readers accept.
            throw new StepException("fetch.answer_too_deep", "The API's answer is nested too deeply to be added to the data. Ask the API for less, or a flatter answer.", retryable: false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // JSON allows a "\uD800" escape (a lone surrogate) and the sender accepts it, but it is not text .NET can read back: the answer can't be kept. Final
            // (the same answer would fail again); the exception's text is not kept.
            throw new StepException("fetch.answer_unsupported", "The API's answer contains text that can't be added to the data (an incomplete Unicode character).", retryable: false);
        }
    }

    /// <summary>
    /// Write to database: runs the step's one INSERT, UPDATE or DELETE on the named Database connection (writes must be switched on for it), its @parameters bound
    /// from the run data with the same rules as Query database. A parameter whose path has no value, or holds an object or a list, is the data's fault: port 'failed'
    /// (the run ends Rejected unless handled) and the database is not called. Every failure of the write itself ends the run Failed and is never retried
    /// automatically: the statement may have reached the database, so only a person decides to run it again (the message says when the outcome is unknown).
    /// The run's data is not changed. The result holds the row count, the duration and the connection key; never a value, never the SQL.
    /// </summary>
    private async Task<Outcome> WriteDatabaseAsync(Run run, Workflows.WorkflowNode node, CancellationToken ct)
    {
        var (bound, issues) = QueryParameters(node, run.Data);
        if (issues.Count > 0)
        {
            return new Outcome("failed", new { issues });
        }

        var connection = QuerySetting(node, "connection");
        var request = new DatabaseWriteRequest(connection, QuerySetting(node, "sql"), bound,
            QueryNumber(node, "maxRows", 1), TimeSpan.FromSeconds(QueryNumber(node, "timeoutSeconds", 15)));
        DatabaseWriteResult written;
        try
        {
            written = await write.RunAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The service reports failures as results; anything else is unexpected, may have happened after the statement was sent, and its text could name
            // the server or the login, so it is not kept. A cancellation is left to RunStepAsync (shutdown: the step stays Running; otherwise a final failure).
            throw new StepException("db.query_failed", DatabaseQueryMessages.OutcomeUnknown, retryable: false);
        }

        if (!written.Ok)
        {
            // Never retryable, whatever the code: the message already carries the outcome-unknown warning when the statement may have run.
            throw new StepException(written.Code!, written.Message ?? (written.OutcomeUnknown ? DatabaseQueryMessages.OutcomeUnknown : DatabaseQueryMessages.ForWrite(written.Code!)), retryable: false);
        }

        // Audited when the write is known to have happened; the entry is saved with the step's success (the same SaveChanges in RunStepAsync). Counts and keys only.
        var execution = run.Execution;
        db.AuditEntries.Add(AuditEntry.Record(
            ExecutionService.Actions.DatabaseWriteExecuted, ExecutionService.EntityType, execution.Id.ToString(), ActorId, ActorName, clock.GetUtcNow(),
            JsonSerializer.Serialize(new { executionId = execution.Id, nodeId = node.Id, iteration = run.Iteration, connection, rowsAffected = written.RowsAffected }),
            execution.CorrelationId));
        return new Outcome("written", new { rowsAffected = written.RowsAffected, durationMs = written.DurationMs, connection });
    }

    /// <summary>
    /// Send SMS: sends ONE message through the named SMS connection (sending must be switched on for it), to a number and with a text built from the run data.
    /// A value that is missing or unusable, a number that isn't a valid international number, or (reported by the service) a number in a country the connection
    /// may not send to or a text that can't be sent is the data's fault: port 'failed' (the run ends Rejected unless handled) and, when the engine finds it, the
    /// service is not called. Every other failure ends the run Failed and is never retried automatically: the message may have left, so only a person decides to
    /// send it again (the message says when the outcome is unknown). The run's data is not changed. The result and the audit entry hold the provider's message
    /// id, the parts, the duration and the connection key; NEVER the number, the text or the tag (nothing here logs them either).
    /// </summary>
    private async Task<Outcome> SendSmsAsync(Run run, Workflows.WorkflowNode node, CancellationToken ct)
    {
        var connection = QuerySetting(node, "connection");
        var (to, toIssues) = SmsTemplates.Resolve(QuerySetting(node, "to"), run.Data, isText: false);
        var (text, textIssues) = SmsTemplates.Resolve(QuerySetting(node, "text"), run.Data, isText: true);
        var issues = new List<object>(toIssues);
        var digits = "";
        if (toIssues.Count == 0 && !SmsRules.TryNormaliseRecipient(to, out digits))
        {
            issues.Add(SmsIssue("sms.recipient_invalid"));
        }

        issues.AddRange(textIssues);
        if (issues.Count > 0)
        {
            return new Outcome("failed", new { issues });
        }

        var execution = run.Execution;
        var request = new SmsSendRequest(connection, digits, text!, $"verso-{execution.Id.ToString("N")[..12]}-{run.Iteration}");
        SmsSendResult sent;
        try
        {
            sent = await sms.RunAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The service reports failures as results; anything else is unexpected, may have happened after the request left, and its text could hold the
            // number or the text, so it is not kept. A cancellation is left to RunStepAsync: on shutdown the step stays Running (StepAsync then fails the run
            // sms.outcome_unknown), otherwise Describe makes it a final unknown failure (execution.timeout or sms.unreachable).
            throw new StepException("sms.unreachable", SmsSendMessages.OutcomeUnknown, retryable: false);
        }

        if (!sent.Ok)
        {
            if (!sent.OutcomeUnknown && sent.Code is "sms.recipient_invalid" or "sms.country_not_allowed" or "sms.text_invalid")
            {
                // The service refused the data before sending (nothing left): a data issue, the run ends Rejected. The issue names the rule, never the value.
                return new Outcome("failed", new { issues = new[] { SmsIssue(sent.Code) } });
            }

            // Never retryable, whatever the code: the service's message already carries the outcome-unknown warning when the message may have left.
            throw new StepException(sent.Code!, sent.Message ?? (sent.OutcomeUnknown ? SmsSendMessages.OutcomeUnknown : SmsSendMessages.For(sent.Code!)), retryable: false);
        }

        // Audited when the message is known to have been accepted; the entry is saved with the step's success (the same SaveChanges in RunStepAsync). No number,
        // no text, no tag.
        db.AuditEntries.Add(AuditEntry.Record(
            ExecutionService.Actions.SmsSent, ExecutionService.EntityType, execution.Id.ToString(), ActorId, ActorName, clock.GetUtcNow(),
            JsonSerializer.Serialize(new { executionId = execution.Id, nodeId = node.Id, iteration = run.Iteration, connection, messageId = sent.MessageId, smsParts = sent.SmsParts }),
            execution.CorrelationId));
        return new Outcome("sent", new { messageId = sent.MessageId, smsParts = sent.SmsParts, durationMs = sent.DurationMs, connection });
    }

    /// <summary>A data issue of the Send SMS step for a rule on the whole number or text: the rule and the field, never the value.</summary>
    private static SmsDataIssue SmsIssue(string code) => code switch
    {
        "sms.country_not_allowed" => new(code, "The recipient's number is in a country this SMS connection may not send to.", "to"),
        "sms.text_invalid" => new(code, "The message can't be sent (it is empty, too long or has a character that can't be sent).", "text"),
        _ => new(code, "The recipient's number isn't a valid international number (8 to 15 digits, starting with the country code).", "to"),
    };

    /// <summary>One data issue of the Send SMS step, serialised as <c>{ code, message, field }</c> like every other issue.</summary>
    private sealed record SmsDataIssue(string Code, string Message, string Field);

    /// <summary>
    /// A step that changes the outside world (<see cref="NodeCatalog.ChangesOutsideWorld"/>) found Running: an earlier run started it and never recorded how it
    /// ended. It is not run again; it is recorded failed with the outcome-unknown warning and the run fails (not retried), with the same events as any other final
    /// failure. An operator retry runs it again. Write to database: <c>write.outcome_unknown</c>; Send SMS: <c>sms.outcome_unknown</c>.
    /// </summary>
    private async Task FailUnfinishedAsync(Run run, Workflows.WorkflowNode node, ExecutionStep step)
    {
        var (code, stepError, runMessage) = node.Type == NodeCatalog.SendSms
            ? ("sms.outcome_unknown", SmsSendMessages.OutcomeUnknown,
                $"Step '{node.Title}' was started but never recorded as finished (the Worker stopped or crashed): the message may or may not have been sent. Check the SMS provider's log before retrying.")
            : ("write.outcome_unknown", DatabaseQueryMessages.OutcomeUnknown,
                $"Step '{node.Title}' was started but never recorded as finished (the Worker stopped or crashed): the statement may or may not have changed data. Check the database before retrying.");
        var execution = run.Execution;
        var now = clock.GetUtcNow();
        step.Fail(stepError, now);
        LogStepFailed(logger, node.Id, execution.Id, step.Attempt, code);
        Event(execution, ExecutionEventTypes.StepFailed, now, node.Id, Data(step, ("attempt", step.Attempt), ("code", code)));
        execution.Fail(code, runMessage, now);
        Event(execution, ExecutionEventTypes.Failed, now, node.Id, Data(step, ("code", code)));
        Completed(execution, now);
        await SaveAsync(execution, CancellationToken.None);
    }

    /// <summary>Upload to SFTP is simulated: nothing connects to a server. The step succeeds and its result says that nothing was uploaded.</summary>
    private static Outcome UploadSftp(Workflows.WorkflowNode node)
    {
        string Text(string name) => node.Config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        return new Outcome("success", new { simulated = true, uploaded = false, host = Text("host"), folder = Text("folder") });
    }

    /// <summary>Assemble document pack is simulated: nothing is assembled and no file is made. The step takes the 'success' path; its result holds counts and names from the step's settings, no document content.</summary>
    private static Outcome AssemblePack(Workflows.WorkflowNode node)
    {
        string Text(string name) => node.Config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var files = Text("files").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        return new Outcome("success", new { simulated = true, assembled = false, files, format = Text("format"), fileName = Text("filename") });
    }

    /// <summary>Request e-signature is simulated: nothing is sent to a signing provider. The step takes the 'submitted' path; its result holds no addresses, only how many signers there would be.</summary>
    private static Outcome RequestSignature(Workflows.WorkflowNode node)
    {
        string Text(string name) => node.Config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        return new Outcome("submitted", new { simulated = true, sent = false, document = Text("document"), signers = EmailAddresses.Parse(Text("signers")).Addresses.Count, order = Text("order") });
    }

    /// <summary>Wait for event is simulated: nothing is awaited. The step takes the path chosen in 'preview' ('received' unless set to 'timeout').</summary>
    private static Outcome WaitEvent(Workflows.WorkflowNode node)
    {
        string Text(string name) => node.Config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var timedOut = Text("preview") == "timeout";
        return new Outcome(timedOut ? "timeout" : "received", new { simulated = true, waited = false, outcome = timedOut ? "timeout" : "received", @event = Text("event"), correlation = Text("correlation") });
    }

    /// <summary>Deduplicate is simulated: nothing is stored or compared. The step takes the path chosen in 'preview' ('unique' unless set to 'duplicate').</summary>
    private static Outcome Deduplicate(Workflows.WorkflowNode node)
    {
        string Text(string name) => node.Config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var duplicate = Text("preview") == "duplicate";
        return new Outcome(duplicate ? "duplicate" : "unique", new { simulated = true, checkedAgainstStore = false, outcome = duplicate ? "duplicate" : "unique", key = Text("key"), scope = Text("scope") });
    }

    /// <summary>Lookup / enrich is simulated: nothing is looked up and the run's data is not changed. The step takes the 'found' path and its result says that nothing was looked up.</summary>
    private static Outcome LookupEnrich(Workflows.WorkflowNode node)
    {
        string Text(string name) => node.Config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        return new Outcome("found", new { simulated = true, lookedUp = false, conn = Text("conn"), key = Text("key"), destination = Text("destination") });
    }

    /// <summary>Deliver via API is simulated: nothing is sent. The step takes the 'delivered' path and its result says that nothing was delivered.</summary>
    private static Outcome DeliverApi(Workflows.WorkflowNode node)
    {
        var url = node.Config.TryGetProperty("url", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        return new Outcome("delivered", new { simulated = true, sent = false, url });
    }

    /// <summary>
    /// Print queue (ADR 0007): writes the run's current PDF and a job file into the spool folder of the named queue. The job id is derived
    /// from (execution, step, iteration), so a retry rewrites the same files rather than queueing a second job. The PDF is read back
    /// from storage and its SHA-256 checked first. Only PDFs can be printed. The result holds the job id and queue key, no content.
    /// </summary>
    private async Task<Outcome> PrintQueueAsync(Run run, Workflows.WorkflowNode node, CancellationToken ct)
    {
        string Text(string name) => node.Config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var queue = Text("queue");
        if (!PrintQueues.IsValidKey(queue))
        {
            throw new StepException("print.queue_invalid", $"Step '{node.Title}' has no valid print queue key.", retryable: false);
        }

        var document = run.Document
            ?? throw new StepException("print.no_document", "There is no generated document to print: put a Generate PDF step before Print queue on this path.", retryable: false);
        if (document.ContentType != Document.PdfContentType)
        {
            throw new StepException("print.not_pdf", $"'{document.FileName}' isn't a PDF; only PDFs can be printed.", retryable: false);
        }

        var bytes = await store.ReadAsync(document.StorageKey, ct)
            ?? throw new StepException("storage.missing", "The generated document is missing from storage.", retryable: true);
        if (DocumentKeys.Sha256Hex(bytes) != document.Sha256)
        {
            throw new StepException("storage.integrity", "The stored document doesn't match its recorded SHA-256.", retryable: true);
        }

        var envelope = PrintQueues.Envelopes.ContainsKey(Text("envelope")) ? Text("envelope") : PrintQueues.DefaultEnvelope;
        var duplex = !node.Config.TryGetProperty("duplex", out var d) || d.ValueKind != JsonValueKind.False;
        var jobId = DocumentKeys.Sha256Hex(System.Text.Encoding.UTF8.GetBytes($"{run.Execution.Id:N}/{node.Id}/{run.Iteration}"))[..32];
        try
        {
            await spooler.SpoolAsync(new PrintJob(jobId, queue, document.FileName, bytes, document.Sha256, envelope, duplex, clock.GetUtcNow()), ct);
        }
        catch (PrintSpoolException ex)
        {
            throw new StepException(ex.Code, ex.Message, ex.Retryable);
        }

        return new Outcome("queued", new { jobId, queue, fileName = document.FileName, sizeBytes = document.SizeBytes, envelope, duplex });
    }

    private async Task<Outcome> ArchiveAsync(Run run, Workflows.WorkflowNode node, CancellationToken ct)
    {
        var document = run.Document
            ?? throw new StepException("archive.no_document", "There is no generated document to archive: put a Generate PDF, Generate DOCX or Generate HTML email step before Archive on this path.", retryable: false);

        if (document.Status == DocumentStatus.Archived)
        {
            var existing = await db.ArchiveRecords.AsNoTracking().SingleAsync(a => a.DocumentId == document.Id, ct);
            return new Outcome(null, new { documentId = document.Id, existing.FolderPath, existing.RetentionUntil });
        }

        var warnings = new List<string>();
        var folder = PatternText.Resolve(node.Config.GetProperty("folderPattern").GetString()!, run.Data);
        warnings.AddRange(folder.Missing.Select(p => $"folderPattern: no value for '{p}'"));
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        var matched = new List<KeyValuePair<string, string>>();
        var matchIncomplete = false;
        foreach (var pair in node.Config.GetProperty("metadata").EnumerateArray())
        {
            var key = pair.GetProperty("key").GetString()!;
            var value = PatternText.Resolve(pair.GetProperty("value").GetString()!, run.Data);
            warnings.AddRange(value.Missing.Select(p => $"metadata {key}: no value for '{p}'"));
            metadata[key] = value.Text.Length > 300 ? value.Text[..300] : value.Text;
            if (pair.TryGetProperty("match", out var match) && match.ValueKind == JsonValueKind.True)
            {
                // A match value that is missing or blank must not replace anything: an empty AccountID would match every other empty one.
                if (value.Missing.Count > 0 || string.IsNullOrWhiteSpace(value.Text))
                {
                    matchIncomplete = true;
                    warnings.Add($"metadata {key}: no value to match on, so nothing was replaced");
                }
                else
                {
                    matched.Add(new KeyValuePair<string, string>(key, metadata[key]));
                }
            }
        }

        var matchKey = matchIncomplete ? null : ArchiveMatch.Key(run.Execution.WorkflowId, matched);

        // Commit: staging → archive location, verifying the hash on the way in and after writing.
        var content = await store.ReadAsync(document.StorageKey, ct)
            ?? throw new StepException("storage.missing", "The generated document is missing from storage.", retryable: true);
        if (DocumentKeys.Sha256Hex(content) != document.Sha256)
        {
            throw new StepException("storage.integrity", "The stored document doesn't match its recorded SHA-256.", retryable: true);
        }

        var archiveKey = DocumentKeys.Archive(document.Id, document.CreatedAt, DocumentNames.ExtensionFor(document.ContentType));
        await store.WriteAsync(archiveKey, content, ct);
        var written = await store.ReadAsync(archiveKey, ct);
        if (written is null || DocumentKeys.Sha256Hex(written) != document.Sha256)
        {
            throw new StepException("storage.integrity", "The archived copy doesn't match the document's SHA-256.", retryable: true);
        }

        var now = clock.GetUtcNow();
        var retention = node.Config.GetProperty("retention").GetString()!;
        var retentionUntil = RetentionPeriod.Until(retention, now);
        var folderPath = DocumentNames.FolderPath(folder.Text);
        var searchText = string.Join('\n', metadata.Values.Prepend(document.FileName));
        // The template's category at the moment of archiving; a document without a template (HTML email) is uncategorised.
        var categoryId = document.TemplateVersionId is { } versionId
            ? await (from v in db.TemplateVersions.AsNoTracking()
                     join t in db.Templates.AsNoTracking() on v.TemplateId equals t.Id
                     where v.Id == versionId
                     select t.CategoryId).FirstOrDefaultAsync(ct)
            : null;
        var record = ArchiveRecord.Create(document.Id, retention, retentionUntil, folderPath, JsonSerializer.Serialize(metadata), searchText, now, categoryId, matchKey);
        var stagingKey = document.StorageKey;
        document.MarkArchived(archiveKey, now);
        db.ArchiveRecords.Add(record);

        // Replace (ADR 0014): older records of this workflow with the same match values are superseded, never deleted; the file stays, the audit trail says who replaced what.
        var supersededIds = new List<Guid>();
        if (matchKey is not null)
        {
            var older = await db.ArchiveRecords.Where(a => a.MatchKey == matchKey && a.SupersededAt == null && a.DocumentId != document.Id).ToListAsync(ct);
            foreach (var old in older)
            {
                old.Supersede(document.Id, now);
                supersededIds.Add(old.DocumentId);
                db.AuditEntries.Add(AuditEntry.Record(
                    DocumentService.SupersededAction, DocumentService.EntityType, old.DocumentId.ToString(), ActorId, ActorName, now,
                    JsonSerializer.Serialize(new { supersededBy = document.Id, executionId = run.Execution.Id }), run.Execution.CorrelationId));
            }
        }

        return new Outcome(null, new { documentId = document.Id, folderPath, retentionUntil, replacedDocumentIds = supersededIds, warnings },
            AfterSave: () => DeleteStagingAsync(stagingKey));
    }

    /// <summary>Re-establishes what a step that already succeeded produced (the parsed data or the document, for later steps).</summary>
    private async Task RestoreAsync(Run run, Workflows.WorkflowNode node, ExecutionStep step, CancellationToken ct)
    {
        if (NodeCatalog.ChangesOutsideWorld(node.Type))
        {
            // A Write to database or Send SMS step that succeeded is only replayed (its recorded port is followed): it changed nothing in the run's data, and
            // running it again would repeat the write or send the message twice. Its record stays as it was; nothing is called on resume
            // (WriteDatabaseExecutionTests, SendSmsExecutionTests).
            return;
        }

        if (NodeCatalog.ProducesDocument(node.Type))
        {
            run.Document = await FindDocumentAsync(run.Execution.Id, node.Id, run.Iteration, ct);
        }
        else if (node.Type == NodeCatalog.MapFields && step.OutputPort == "success")
        {
            // Mapping is deterministic, like parsing: the data is rebuilt rather than stored again.
            run.Data = FieldMapper.Apply(node.Config, run.Data).Data
                ?? throw new StepException("map.restore", $"Step '{node.Title}' succeeded before but its mapping can't be applied again.", retryable: false);
        }
        else if (node.Type == NodeCatalog.TransformData && step.OutputPort == "success")
        {
            // Transforming is deterministic, like mapping: the data is rebuilt rather than stored again.
            run.Data = DataTransformer.Apply(node.Config, run.Data).Data
                ?? throw new StepException("transform.restore", $"Step '{node.Title}' succeeded before but its rules can't be applied again.", retryable: false);
        }
        else if (node.Type == NodeCatalog.MergeData && step.OutputPort == "success")
        {
            // Merging is deterministic, like transforming: the data is rebuilt rather than stored again.
            run.Data = DataMerger.Apply(node.Config, run.Data).Data
                ?? throw new StepException("merge.restore", $"Step '{node.Title}' succeeded before but its merge can't be applied again.", retryable: false);
        }
        else if (node.Type == NodeCatalog.ParseContent && step.OutputPort == "success")
        {
            // Parsing is deterministic, so the data is rebuilt rather than stored a second time.
            run.Data = RecordParser.Parse(node.Config, run.Data).Data
                ?? throw new StepException("parse.restore", $"Step '{node.Title}' succeeded before but its content can't be parsed again.", retryable: false);
        }
        else if (node.Type == NodeCatalog.QueryDatabase && step.OutputPort == "found")
        {
            // Rows are never stored (they may hold customer data, and step results are history every reader sees), so a resumed run (after an Approval,
            // a retry or a Worker restart) reads them again. A query is NOT deterministic: the database may have changed meanwhile, so the data the later
            // steps see can differ from what the first pass saw (ADR 0019). If the row is gone or the query fails now, the run can't continue as it was.
            // 'not-found' and 'failed' left the data unchanged: nothing to rebuild.
            var (bound, issues) = QueryParameters(node, run.Data);
            if (issues.Count > 0)
            {
                throw new StepException("query.restore", $"Step '{node.Title}' found a row before, but its parameters can't be filled again.", retryable: false);
            }

            DatabaseQueryResult read;
            try
            {
                read = await RunQueryAsync(node, QuerySetting(node, "connection"), bound, ct);
            }
            catch (StepException ex)
            {
                throw new StepException("query.restore", $"Step '{node.Title}' found a row before, but reading it again failed: {ex.Message}", ex.Retryable);
            }

            if (read.Rows.Count == 0)
            {
                throw new StepException("query.restore", $"Step '{node.Title}' found a row before, but reading it again found none: the database changed while the run waited.", retryable: false);
            }

            run.Data = WithQueryResult(node, run.Data, read);
        }
        else if (node.Type == NodeCatalog.FetchApi && step.OutputPort == "found")
        {
            // The answer is never stored (it is third-party data that may hold customer data, and step results are history every reader sees), so a resumed
            // run (after an Approval, a retry or a Worker restart) fetches it again with the same path and query. An API is NOT deterministic: its answer may
            // have changed meanwhile, so the later steps can see other data than the first pass did. If the answer is gone or the call fails now, the run can't
            // continue as it was: fetch.restore, retryable as the failure was (an operator retry resumes here). 'not-found' and 'failed' left the data unchanged.
            var (request, issues) = FetchRequest(node, run.Data);
            if (request is null || issues.Count > 0)
            {
                throw new StepException("fetch.restore", $"Step '{node.Title}' found an answer before, but its path or query values can't be filled again.", retryable: false);
            }

            ApiFetchResult fetched;
            try
            {
                fetched = await RunFetchAsync(request, ct);
            }
            catch (StepException ex)
            {
                throw new StepException("fetch.restore", $"Step '{node.Title}' found an answer before, but fetching it again failed: {ex.Message}", ex.Retryable);
            }

            if (!HasAnswer(fetched))
            {
                throw new StepException("fetch.restore", $"Step '{node.Title}' found an answer before, but fetching it again found none: the API changed while the run waited.", retryable: false);
            }

            try
            {
                run.Data = WithFetchResult(node, run.Data, fetched);
            }
            catch (StepException ex)
            {
                throw new StepException("fetch.restore", $"Step '{node.Title}' found an answer before, but the new answer can't be added to the data: {ex.Message}", retryable: false);
            }
        }

    }

    /// <summary>A step that succeeded before can't be re-established on resume: the run fails (an operator retry resumes here). Only the code is logged.</summary>
    private async Task FailRestoreAsync(Run run, Workflows.WorkflowNode node, ExecutionStep step, StepException ex)
    {
        var execution = run.Execution;
        var now = clock.GetUtcNow();
        LogStepFailed(logger, node.Id, execution.Id, step.Attempt, ex.Code);
        execution.Fail(ex.Code, ex.Message, now);
        Event(execution, ExecutionEventTypes.Failed, now, node.Id, Data(step, ("code", ex.Code), ("retryable", ex.Retryable)));
        Completed(execution, now);
        await SaveAsync(execution, CancellationToken.None);
    }

    private async Task FinishAsync(Run run, Workflows.WorkflowNode last, StepResult finished, CancellationToken ct)
    {
        var execution = run.Execution;
        var step = finished.Step;
        var now = clock.GetUtcNow();
        switch (last.Type, finished.Port)
        {
            case (NodeCatalog.Validate, "invalid"):
                using (var result = JsonDocument.Parse(step.Result ?? "{}"))
                {
                    var count = result.RootElement.TryGetProperty("issues", out var issues) ? issues.GetArrayLength() : 0;
                    execution.Reject("data.invalid", $"The data failed {count} validation rule(s) in step '{last.Title}'.", now);
                }

                Event(execution, ExecutionEventTypes.Rejected, now, last.Id, new { code = execution.FailureCode });
                break;
            case (NodeCatalog.ParseContent, "failed"):
                execution.Reject("data.unparseable", $"The content couldn't be parsed in step '{last.Title}' (see its issues).", now);
                Event(execution, ExecutionEventTypes.Rejected, now, last.Id, new { code = execution.FailureCode });
                break;
            case (NodeCatalog.MapFields, "failed"):
                execution.Reject("data.unmappable", $"The data couldn't be mapped in step '{last.Title}' (see its issues).", now);
                Event(execution, ExecutionEventTypes.Rejected, now, last.Id, new { code = execution.FailureCode });
                break;
            case (NodeCatalog.TransformData, "failed"):
                execution.Reject("data.untransformable", $"The data couldn't be transformed in step '{last.Title}' (see its issues).", now);
                Event(execution, ExecutionEventTypes.Rejected, now, last.Id, new { code = execution.FailureCode });
                break;
            case (NodeCatalog.MergeData, "failed"):
                execution.Reject("data.unmergeable", $"The data couldn't be merged in step '{last.Title}' (see its issues).", now);
                Event(execution, ExecutionEventTypes.Rejected, now, last.Id, new { code = execution.FailureCode });
                break;
            case (NodeCatalog.ApprovalStep, Approval.Rejected):
                execution.Reject("approval.declined", $"The approval in step '{last.Title}' was declined.", now);
                Event(execution, ExecutionEventTypes.Rejected, now, last.Id, new { code = execution.FailureCode });
                break;
            case (NodeCatalog.GeneratePdf or NodeCatalog.GenerateDocx or NodeCatalog.GenerateEmail, "failed"):
                execution.Reject("document.data_incomplete", $"The data can't produce a complete document in step '{last.Title}' (see its issues).", now);
                Event(execution, ExecutionEventTypes.Rejected, now, last.Id, new { code = execution.FailureCode });
                break;
            case (NodeCatalog.SendEmail, "failed"):
                execution.Reject("email.data_incomplete", $"The data can't produce a complete email in step '{last.Title}' (see its issues).", now);
                Event(execution, ExecutionEventTypes.Rejected, now, last.Id, new { code = execution.FailureCode });
                break;
            case (NodeCatalog.QueryDatabase, "failed"):
                execution.Reject("data.query_invalid", $"The data can't make a valid query in step '{last.Title}' (see its issues).", now);
                Event(execution, ExecutionEventTypes.Rejected, now, last.Id, new { code = execution.FailureCode });
                break;
            case (NodeCatalog.WriteDatabase, "failed"):
                execution.Reject("data.write_invalid", $"The data can't make a valid statement in step '{last.Title}' (see its issues).", now);
                Event(execution, ExecutionEventTypes.Rejected, now, last.Id, new { code = execution.FailureCode });
                break;
            case (NodeCatalog.SendSms, "failed"):
                execution.Reject("data.sms_invalid", $"The data can't make a valid message in step '{last.Title}' (see its issues).", now);
                Event(execution, ExecutionEventTypes.Rejected, now, last.Id, new { code = execution.FailureCode });
                break;
            case (NodeCatalog.FetchApi, "failed"):
                execution.Reject("data.fetch_invalid", $"The data can't make a valid request in step '{last.Title}' (see its issues).", now);
                Event(execution, ExecutionEventTypes.Rejected, now, last.Id, new { code = execution.FailureCode });
                break;
            case (NodeCatalog.CombinePdfs, "failed"):
                execution.Reject("combine.not_enough_documents", $"Step '{last.Title}' found fewer than two PDFs to combine (see its issues).", now);
                Event(execution, ExecutionEventTypes.Rejected, now, last.Id, new { code = execution.FailureCode });
                break;
            default:
                execution.Succeed(now);
                Event(execution, ExecutionEventTypes.Succeeded, now);
                break;
        }

        Completed(execution, now);
        await SaveAsync(execution, ct);
    }

    private Task<Document?> FindDocumentAsync(Guid executionId, string nodeId, int iteration, CancellationToken ct) =>
        db.Documents.SingleOrDefaultAsync(d => d.ExecutionId == executionId && d.NodeId == nodeId && d.Iteration == iteration, ct);

    private static object DocumentResult(Document d, IReadOnlyList<string> warnings, object? renderWarnings = null) =>
        new { documentId = d.Id, d.FileName, d.SizeBytes, d.Sha256, warnings, renderWarnings };

    /// <summary>
    /// Code, sanitized message and retryability of a step failure. Messages never contain payload values. Internal (not private) so tests can reach the run's
    /// time-limit branch, which no engine test can trigger (the 5-minute limit has no seam).
    /// </summary>
    internal static (string Code, string Message, bool Retryable) Describe(Exception ex, string nodeType, CancellationToken timeoutToken)
    {
        if (ex is StepException step)
        {
            return (step.Code, step.Message, step.Retryable);
        }

        if (NodeCatalog.ChangesOutsideWorld(nodeType))
        {
            // A step that changes the outside world is never retried automatically: a cancellation that is not the Worker stopping (the run's time limit) or any
            // other surprise may have come after the statement reached the database or the message reached the SMS provider.
            var (unknown, surpriseCode, during) = nodeType == NodeCatalog.SendSms
                ? (SmsSendMessages.OutcomeUnknown, "sms.unreachable", "the message was being sent")
                : (DatabaseQueryMessages.OutcomeUnknown, "db.query_failed", "the statement was running");
            return ex is OperationCanceledException && timeoutToken.IsCancellationRequested
                ? ("execution.timeout", $"The run exceeded its time limit of {RunTimeout.TotalMinutes:0} minutes while {during}. {unknown}", false)
                : (surpriseCode, unknown, false);
        }

        if (ex is OperationCanceledException && timeoutToken.IsCancellationRequested)
        {
            return ("execution.timeout", $"The run exceeded its time limit of {RunTimeout.TotalMinutes:0} minutes.", true);
        }

        var message = $"{ex.GetType().Name}: {ex.Message}";
        return ("step.technical_error", message.Length > ExecutionStep.MaxErrorLength ? message[..ExecutionStep.MaxErrorLength] : message, true);
    }

    private void Event(Execution execution, string type, DateTimeOffset now, string? nodeId = null, object? data = null) =>
        db.ExecutionEvents.Add(ExecutionEvent.Record(execution.Id, type, now, nodeId, data is null ? null : JsonSerializer.Serialize(data, Json)));

    private void Completed(Execution execution, DateTimeOffset now)
    {
        LogRunFinished(logger, execution.Id, execution.Status, execution.FailureCode);
        db.AuditEntries.Add(AuditEntry.Record(
            ExecutionService.Actions.Completed, ExecutionService.EntityType, execution.Id.ToString(), ActorId, ActorName, now,
            JsonSerializer.Serialize(new { status = execution.Status.ToString(), failureCode = execution.FailureCode, attempt = execution.Attempt }),
            execution.CorrelationId));
    }

    /// <summary>False when another run took over this execution (its lock had expired); this run then stops.</summary>
    private async Task<bool> SaveAsync(Execution execution, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            LogLostLock(logger, execution.Id);
            return false;
        }
    }

    private void ScheduleRetry(Guid executionId, TimeSpan delay)
    {
        try
        {
            jobs.Schedule(executionId, delay);
        }
#pragma warning disable CA1031 // the dispatcher re-queues the execution once the retry is overdue
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogScheduleFailed(logger, executionId, ex);
        }
    }

    private async Task DeleteStagingAsync(string key)
    {
        try
        {
            await store.DeleteAsync(key, CancellationToken.None);
        }
#pragma warning disable CA1031 // a leftover staging copy is harmless; the archived copy is the record
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogStagingCleanupFailed(logger, key, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Execution {ExecutionId} running (attempt {Attempt})")]
    private static partial void LogRunStarted(ILogger logger, Guid executionId, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Execution {ExecutionId} finished: {Status} {FailureCode}")]
    private static partial void LogRunFinished(ILogger logger, Guid executionId, ExecutionStatus status, string? failureCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Step {NodeId} of execution {ExecutionId} failed (attempt {Attempt}): {Code}")]
    private static partial void LogStepFailed(ILogger logger, string nodeId, Guid executionId, int attempt, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Execution {ExecutionId} was taken over by another run; this run stops")]
    private static partial void LogLostLock(ILogger logger, Guid executionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduling the retry of execution {ExecutionId} failed; the dispatcher will queue it")]
    private static partial void LogScheduleFailed(ILogger logger, Guid executionId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting staging copy {StorageKey} failed")]
    private static partial void LogStagingCleanupFailed(ILogger logger, string storageKey, Exception exception);
}

/// <summary>
/// Finds executions whose job went missing and queues them again (the "sweeper" of the transactional outbox).
/// Candidates: Queued or Running, not locked by a live run, and last dispatched more than <see cref="Grace"/> ago
/// (scheduled retries count as dispatched at their due time). A duplicate job is harmless: the lock lets one run.
/// </summary>
public sealed class ExecutionDispatcher(IVersoDbContext db, IExecutionJobs jobs, TimeProvider clock)
{
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);
    public const int BatchSize = 100;

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var cutoff = now - Grace;
        var stalled = await db.Executions
            .Where(e => (e.Status == ExecutionStatus.Queued || e.Status == ExecutionStatus.Running)
                        && e.DispatchedAt < cutoff
                        && (e.LockedUntil == null || e.LockedUntil < now))
            .OrderBy(e => e.DispatchedAt)
            .Take(BatchSize)
            .ToListAsync(ct);
        if (stalled.Count == 0)
        {
            return 0;
        }

        foreach (var execution in stalled)
        {
            execution.MarkDispatched(now);
            db.ExecutionEvents.Add(ExecutionEvent.Record(execution.Id, ExecutionEventTypes.Redispatched, now));
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return 0; // a run claimed one of them meanwhile; the next sweep looks again
        }

        foreach (var execution in stalled)
        {
            jobs.Enqueue(execution.Id);
        }

        return stalled.Count;
    }
}
