using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Verso.Application.Audit;
using Verso.Application.Templates;

namespace Verso.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddVersoApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<TemplateService>();
        services.AddScoped<TemplatePreview>();
        services.AddScoped<AuditQueries>();
        return services;
    }
}
