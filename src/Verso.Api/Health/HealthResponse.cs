using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Verso.Api.Health;

/// <summary>Response body of <c>GET /api/health</c>. Documented in docs/api.md.</summary>
public sealed record HealthResponse(string Status, double TotalDurationMs, IReadOnlyList<HealthCheckEntry> Checks)
{
    public static HealthResponse From(HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new(
            report.Status.ToString(),
            Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            [.. report.Entries.Select(e => new HealthCheckEntry(
                e.Key,
                e.Value.Status.ToString(),
                e.Value.Description,
                Math.Round(e.Value.Duration.TotalMilliseconds, 1),
                e.Value.Data.Count == 0 ? null : e.Value.Data))]);
    }
}

/// <summary>One health check result. Exceptions are logged server-side and never returned.</summary>
public sealed record HealthCheckEntry(
    string Name,
    string Status,
    string? Description,
    double DurationMs,
    IReadOnlyDictionary<string, object>? Data);
