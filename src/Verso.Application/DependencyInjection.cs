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
        services.AddScoped<TemplatePreview>();
        services.AddScoped<PdfPreviews>();
        services.AddScoped<WorkflowService>();
        services.AddScoped<ExecutionService>();
        services.AddScoped<ExecutionQueries>();
        services.AddScoped<DocumentService>();
        services.AddScoped<AuditQueries>();
        services.AddScoped<FolderInputQueries>();
        services.AddScoped<FolderAccessTests>();
        services.AddScoped<FolderLocationAdmin>();
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
        return services;
    }
}
