using Microsoft.Extensions.DependencyInjection;
using Verso.Application.Connections;

namespace Verso.Databases;

public static class DatabaseDrivers
{
    /// <summary>Worker only: registers the PostgreSQL, MySQL / MariaDB and Oracle probes and query runners beside the SQL Server one (ADR 0016 addendum 1).</summary>
    public static IServiceCollection AddVersoDatabaseDrivers(this IServiceCollection services)
    {
        services.AddSingleton<IProviderProbe, PostgreSqlProbe>();
        services.AddSingleton<IProviderProbe, MySqlProbe>();
        services.AddSingleton<IProviderProbe, OracleProbe>();
        services.AddSingleton<IProviderQueryRunner, PostgreSqlQueryRunner>();
        services.AddSingleton<IProviderQueryRunner, MySqlQueryRunner>();
        services.AddSingleton<IProviderQueryRunner, OracleQueryRunner>();
        return services;
    }
}
