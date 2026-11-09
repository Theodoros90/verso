using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Verso.Application.Audit;
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
        services.AddScoped<AuditQueries>();
        return services;
    }

    /// <summary>Use cases that run in the Worker (they need the PDF printer, which only the Worker has).</summary>
    public static IServiceCollection AddVersoWorkerUseCases(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<PdfPreviewRunner>();
        services.AddScoped<PdfPreviewCleanup>();
        return services;
    }
}
