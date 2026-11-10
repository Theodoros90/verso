using Verso.Application.Executions;
using Verso.Application.Workflows;
using Verso.Domain;
using Verso.Domain.Executions;

namespace Verso.UnitTests.Executions;

public sealed class ExecutionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(6);

    [Fact]
    public void Submit_CreatesPendingStepsInOrder_AndIsQueued()
    {
        var e = NewExecution();

        Assert.Equal(ExecutionStatus.Queued, e.Status);
        Assert.Equal(1, e.Attempt);
        Assert.Equal(T0, e.DispatchedAt);
        Assert.Equal(["trigger", "validate", "pdf", "archive"], e.Steps.Select(s => s.NodeId));
        Assert.Equal([1, 2, 3, 4], e.Steps.Select(s => s.Sequence));
        Assert.All(e.Steps, s => Assert.Equal(StepStatus.Pending, s.Status));
    }

    [Fact]
    public void Claim_IsExclusiveWhileTheLockIsLive_AndPossibleOnceItExpires()
    {
        var e = NewExecution();

        Assert.True(e.TryClaim(T0, Lease));
        Assert.Equal(ExecutionStatus.Running, e.Status);
        Assert.Equal(T0, e.StartedAt);
        Assert.False(e.TryClaim(T0.AddMinutes(1), Lease)); // a duplicate delivery
        Assert.True(e.TryClaim(T0.AddMinutes(7), Lease));  // the first run died
        Assert.Equal(T0, e.StartedAt);
    }

    [Fact]
    public void ReleasedLock_CanBeClaimedAtOnce()
    {
        var e = NewExecution();
        e.TryClaim(T0, Lease);
        e.ReleaseLock();

        Assert.True(e.TryClaim(T0.AddSeconds(1), Lease));
    }

    [Fact]
    public void SucceedAndReject_SkipStepsThatDidNotRun_ButFailKeepsThemPending()
    {
        var succeeded = NewExecution();
        succeeded.TryClaim(T0, Lease);
        succeeded.Succeed(T0.AddSeconds(1));
        Assert.All(succeeded.Steps, s => Assert.Equal(StepStatus.Skipped, s.Status));
        Assert.Null(succeeded.LockedUntil);

        var failed = NewExecution();
        failed.TryClaim(T0, Lease);
        failed.Fail("step.technical_error", "boom", T0.AddSeconds(1));
        Assert.All(failed.Steps, s => Assert.Equal(StepStatus.Pending, s.Status));
        Assert.False(failed.TryClaim(T0.AddSeconds(2), Lease));
    }

    [Fact]
    public void Retry_OnlyForFailed_ResumesWithAFreshAttemptBudget()
    {
        var e = NewExecution();
        e.TryClaim(T0, Lease);
        var pdf = e.Step("pdf");
        pdf.Start(T0);
        pdf.Fail("IOException: disk full", T0.AddSeconds(1));
        e.Fail("step.technical_error", "Step failed", T0.AddSeconds(1));

        e.Retry(T0.AddMinutes(5));

        Assert.Equal(ExecutionStatus.Queued, e.Status);
        Assert.Equal(2, e.Attempt);
        Assert.Null(e.FailureCode);
        Assert.Null(e.CompletedAt);
        Assert.Equal(StepStatus.Pending, pdf.Status);
        Assert.Equal(0, pdf.Attempt);
        Assert.Equal(T0.AddMinutes(5), e.DispatchedAt);
    }

    [Theory]
    [InlineData(ExecutionStatus.Rejected, "Submit corrected data")]
    [InlineData(ExecutionStatus.Succeeded, "Only failed executions")]
    public void Retry_RefusesRejectedAndSucceeded(ExecutionStatus status, string message)
    {
        var e = NewExecution();
        e.TryClaim(T0, Lease);
        if (status == ExecutionStatus.Rejected)
        {
            e.Reject("data.invalid", "bad data", T0);
        }
        else
        {
            e.Succeed(T0);
        }

        var ex = Assert.Throws<DomainRuleException>(() => e.Retry(T0));
        Assert.Equal("execution.not_retryable", ex.Code);
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Step_Interrupt_DoesNotCountTheAttempt()
    {
        var e = NewExecution();
        var step = e.Step("pdf");
        step.Start(T0);
        step.Interrupt();

        Assert.Equal(StepStatus.Pending, step.Status);
        Assert.Equal(0, step.Attempt);
    }

    [Fact]
    public void Plan_OrdersTopologically_AndFollowsPorts()
    {
        // Definition order deliberately scrambled; the invalid port leads to a second archive step.
        var (definition, _) = WorkflowDefinitionParser.Parse("""
            { "nodes": [
                { "id": "archive", "type": "archive", "title": "Archive", "position": { "x": 0, "y": 0 }, "config": {} },
                { "id": "pdf", "type": "generate-pdf", "title": "PDF", "position": { "x": 0, "y": 0 }, "config": {} },
                { "id": "trigger", "type": "api-trigger", "title": "Trigger", "position": { "x": 0, "y": 0 }, "config": {} },
                { "id": "validate", "type": "validate", "title": "Validate", "position": { "x": 0, "y": 0 }, "config": {} } ],
              "edges": [
                { "id": "e3", "source": "pdf", "sourcePort": "success", "target": "archive", "targetPort": "in" },
                { "id": "e1", "source": "trigger", "sourcePort": "out", "target": "validate", "targetPort": "in" },
                { "id": "e2", "source": "validate", "sourcePort": "valid", "target": "pdf", "targetPort": "in" } ] }
            """);

        var plan = ExecutionPlan.Create(definition!);

        Assert.Equal(["trigger", "validate", "pdf", "archive"], plan.Ordered.Select(n => n.Id));
        Assert.Equal("trigger", plan.Trigger.Id);
        Assert.Equal("pdf", plan.Next("validate", "valid")!.Id);
        Assert.Null(plan.Next("validate", "invalid"));
        Assert.Null(plan.Next("archive", "out"));
    }

    private static Execution NewExecution() => Execution.Submit(
        Guid.NewGuid(), "invoice-standard", Guid.NewGuid(), 1, "{}", "key-1", "corr-1", "{}", new string('0', 64),
        [new("trigger", "api-trigger", "API trigger"), new("validate", "validate", "Validate"), new("pdf", "generate-pdf", "PDF"), new("archive", "archive", "Archive")],
        "dev.user", T0);
}
