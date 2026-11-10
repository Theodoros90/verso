using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Verso.Api.Realtime;
using Verso.Application;

namespace Verso.Api.Auth;

/// <summary>
/// <c>Auth</c> configuration section. <see cref="Mode"/> is <c>Entra</c> (default, required outside Development and
/// Testing) or <c>Development</c> (synthetic users, refused anywhere else).
/// </summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";
    public const string EntraMode = "Entra";
    public const string DevelopmentMode = "Development";

    public string Mode { get; set; } = EntraMode;
    public EntraOptions Entra { get; set; } = new();

    /// <summary>
    /// Entra app-role values or security-group object ids → Verso role. Values named <c>Verso.&lt;Role&gt;</c> (e.g.
    /// <c>Verso.Operator</c>) map to that role without an entry. Prefer app roles assigned to groups (no group overage).
    /// </summary>
    public Dictionary<string, string> RoleMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class EntraOptions
{
    public string Instance { get; set; } = "https://login.microsoftonline.com/";
    public string? TenantId { get; set; }

    /// <summary>Application (client) id of the Verso API registration; tokens must be issued for it.</summary>
    public string? ApiClientId { get; set; }

    /// <summary>Application (client) id of the SPA registration (public client, auth code + PKCE).</summary>
    public string? SpaClientId { get; set; }

    /// <summary>The scope the SPA requests, e.g. <c>api://&lt;api client id&gt;/access_as_user</c>.</summary>
    public string? Scope { get; set; }
}

/// <summary>Authorization policies; every endpoint names one (or is explicitly anonymous). The matrix is in docs/api.md.</summary>
public static class Policies
{
    /// <summary>Signed in, with or without a Verso role (only <c>/me</c>, so the SPA can explain "no access").</summary>
    public const string SignedIn = "SignedIn";

    /// <summary>Any Verso role: read templates, workflows, executions, documents; subscribe to notifications.</summary>
    public const string Read = "Read";

    public const string TemplateAuthoring = "TemplateAuthoring";
    public const string WorkflowAuthoring = "WorkflowAuthoring";

    /// <summary>Submit and retry executions.</summary>
    public const string Operate = "Operate";

    /// <summary>Submitted payloads (may hold personal data).</summary>
    public const string ReadPayload = "ReadPayload";

    /// <summary>The audit trail.</summary>
    public const string ReadAudit = "ReadAudit";

    public static readonly IReadOnlyDictionary<string, string[]> RolesByPolicy = new Dictionary<string, string[]>
    {
        [Read] = [Roles.Viewer, Roles.Operator, Roles.TemplateAuthor, Roles.WorkflowAuthor, Roles.Auditor, Roles.Admin],
        [TemplateAuthoring] = [Roles.TemplateAuthor, Roles.Admin],
        [WorkflowAuthoring] = [Roles.WorkflowAuthor, Roles.Admin],
        [Operate] = [Roles.Operator, Roles.Admin],
        [ReadPayload] = [Roles.Auditor, Roles.Admin],
        [ReadAudit] = [Roles.Auditor, Roles.Admin],
    };
}

/// <summary>A synthetic person for Development sign-in. Fictional names; one role each, plus one without access.</summary>
public sealed record DevUser(string Id, string Name, IReadOnlyList<string> Roles);

public static class DevUsers
{
    public static IReadOnlyList<DevUser> All { get; } =
    [
        new("dev.viewer", "Vera Viewer", [Roles.Viewer]),
        new("dev.operator", "Omar Operator", [Roles.Operator]),
        new("dev.template-author", "Tara Templates", [Roles.TemplateAuthor]),
        new("dev.workflow-author", "Wes Workflows", [Roles.WorkflowAuthor]),
        new("dev.auditor", "Ada Auditor", [Roles.Auditor]),
        new("dev.admin", "Alex Admin", [Roles.Admin]),
        new("dev.no-access", "Nico No-Access", []),
    ];

    public static DevUser? Find(string id) => All.FirstOrDefault(u => u.Id == id);

    /// <summary>The bearer token the Development scheme accepts for a user.</summary>
    public static string Token(string id) => DevelopmentAuthHandler.TokenPrefix + id;
}

