using Microsoft.AspNetCore.Mvc;
using Verso.Api.Auth;
using Verso.Api.Common;
using Verso.Application.Executions;
using Verso.Application.Templates;

namespace Verso.Api.Executions;

/// <summary>Executions API (plan §5.3): submit, read, retry. Contract documented in docs/api.md.</summary>
public static class ExecutionEndpoints
{
    /// <summary>Request body cap for submissions; the data itself is limited to 1,000,000 characters.</summary>
    public const long MaxSubmitBodyBytes = 1_100_000;

    /// <summary>Rate-limit policy for submissions, per signed-in user (<c>Limits:SubmissionsPerMinute</c>).</summary>
    public const string SubmissionRateLimit = "submissions";

    public static IEndpointRouteBuilder MapExecutionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/workflows/{key}/executions", async (
                    string key, SubmitExecutionRequest? request,
                    [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                    [FromHeader(Name = CorrelationId.Header)] string? correlationId,
                    ExecutionService service, HttpContext http, CancellationToken ct) =>
            {
                // Without a caller-supplied id, the run carries the request's own correlation id (same one as in the logs).
                var result = await service.SubmitAsync(key, idempotencyKey, correlationId ?? CorrelationId.Get(http), request, ct);
                if (!result.IsSuccess)
                {
                    return result.Error!.ToProblem();
                }

                var submission = result.Value!;
                var location = $"/api/v1/executions/{submission.Execution.ExecutionId}";
                return submission.Created
                    ? Results.Accepted(location, submission.Execution)
                    : Results.Ok(submission.Execution);
            })
            .WithTags("Executions")
            .WithName("SubmitExecution")
            .RequireAuthorization(Policies.Operate)
            .RequireRateLimiting(SubmissionRateLimit)
            .WithMetadata(new RequestSizeLimitAttribute(MaxSubmitBodyBytes))
            .Produces<SubmissionDto>(StatusCodes.Status202Accepted)
            .Produces<SubmissionDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        var group = app.MapGroup("/api/v1/executions").WithTags("Executions").RequireAuthorization(Policies.Read);

        group.MapGet("/", async (
                    ExecutionQueries queries, CancellationToken ct,
                    string? workflow = null, string? status = null, DateTimeOffset? from = null, DateTimeOffset? to = null,
                    string? correlationId = null, int page = 1, int pageSize = 25) =>
                (await queries.ListAsync(new ExecutionFilter(workflow, status, from, to, correlationId), page, pageSize, ct)).ToResult())
            .WithName("ListExecutions")
            .Produces<PageResult<ExecutionSummaryDto>>()
            .ProducesValidationProblem();

        group.MapGet("/{id:guid}/events", async (Guid id, ExecutionQueries queries, CancellationToken ct, long after = 0, int limit = ExecutionQueries.MaxEventsPerRead) =>
                (await queries.EventsAsync(id, after, limit, ct)).ToResult())
            .WithName("ListExecutionEvents")
            .Produces<IReadOnlyList<ExecutionEventDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/input", async (Guid id, ExecutionService service, HttpContext http, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return (await service.InputAsync(id, ct)).ToResult();
            })
            .WithName("GetExecutionInput")
            .RequireAuthorization(Policies.ReadPayload)
            .Produces<ExecutionInputDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}", async (Guid id, ExecutionService service, CancellationToken ct) =>
                (await service.GetAsync(id, ct)).ToResult())
            .WithName("GetExecution")
            .Produces<ExecutionDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/retry", async (Guid id, ExecutionService service, CancellationToken ct) =>
            {
                var result = await service.RetryAsync(id, ct);
                return result.IsSuccess
                    ? Results.Accepted($"/api/v1/executions/{id}", result.Value)
                    : result.Error!.ToProblem();
            })
            .WithName("RetryExecution")
            .RequireAuthorization(Policies.Operate)
            .Produces<ExecutionDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // Decide a waiting Approval step. Open to every reader at the policy level: the step's approver role is checked in the service.
        group.MapPost("/{id:guid}/approvals/{nodeId}", async (Guid id, string nodeId, DecideApprovalRequest? request, ExecutionService service, CancellationToken ct) =>
            {
                var result = await service.DecideAsync(id, nodeId, request, ct);
                return result.IsSuccess
                    ? Results.Accepted($"/api/v1/executions/{id}", result.Value)
                    : result.Error!.ToProblem();
            })
            .WithName("DecideApproval")
            .Produces<ExecutionDto>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }
}
