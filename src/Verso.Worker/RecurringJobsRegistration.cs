using Hangfire;
using Verso.Infrastructure.Jobs;

namespace Verso.Worker;

/// <summary>Registers recurring jobs (preview cleanup) at startup. Failure is logged, not fatal: jobs still run.</summary>
internal sealed partial class RecurringJobsRegistration(IRecurringJobManager recurring, ILogger<RecurringJobsRegistration> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            HangfireJobs.RegisterRecurringJobs(recurring);
        }
        catch (Exception ex)
        {
            LogRegistrationFailed(logger, ex);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not register recurring jobs (is the database migrated?)")]
    private static partial void LogRegistrationFailed(ILogger logger, Exception exception);
}
