using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Verso.Application;
using Verso.Application.Executions;
using Verso.Application.Ingestion;
using Verso.Infrastructure.Ingestion;
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

    /// <summary>Worker only: the SMTP relay of the Send email step (<c>Email</c> section). The API never sends mail.</summary>
    public static IServiceCollection AddVersoEmail(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(Email.EmailOptions.Section);
        var defaults = new Email.EmailOptions();
        var options = new Email.EmailOptions
        {
            Host = section[nameof(Email.EmailOptions.Host)] ?? "",
            Port = int.TryParse(section[nameof(Email.EmailOptions.Port)], CultureInfo.InvariantCulture, out var port) ? port : defaults.Port,
            EnableSsl = !bool.TryParse(section[nameof(Email.EmailOptions.EnableSsl)], out var ssl) || ssl,
            UserName = section[nameof(Email.EmailOptions.UserName)] ?? "",
            Password = section[nameof(Email.EmailOptions.Password)] ?? "",
            From = section[nameof(Email.EmailOptions.From)] ?? "",
            FromName = section[nameof(Email.EmailOptions.FromName)] ?? defaults.FromName,
            AllowedRecipientDomains = [.. section.GetSection(nameof(Email.EmailOptions.AllowedRecipientDomains)).GetChildren().Select(c => c.Value ?? "").Where(d => d.Length > 0)],
            Timeout = TimeSpan.TryParse(section[nameof(Email.EmailOptions.Timeout)], CultureInfo.InvariantCulture, out var timeout) ? timeout : defaults.Timeout,
        };
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<IEmailSender, Email.SmtpEmailSender>();
        return services;
    }

    /// <summary>
    /// Worker only: approved folders (<c>FolderInput:Locations</c>) and folder-input settings (<c>FolderInput</c>).
    /// The API never touches folders; it only reads what the Worker reported.
    /// </summary>
    public static IServiceCollection AddVersoFolderAccess(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(FolderIngestionOptions.Section);
        var locations = new FolderAccessOptions();
        foreach (var child in section.GetSection(nameof(FolderAccessOptions.Locations)).GetChildren())
        {
            locations.Locations[child.Key] = new FolderLocationOptions
            {
                Name = child[nameof(FolderLocationOptions.Name)] ?? "",
                Path = child[nameof(FolderLocationOptions.Path)] ?? "",
                Input = bool.TryParse(child[nameof(FolderLocationOptions.Input)], out var input) && input,
                Output = bool.TryParse(child[nameof(FolderLocationOptions.Output)], out var output) && output,
            };
        }

        locations.AllowedRoots = [.. section.GetSection(nameof(FolderAccessOptions.AllowedRoots)).GetChildren().Select(c => c.Value ?? "")];

        var ingestion = new FolderIngestionOptions();
        if (TimeSpan.TryParse(section[nameof(FolderIngestionOptions.PollInterval)], CultureInfo.InvariantCulture, out var poll) && poll > TimeSpan.Zero)
        {
            ingestion.PollInterval = poll;
        }

        if (TimeSpan.TryParse(section[nameof(FolderIngestionOptions.SettleTime)], CultureInfo.InvariantCulture, out var settle) && settle >= TimeSpan.Zero)
        {
            ingestion.SettleTime = settle;
        }

        if (long.TryParse(section[nameof(FolderIngestionOptions.MaxFileBytes)], CultureInfo.InvariantCulture, out var max) && max > 0)
        {
            ingestion.MaxFileBytes = max;
        }

        services.AddSingleton(ingestion);
        services.AddSingleton<IFolderAccess>(new LocalFolderAccess(Options.Create(locations)));
        return services;
    }

    public static IHealthChecksBuilder AddVersoDatabaseCheck(this IHealthChecksBuilder builder) =>
        builder.AddCheck<DatabaseHealthCheck>("database", HealthStatus.Unhealthy, tags: ["ready"], timeout: TimeSpan.FromSeconds(5));
}
