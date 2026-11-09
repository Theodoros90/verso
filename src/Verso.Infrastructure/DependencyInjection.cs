using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Verso.Application;
using Verso.Infrastructure.Persistence;

namespace Verso.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Verso";

    /// <summary>SQL Server 2022 (approved target). Keeps EF from generating SQL newer servers only understand.</summary>
    private const int SqlServerCompatibilityLevel = 160;

    public static IServiceCollection AddVersoPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is not configured. " +
                "Set it in appsettings.Development.json, user secrets, or the environment variable ConnectionStrings__Verso. See docs/setup.md.");
        }

        services.AddDbContext<VersoDbContext>(options => options.UseSqlServer(
            connectionString,
            sql => sql.UseCompatibilityLevel(SqlServerCompatibilityLevel)));
        services.AddScoped<IVersoDbContext>(sp => sp.GetRequiredService<VersoDbContext>());

        return services;
    }

    public static IHealthChecksBuilder AddVersoDatabaseCheck(this IHealthChecksBuilder builder) =>
        builder.AddCheck<DatabaseHealthCheck>("database", HealthStatus.Unhealthy, tags: ["ready"], timeout: TimeSpan.FromSeconds(5));
}
