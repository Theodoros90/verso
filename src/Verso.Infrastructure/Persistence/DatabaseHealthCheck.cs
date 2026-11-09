using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Verso.Infrastructure.Persistence;

/// <summary>
/// Reports whether the database is reachable and whether all migrations are applied.
/// Unreachable = Unhealthy; reachable with pending migrations = Degraded.
/// Never puts connection details or exception messages in the result description.
/// </summary>
internal sealed class DatabaseHealthCheck(VersoDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await db.Database.CanConnectAsync(cancellationToken))
            {
                return new HealthCheckResult(context.Registration.FailureStatus, "Database is unreachable or does not exist.");
            }

            var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).Count();
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
            var data = new Dictionary<string, object>
            {
                ["appliedMigrations"] = applied,
                ["pendingMigrations"] = pending,
            };

            return pending == 0
                ? HealthCheckResult.Healthy("Connected; schema is up to date.", data)
                : HealthCheckResult.Degraded($"Connected; {pending} pending migration(s).", data: data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The exception is logged by the health check service; the description stays generic.
            return new HealthCheckResult(context.Registration.FailureStatus, "Database check failed.", ex);
        }
    }
}
