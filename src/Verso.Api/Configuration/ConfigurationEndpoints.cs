using Verso.Api.Auth;
using Verso.Api.Common;
using Verso.Application.Configuration;

namespace Verso.Api.Configuration;

/// <summary>Configuration package API, <c>/api/v1/configuration</c>. Contract documented in docs/api.md, design in ADR 0010.</summary>
public static class ConfigurationEndpoints
{
    public static IEndpointRouteBuilder MapConfigurationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/configuration").WithTags("Configuration").RequireAuthorization(Policies.Read);

        group.MapPost("/export", async (ConfigurationExportRequest request, ConfigurationService service, CancellationToken ct) =>
                (await service.ExportAsync(request, ct)).ToResult())
            .WithName("ExportConfiguration")
            .Produces<ConfigurationPackage>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/import", async (ConfigurationImportRequest request, ConfigurationService service, CancellationToken ct, bool dryRun = false) =>
                (await service.ImportAsync(request, dryRun, ct)).ToResult())
            .WithName("ImportConfiguration")
            .RequireAuthorization(Policies.Administration)
            .Produces<ConfigurationImportResult>()
            .ProducesValidationProblem();

        return app;
    }
}
