using Microsoft.AspNetCore.Mvc;
using Verso.Api.Common;
using Verso.Application.Executions;

namespace Verso.Api.Executions;

/// <summary>Executions API (plan §5.3): submit, read, retry. Contract documented in docs/api.md.</summary>
public static class ExecutionEndpoints
{
    /// <summary>Request body cap for submissions; the data itself is limited to 1,000,000 characters.</summary>
    public const long MaxSubmitBodyBytes = 1_100_000;

    public static IEndpointRouteBuilder MapExecutionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/workflows/{key}/executions", async (
                    string key, SubmitExecutionRequest? request,
                    [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                    [FromHeader(Name = "X-Correlation-Id")] string? correlationId,
                    ExecutionService service, CancellationToken ct) =>
            {
                var result = await service.SubmitAsync(key, idempotencyKey, correlationId, request, ct);
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
            .WithMetadata(new RequestSizeLimitAttribute(MaxSubmitBodyBytes))
            .Produces<SubmissionDto>(StatusCodes.Status202Accepted)
            .Produces<SubmissionDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        var group = app.MapGroup("/api/v1/executions").WithTags("Executions");

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
            .Produces<ExecutionDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }
}