/// <summary>
/// Development sign-in: <c>Authorization: Bearer dev:&lt;user id&gt;</c> (or <c>?access_token=</c> on the SignalR hub,
/// which browsers can't send headers to) signs in as one of <see cref="DevUsers"/>. Registered only when
/// <c>Auth:Mode</c> is Development, which startup refuses outside the Development and Testing environments.
/// </summary>
public sealed class DevelopmentAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Development";
    public const string TokenPrefix = "dev:";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = AuthSetup.ReadBearerToken(Request);
        if (token is null)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!token.StartsWith(TokenPrefix, StringComparison.Ordinal) || DevUsers.Find(token[TokenPrefix.Length..]) is not { } user)
        {
            return Task.FromResult(AuthenticateResult.Fail("Unknown development user."));
        }

        Claim[] claims =
        [
            new(AuthSetup.IdClaim, user.Id),
            new(AuthSetup.NameClaim, user.Name),
            .. user.Roles.Select(r => new Claim(AuthSetup.VersoRoleClaim, r)),
        ];
        var identity = new ClaimsIdentity(claims, SchemeName, AuthSetup.NameClaim, AuthSetup.VersoRoleClaim);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>Turns Entra <c>roles</c> / <c>groups</c> claims into Verso roles through <see cref="AuthOptions.RoleMap"/>.</summary>
