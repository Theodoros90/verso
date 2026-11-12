using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    IVersoDbContext db, IPdfPrinter printer, IDocxWriter docx, IDocumentStore store, IExecutionJobs jobs, TimeProvider clock, ILogger<ExecutionEngine> logger)
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
                if (!await RunLoopAsync(run, node, timeout.Token, ct))
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
    /// </summary>
    private async Task<bool> RunLoopAsync(Run run, Workflows.WorkflowNode forEach, CancellationToken timeoutToken, CancellationToken shutdown)
    {
        var items = ForEach.Items(forEach.Config, run.Data);
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
            await RestoreAsync(run, node, step, shutdown);
            return new StepResult(step.OutputPort, step);
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
        or (NodeCatalog.TransformData, "failed") or (NodeCatalog.MergeData, "failed") or (NodeCatalog.GeneratePdf or NodeCatalog.GenerateDocx or NodeCatalog.GenerateEmail, "failed")
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
            if (step.Status == StepStatus.Running)
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
            var (code, message, retryable) = Describe(ex, timeoutToken);
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
        NodeCatalog.ApiTrigger or NodeCatalog.ErpConnector or NodeCatalog.FileUpload or NodeCatalog.SftpFolder or NodeCatalog.ScheduledTrigger
            or NodeCatalog.ManualTrigger or NodeCatalog.FolderInput => Task.FromResult(new Outcome("out", null)),
        NodeCatalog.ParseContent => Task.FromResult(Parse(run, node)),
        NodeCatalog.MapFields => Task.FromResult(Map(run, node)),
        NodeCatalog.TransformData => Task.FromResult(Transform(run, node)),
        NodeCatalog.MergeData => Task.FromResult(Merge(run, node)),
        NodeCatalog.IfElse => Task.FromResult(Branch(run, node)),
        NodeCatalog.SwitchRoute => Task.FromResult(Route(run, node)),
        NodeCatalog.ForEach => Task.FromResult(Repeat(run, node)),
        NodeCatalog.Validate => Task.FromResult(Validate(run, node)),
        NodeCatalog.GeneratePdf or NodeCatalog.GenerateDocx => GenerateDocumentAsync(run, node, ct),
        NodeCatalog.GenerateEmail => GenerateEmailAsync(run, node, ct),
        NodeCatalog.Archive => ArchiveAsync(run, node, ct),
        _ => throw new StepException("node.unsupported", $"Node type '{node.Type}' can't run in this version of Verso.", retryable: false),
    };

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
    /// unchanged, so a resumed run has nothing to rebuild. The result holds the count, never the items.
    /// </summary>
    private static Outcome Repeat(Run run, Workflows.WorkflowNode node)
    {
        var count = ForEach.Items(node.Config, run.Data).Count;
        if (count > ForEach.MaxItems)
        {
            throw new StepException("foreach.too_many_items", $"The list has {count} items; at most {ForEach.MaxItems} are allowed in a For each.", retryable: false);
        }

        return new Outcome(count == 0 ? ForEach.Empty : ForEach.Each, new { count });
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
        foreach (var pair in node.Config.GetProperty("metadata").EnumerateArray())
        {
            var key = pair.GetProperty("key").GetString()!;
            var value = PatternText.Resolve(pair.GetProperty("value").GetString()!, run.Data);
            warnings.AddRange(value.Missing.Select(p => $"metadata {key}: no value for '{p}'"));
            metadata[key] = value.Text.Length > 300 ? value.Text[..300] : value.Text;
        }

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
        DateTimeOffset? retentionUntil = retention switch
        {
            "7y" => now.AddYears(7),
            "10y" => now.AddYears(10),
            _ => null,
        };
        var folderPath = DocumentNames.FolderPath(folder.Text);
        var searchText = string.Join('\n', metadata.Values.Prepend(document.FileName));
        var record = ArchiveRecord.Create(document.Id, retention, retentionUntil, folderPath, JsonSerializer.Serialize(metadata), searchText, now);
        var stagingKey = document.StorageKey;
        document.MarkArchived(archiveKey, now);
        db.ArchiveRecords.Add(record);

        return new Outcome(null, new { documentId = document.Id, folderPath, retentionUntil, warnings },
            AfterSave: () => DeleteStagingAsync(stagingKey));
    }

    /// <summary>Re-establishes what a step that already succeeded produced (the parsed data or the document, for later steps).</summary>
    private async Task RestoreAsync(Run run, Workflows.WorkflowNode node, ExecutionStep step, CancellationToken ct)
    {
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

    /// <summary>Code, sanitized message and retryability of a step failure. Messages never contain payload values.</summary>
    private static (string Code, string Message, bool Retryable) Describe(Exception ex, CancellationToken timeoutToken)
    {
        if (ex is StepException step)
        {
            return (step.Code, step.Message, step.Retryable);
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
