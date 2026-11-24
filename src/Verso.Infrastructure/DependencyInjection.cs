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

        // Encrypts the passwords Verso stores (ADR 0015). The master key comes from the environment or a secret store, never from the database.
        services.AddSingleton<Application.Mail.ISecretProtector>(new Security.AesGcmSecretProtector(configuration["Secrets:MasterKey"]));

        services.AddSingleton(Options.Create(UploadOptionsFrom(configuration)));

        return services;
    }

    /// <summary>Worker only: the print queues (<c>Print:Queues:{key}:Path</c>) of the Print queue step. The API never writes to them.</summary>
    public static IServiceCollection AddVersoPrintSpooler(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new Print.PrintOptions();
        foreach (var child in configuration.GetSection(Print.PrintOptions.Section).GetSection(nameof(Print.PrintOptions.Queues)).GetChildren())
        {
            options.Queues[child.Key] = new Print.PrintQueueOptions
            {
                Name = child[nameof(Print.PrintQueueOptions.Name)] ?? "",
                Path = child[nameof(Print.PrintQueueOptions.Path)] ?? "",
            };
        }

        services.AddSingleton(Options.Create(options));
        services.AddSingleton<IPrintSpooler, Print.FolderPrintSpooler>();
        return services;
    }

    /// <summary>Worker only: the SMTP relay of the Send email step (<c>Email</c> section). The API never sends mail.</summary>
    public static IServiceCollection AddVersoEmail(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(Email.EmailOptions.Section);
        var options = ReadEmailRelay(section);
        foreach (var child in section.GetSection(nameof(Email.EmailOptions.Relays)).GetChildren())
        {
            options.Relays[child.Key] = ReadEmailRelay(child);
        }

        services.AddSingleton(Options.Create(options));
        services.AddSingleton<Application.Mail.IEmailRelayStore, Email.EmailRelayStore>();
        services.AddSingleton<IEmailSender, Email.SmtpEmailSender>();
        return services;
    }

    /// <summary>One relay's settings: the top-level <c>Email</c> section is the relay <c>default</c>, each <c>Email:Relays:{key}</c> another (ADR 0006 addendum 1).</summary>
    /// <summary>Worker only: connects to administrator-created SQL Server connections for "Test connection" (ADR 0016). Optional <c>DatabaseConnections:AllowedServers</c> limits the hosts.</summary>
    public static IServiceCollection AddVersoDatabaseConnections(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new Application.Connections.DatabaseConnectionOptions
        {
            AllowedServers = [.. configuration.GetSection(Application.Connections.DatabaseConnectionOptions.Section).GetSection(nameof(Application.Connections.DatabaseConnectionOptions.AllowedServers)).GetChildren().Select(c => c.Value ?? "").Where(v => v.Length > 0)],
        };
        services.AddSingleton(options);
        services.AddSingleton<Application.Connections.IProviderProbe, Connections.SqlServerProbe>(); // SQL Server; the other providers come from Verso.Databases
        services.AddSingleton<Application.Connections.IDatabaseProbe, Connections.DatabaseProbeRouter>();
        services.AddSingleton<Application.Connections.IProviderQueryRunner, Connections.SqlServerQueryRunner>(); // Query database step; the other providers come from Verso.Databases
        services.AddScoped<Application.Connections.IDatabaseQuery, Application.Connections.DatabaseQueryService>(); // scoped: it reads the database through the scoped context
        services.AddSingleton<Application.Connections.IProviderWriteRunner, Connections.SqlServerWriteRunner>(); // Write to database step; the other providers come from Verso.Databases
        services.AddScoped<Application.Connections.DatabaseConnectionResolver>(); // scoped: the scoped context
        services.AddScoped<Application.Connections.IDatabaseWrite, Application.Connections.DatabaseWriteService>();
        return services;
    }

    /// <summary>Worker only: calls the HTTP APIs an administrator creates in Configuration: "Test connection" (ADR 0017) and the GET of the Fetch from API step. Optional <c>ApiConnections:AllowedHosts</c> limits the hosts.</summary>
    public static IServiceCollection AddVersoApiConnections(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new Application.Connections.ApiConnectionOptions
        {
            AllowedHosts = [.. configuration.GetSection(Application.Connections.ApiConnectionOptions.Section).GetSection(nameof(Application.Connections.ApiConnectionOptions.AllowedHosts)).GetChildren().Select(c => c.Value ?? "").Where(v => v.Length > 0)],
        });
        services.AddSingleton<Application.Connections.IApiProbe>(new Connections.HttpApiProbe());
        // The Fetch from API step (one GET per run step); the service is scoped because it reads the connection through the scoped db context.
        services.AddSingleton<Application.Connections.IApiFetchSender>(new Connections.HttpApiFetchSender());
        services.AddScoped<Application.Connections.IApiFetch, Application.Connections.ApiFetchService>();
        return services;
    }

    /// <summary>Worker only: sends the one real SMS of "Test connection" and of the Send SMS step through the SMS connections an administrator creates in Configuration (ADR 0018). Optional <c>SmsConnections:AllowedHosts</c> limits the hosts.</summary>
    public static IServiceCollection AddVersoSmsConnections(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new Application.Connections.SmsConnectionOptions
        {
            AllowedHosts = [.. configuration.GetSection(Application.Connections.SmsConnectionOptions.Section).GetSection(nameof(Application.Connections.SmsConnectionOptions.AllowedHosts)).GetChildren().Select(c => c.Value ?? "").Where(v => v.Length > 0)],
        });
        services.AddSingleton<Application.Connections.ISmsProbe>(new Connections.BrevoSmsProbe());
        // The Send SMS step (one SMS per run step, never retried); the service is scoped because it reads the connection through the scoped db context.
        services.AddSingleton<Application.Connections.ISmsSender>(new Connections.HttpSmsSender());
        services.AddScoped<Application.Connections.ISmsSend, Application.Connections.SmsSendService>();
        return services;
    }

    private static Email.EmailOptions ReadEmailRelay(IConfiguration section)
    {
        var defaults = new Email.EmailOptions();
        return new Email.EmailOptions
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

    /// <summary>File upload settings (ADR 0023): the ceiling from <c>Limits:MaxUploadBytes</c>, the retention from <c>Uploads:Retention</c>; defaults otherwise.</summary>
    private static Application.Uploads.UploadOptions UploadOptionsFrom(IConfiguration configuration)
    {
        var options = new Application.Uploads.UploadOptions();
        if (long.TryParse(configuration["Limits:MaxUploadBytes"], CultureInfo.InvariantCulture, out var ceiling) && ceiling > 0)
        {
            options.CeilingBytes = ceiling;
        }

        if (TimeSpan.TryParse(configuration[$"{Application.Uploads.UploadOptions.Section}:{nameof(Application.Uploads.UploadOptions.Retention)}"], CultureInfo.InvariantCulture, out var retention)
            && retention > TimeSpan.Zero)
        {
            options.Retention = retention;
        }

        return options;
    }

    public static IHealthChecksBuilder AddVersoDatabaseCheck(this IHealthChecksBuilder builder) =>
        builder.AddCheck<DatabaseHealthCheck>("database", HealthStatus.Unhealthy, tags: ["ready"], timeout: TimeSpan.FromSeconds(5));
}