internal sealed class EntraRoleMapping(IOptions<AuthOptions> options) : IClaimsTransformation
{
    private static readonly HashSet<string> Known = new(Policies.RolesByPolicy[Policies.Read], StringComparer.OrdinalIgnoreCase);

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        // Registered only in Entra mode. The JWT handler labels identities "AuthenticationTypes.Federation", not by scheme.
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity
            || identity.HasClaim(c => c.Type == AuthSetup.VersoRoleClaim))
        {
            return Task.FromResult(principal); // not signed in, or already mapped (transformations can run more than once)
        }

        var map = options.Value.RoleMap;
        var roles = identity.FindAll("roles").Concat(identity.FindAll("groups")).Select(c => c.Value)
            .Select(v => map.TryGetValue(v, out var mapped) ? mapped
                : v.StartsWith("Verso.", StringComparison.OrdinalIgnoreCase) ? v["Verso.".Length..] : null)
            .Where(r => r is not null && Known.Contains(r))
            .Select(r => Known.First(k => string.Equals(k, r, StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .ToList(); // materialize: the loop below adds claims to the identity being read
        foreach (var role in roles)
        {
            identity.AddClaim(new Claim(AuthSetup.VersoRoleClaim, role));
        }

        return Task.FromResult(principal);
    }
}

/// <summary>The signed-in person, from the request's claims.</summary>
internal sealed class HttpCurrentUser(IHttpContextAccessor http) : ICurrentUser
{
    private ClaimsPrincipal User => http.HttpContext?.User ?? new ClaimsPrincipal();

    public string Id => User.FindFirst(AuthSetup.IdClaim)?.Value ?? User.FindFirst("sub")?.Value ?? "anonymous";

    public string Name => User.FindFirst(AuthSetup.NameClaim)?.Value ?? User.FindFirst("preferred_username")?.Value ?? Id;

    public IReadOnlyCollection<string> Roles => [.. User.FindAll(AuthSetup.VersoRoleClaim).Select(c => c.Value)];
}

public static class AuthSetup
{
    public const string EntraScheme = "Entra";

    /// <summary>Entra's immutable user id (object id); the development handler uses the same claim.</summary>
    public const string IdClaim = "oid";

    public const string NameClaim = "name";

    /// <summary>Verso roles after mapping (distinct from Entra's raw <c>roles</c> claim).</summary>
    public const string VersoRoleClaim = "verso_role";

    /// <summary>
    /// Validates the configuration and refuses to start when it's unsafe: Development sign-in outside the Development
    /// and Testing environments, or Entra mode without its tenant and API client id (fail closed).
    /// </summary>
    public static AuthOptions AddVersoAuth(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
        var local = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing");
        var services = builder.Services;
        services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();

        if (string.Equals(options.Mode, AuthOptions.DevelopmentMode, StringComparison.OrdinalIgnoreCase))
        {
            if (!local)
            {
                throw new InvalidOperationException(
                    $"Auth:Mode 'Development' (synthetic users) refuses to run in the '{builder.Environment.EnvironmentName}' environment. " +
                    "Configure Auth:Mode 'Entra' with Auth:Entra:TenantId and Auth:Entra:ApiClientId.");
            }

            services.AddAuthentication(DevelopmentAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthHandler>(DevelopmentAuthHandler.SchemeName, null);
        }
        else if (string.Equals(options.Mode, AuthOptions.EntraMode, StringComparison.OrdinalIgnoreCase))
        {
            var entra = options.Entra;
            if (string.IsNullOrWhiteSpace(entra.TenantId) || string.IsNullOrWhiteSpace(entra.ApiClientId))
            {
                throw new InvalidOperationException(
                    "Auth:Mode is 'Entra' but Auth:Entra:TenantId or Auth:Entra:ApiClientId is not configured. " +
                    "Set them (see docs/runbook.md), or use Auth:Mode 'Development' on a developer machine.");
            }

            services.AddTransient<IClaimsTransformation, EntraRoleMapping>();
            services.AddAuthentication(EntraScheme).AddJwtBearer(EntraScheme, jwt =>
            {
                jwt.Authority = $"{entra.Instance.TrimEnd('/')}/{entra.TenantId}/v2.0";
                jwt.MapInboundClaims = false; // keep Entra's claim names (oid, name, roles, groups)
                jwt.TokenValidationParameters.ValidAudiences = [entra.ApiClientId, $"api://{entra.ApiClientId}"];
                jwt.TokenValidationParameters.NameClaimType = NameClaim;
                jwt.TokenValidationParameters.RoleClaimType = VersoRoleClaim;
                jwt.Events = new JwtBearerEvents
                {
                    // Browsers can't set headers on WebSockets: SignalR sends the token as ?access_token= on the hub only.
                    OnMessageReceived = ctx =>
                    {
                        if (ReadBearerToken(ctx.Request) is { } token)
                        {
                            ctx.Token = token;
                        }

                        return Task.CompletedTask;
                    },
                };
            });
        }
        else
        {
            throw new InvalidOperationException($"Auth:Mode must be '{AuthOptions.EntraMode}' or '{AuthOptions.DevelopmentMode}', not '{options.Mode}'.");
        }

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.SignedIn, p => p.RequireAuthenticatedUser())
            .AddDefaultPolicy(Policies.Read, p => p.RequireAuthenticatedUser().RequireClaim(VersoRoleClaim, Policies.RolesByPolicy[Policies.Read]))
            .AddPolicy(Policies.TemplateAuthoring, p => p.RequireAuthenticatedUser().RequireClaim(VersoRoleClaim, Policies.RolesByPolicy[Policies.TemplateAuthoring]))
            .AddPolicy(Policies.WorkflowAuthoring, p => p.RequireAuthenticatedUser().RequireClaim(VersoRoleClaim, Policies.RolesByPolicy[Policies.WorkflowAuthoring]))
            .AddPolicy(Policies.Operate, p => p.RequireAuthenticatedUser().RequireClaim(VersoRoleClaim, Policies.RolesByPolicy[Policies.Operate]))
            .AddPolicy(Policies.ReadPayload, p => p.RequireAuthenticatedUser().RequireClaim(VersoRoleClaim, Policies.RolesByPolicy[Policies.ReadPayload]))
            .AddPolicy(Policies.ReadAudit, p => p.RequireAuthenticatedUser().RequireClaim(VersoRoleClaim, Policies.RolesByPolicy[Policies.ReadAudit]))
            // Secure by default: an endpoint that forgets to name a policy still needs a Verso role.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireClaim(VersoRoleClaim, Policies.RolesByPolicy[Policies.Read]).Build());

        return options;
    }

    /// <summary>The bearer token from the Authorization header, or from <c>?access_token=</c> on the SignalR hub only.</summary>
    internal static string? ReadBearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return header["Bearer ".Length..].Trim() is { Length: > 0 } t ? t : null;
        }

        return request.Path.StartsWithSegments(ExecutionsHub.Path) && request.Query["access_token"].ToString() is { Length: > 0 } q ? q : null;
    }
}
