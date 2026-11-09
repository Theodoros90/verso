using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Verso.Api.Health;

public static class HealthEndpoints
{
    /// <summary>
    /// <c>GET /api/health</c>: 200 when Healthy or Degraded, 503 when Unhealthy.
    /// Anonymous by design (load balancers, SPA status indicator), so it returns no connection details.
    /// </summary>
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", async (HealthCheckService health, CancellationToken cancellationToken) =>
            {
                var report = await health.CheckHealthAsync(cancellationToken);
                var body = HealthResponse.From(report);
                return report.Status == HealthStatus.Unhealthy
                    ? Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.Ok(body);
            })
            .WithName("GetHealth")
            .WithTags("Health")
            .Produces<HealthResponse>(StatusCodes.Status200OK)
            .Produces<HealthResponse>(StatusCodes.Status503ServiceUnavailable);

        return app;
    }
}
