using Microsoft.Extensions.Options;
using Verso.Application;

namespace Verso.Api.Auth;

/// <summary>What the SPA needs to sign in: the mode, Entra settings (public values only), or the synthetic users.</summary>
public sealed record AuthConfigDto(string Mode, EntraConfigDto? Entra, IReadOnlyList<DevUserDto>? DevUsers);

public sealed record EntraConfigDto(string Authority, string ClientId, string Scope);

public sealed record DevUserDto(string Id, string Name, IReadOnlyList<string> Roles, string Token);

/// <summary>The signed-in person and what they may do (derived from the same policy table the API enforces).</summary>
public sealed record MeDto(string Id, string Name, IReadOnlyList<string> Roles, PermissionsDto Permissions);

public sealed record PermissionsDto(bool Read, bool TemplateAuthoring, bool WorkflowAuthoring, bool Operate, bool ReadPayload, bool ReadAudit, bool Administer);

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/config", (IOptions<AuthOptions> options) =>
            {
                var o = options.Value;
                if (string.Equals(o.Mode, AuthOptions.DevelopmentMode, StringComparison.OrdinalIgnoreCase))
                {
                    // Only ever served in Development/Testing: startup refuses this mode anywhere else.
                    return Results.Ok(new AuthConfigDto(AuthOptions.DevelopmentMode, null,
                        [.. DevUsers.All.Select(u => new DevUserDto(u.Id, u.Name, u.Roles, DevUsers.Token(u.Id)))]));
                }

                var e = o.Entra;
                return Results.Ok(new AuthConfigDto(AuthOptions.EntraMode,
                    new EntraConfigDto($"{e.Instance.TrimEnd('/')}/{e.TenantId}", e.SpaClientId ?? "", e.Scope ?? $"api://{e.ApiClientId}/access_as_user"),
                    null));
            })
            .AllowAnonymous()
            .WithTags("Auth")
            .WithName("GetAuthConfig")
            .Produces<AuthConfigDto>();

        app.MapGet("/api/v1/me", (ICurrentUser user) =>
            {
                bool Has(string policy) => user.Roles.Intersect(Policies.RolesByPolicy[policy], StringComparer.OrdinalIgnoreCase).Any();
                return Results.Ok(new MeDto(user.Id, user.Name, [.. user.Roles], new PermissionsDto(
                    Has(Policies.Read), Has(Policies.TemplateAuthoring), Has(Policies.WorkflowAuthoring),
                    Has(Policies.Operate), Has(Policies.ReadPayload), Has(Policies.ReadAudit), Has(Policies.Administration))));
            })
            .RequireAuthorization(Policies.SignedIn)
            .WithTags("Auth")
            .WithName("GetMe")
            .Produces<MeDto>();

        return app;
    }
}
