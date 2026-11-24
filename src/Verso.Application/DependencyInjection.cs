using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Verso.Application.Audit;
using Verso.Application.Documents;
using Verso.Application.Executions;
using Verso.Application.Ingestion;
using Verso.Application.Templates;
using Verso.Application.Workflows;

namespace Verso.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddVersoApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<TemplateService>();
        services.AddScoped<DocumentCategoryService>();
        services.AddScoped<Mail.EmailRelayService>();
        services.AddScoped<Mail.EmailRelayTests>();
        services.AddScoped<Connections.DatabaseConnectionService>();
        services.AddScoped<Connections.DatabaseConnectionTests>();
        services.AddScoped<Connections.ApiConnectionService>();
        services.AddScoped<Connections.ApiConnectionTests>();
        services.AddScoped<Connections.SmsConnectionService>();
        services.AddScoped<Connections.SmsConnectionTests>();
        services.AddScoped<TemplatePreview>();
        services.AddScoped<PdfPreviews>();
        services.AddScoped<WorkflowService>();
        services.AddScoped<Configuration.ConfigurationService>();
        services.AddScoped<ExecutionService>();
        services.AddScoped<Uploads.UploadService>();
        services.AddScoped<ExecutionQueries>();
        services.AddScoped<DocumentService>();
        services.AddScoped<AuditQueries>();
        services.AddScoped<FolderInputQueries>();
        services.AddScoped<FolderAccessTests>();
        return services;
    }

    /// <summary>Use cases that run in the Worker (they need the PDF printer or the folders, which only the Worker has).</summary>
    public static IServiceCollection AddVersoWorkerUseCases(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<PdfPreviewRunner>();
        services.AddScoped<PdfPreviewCleanup>();
        services.AddScoped<ExecutionEngine>();
        services.AddScoped<ExecutionDispatcher>();

        // Folder input: the Worker submits runs itself, as its own identity.
        services.TryAddScoped<ICurrentUser, WorkerUser>();
        services.TryAddScoped<ExecutionService>();
        services.TryAddSingleton(new FolderIngestionOptions());
        services.AddScoped<FolderIngestion>();
        services.AddScoped<FolderAccessTestRunner>();
        services.AddScoped<Uploads.UploadRetention>();
        services.AddScoped<Mail.EmailRelayTestRunner>();
        services.AddScoped<Connections.DatabaseConnectionTestRunner>();
        services.AddScoped<Connections.ApiConnectionTestRunner>();
        services.AddScoped<Connections.SmsConnectionTestRunner>();
        return services;
    }
}
