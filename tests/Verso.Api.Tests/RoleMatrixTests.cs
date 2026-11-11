using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Verso.Api.Tests;

/// <summary>
/// Plan §7 / M9: the endpoint × role matrix, checked by calling every endpoint as every development user.
/// The expectations are written out here, independently of the policy table in the API, so a wrong policy can't
/// agree with itself. Calls use unknown ids and empty bodies: an allowed call answers 400/404/409 (never 401/403)
/// and changes nothing; a denied one is 403 (signed in) or 401 (anonymous).
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class RoleMatrixTests(MigratedDatabaseFixture fixture)
{
    // V viewer · O operator · T template author · W workflow author · A auditor · X admin · N signed in, no Verso role
    private const string Anyone = "anonymous";
    private const string SignedIn = "VOTWAXN";
    private const string AnyRole = "VOTWAX";

    private static readonly Dictionary<char, string> Users = new()
    {
        ['V'] = "dev.viewer", ['O'] = "dev.operator", ['T'] = "dev.template-author", ['W'] = "dev.workflow-author",
        ['A'] = "dev.auditor", ['X'] = "dev.admin", ['N'] = "dev.no-access",
    };

    private static readonly string Id = Guid.NewGuid().ToString();

    /// <summary>(method, route pattern as registered, a concrete URL, who may call it).</summary>
    public static readonly TheoryData<string, string, string, string> Matrix = new()
    {
        { "GET", "/api/health", "/api/health", Anyone },
        { "GET", "/api/auth/config", "/api/auth/config", Anyone },
        { "GET", "/api/v1/me", "/api/v1/me", SignedIn },

        { "GET", "/api/v1/templates/", "/api/v1/templates", AnyRole },
        { "POST", "/api/v1/templates/", "/api/v1/templates", "TX" },
        { "GET", "/api/v1/templates/{id:guid}", $"/api/v1/templates/{Id}", AnyRole },
        { "GET", "/api/v1/templates/{id:guid}/versions", $"/api/v1/templates/{Id}/versions", AnyRole },
        { "GET", "/api/v1/templates/{id:guid}/versions/{versionId:guid}", $"/api/v1/templates/{Id}/versions/{Id}", AnyRole },
        { "GET", "/api/v1/templates/{id:guid}/draft", $"/api/v1/templates/{Id}/draft", AnyRole },
        { "POST", "/api/v1/templates/{id:guid}/draft", $"/api/v1/templates/{Id}/draft", "TX" },
        { "PUT", "/api/v1/templates/{id:guid}/draft", $"/api/v1/templates/{Id}/draft", "TX" },
        { "POST", "/api/v1/templates/{id:guid}/draft/publish", $"/api/v1/templates/{Id}/draft/publish", "TX" },
        { "POST", "/api/v1/templates/{id:guid}/draft/preview/html", $"/api/v1/templates/{Id}/draft/preview/html", "TX" },
        { "POST", "/api/v1/templates/{id:guid}/draft/preview/pdf", $"/api/v1/templates/{Id}/draft/preview/pdf", "TX" },
        { "GET", "/api/v1/previews/{previewId:guid}", $"/api/v1/previews/{Id}", "TX" },
        { "GET", "/api/v1/previews/{previewId:guid}/content", $"/api/v1/previews/{Id}/content", "TX" },

        { "GET", "/api/v1/node-types", "/api/v1/node-types", AnyRole },
        { "GET", "/api/v1/workflows/", "/api/v1/workflows", AnyRole },
        { "POST", "/api/v1/workflows/", "/api/v1/workflows", "WX" },
        { "GET", "/api/v1/workflows/{id:guid}", $"/api/v1/workflows/{Id}", AnyRole },
        { "GET", "/api/v1/workflows/{id:guid}/versions", $"/api/v1/workflows/{Id}/versions", AnyRole },
        { "GET", "/api/v1/workflows/{id:guid}/versions/{versionId:guid}", $"/api/v1/workflows/{Id}/versions/{Id}", AnyRole },
        { "GET", "/api/v1/workflows/{id:guid}/draft", $"/api/v1/workflows/{Id}/draft", AnyRole },
        { "POST", "/api/v1/workflows/{id:guid}/draft", $"/api/v1/workflows/{Id}/draft", "WX" },
        { "PUT", "/api/v1/workflows/{id:guid}/draft", $"/api/v1/workflows/{Id}/draft", "WX" },
        { "POST", "/api/v1/workflows/{id:guid}/draft/validate", $"/api/v1/workflows/{Id}/draft/validate", "WX" },
        { "POST", "/api/v1/workflows/{id:guid}/draft/publish", $"/api/v1/workflows/{Id}/draft/publish", "WX" },

        { "POST", "/api/v1/workflows/{key}/executions", "/api/v1/workflows/no-such-workflow/executions", "OX" },
        { "GET", "/api/v1/executions/", "/api/v1/executions", AnyRole },
        { "GET", "/api/v1/executions/{id:guid}", $"/api/v1/executions/{Id}", AnyRole },
        { "GET", "/api/v1/executions/{id:guid}/events", $"/api/v1/executions/{Id}/events", AnyRole },
        { "GET", "/api/v1/executions/{id:guid}/input", $"/api/v1/executions/{Id}/input", "AX" },
        { "POST", "/api/v1/executions/{id:guid}/retry", $"/api/v1/executions/{Id}/retry", "OX" },

        { "GET", "/api/v1/documents/", "/api/v1/documents", AnyRole },
        { "GET", "/api/v1/documents/{id:guid}", $"/api/v1/documents/{Id}", AnyRole },
        { "GET", "/api/v1/documents/{id:guid}/content", $"/api/v1/documents/{Id}/content", AnyRole },

        { "GET", "/api/v1/audit/", "/api/v1/audit", "AX" },
        { "GET", "/api/v1/audit/actions", "/api/v1/audit/actions", "AX" },

        { "GET", "/api/v1/folder-locations", "/api/v1/folder-locations", AnyRole },
        { "POST", "/api/v1/folder-access-tests", "/api/v1/folder-access-tests", "WX" },
        { "GET", "/api/v1/folder-access-tests/{id:guid}", $"/api/v1/folder-access-tests/{Id}", "WX" },
        { "GET", "/api/v1/workflows/{id:guid}/folder-input", $"/api/v1/workflows/{Id}/folder-input", AnyRole },
        { "GET", "/api/v1/admin/folder-locations/", "/api/v1/admin/folder-locations", "X" },
        { "PUT", "/api/v1/admin/folder-locations/{key}", "/api/v1/admin/folder-locations/some-key", "X" },
        { "DELETE", "/api/v1/admin/folder-locations/{key}", "/api/v1/admin/folder-locations/some-key", "X" },
        { "POST", "/hubs/executions/negotiate", "/hubs/executions/negotiate?negotiateVersion=1", AnyRole },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task EveryUser_GetsExactlyTheAccessTheMatrixGrants(string method, string route, string url, string allowed)
    {
        _ = route;
        var failures = new List<string>();
        foreach (var (letter, user) in Users.Select(u => (u.Key, (string?)u.Value)).Append(('-', null)))
        {
            using var client = fixture.Factory.CreateClientAs(user);
            using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(url, UriKind.Relative));
            if (method is "POST" or "PUT")
            {
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            }

            using var response = await client.SendAsync(request);
            var status = (int)response.StatusCode;
            var expected = allowed == Anyone || (user is not null && allowed.Contains(letter, StringComparison.Ordinal))
                ? "allowed"
                : user is null ? "401" : "403";
            var actual = status is 401 or 403 ? status.ToString(System.Globalization.CultureInfo.InvariantCulture) : "allowed";
            if (actual != expected)
            {
                failures.Add($"{user ?? "anonymous"}: expected {expected}, got {status}");
            }
        }

        Assert.True(failures.Count == 0, $"{method} {url}:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void EveryEndpoint_IsInTheMatrix_AndNamesAPolicyOrIsExplicitlyAnonymous()
    {
        var known = Matrix.Select(row => $"{row[0]} {row[1]}").ToHashSet(StringComparer.Ordinal); // xUnit 2: rows are object[]
        var problems = new List<string>();
        foreach (var endpoint in fixture.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var route = endpoint.RoutePattern.RawText ?? "";
            if (route.StartsWith("/openapi", StringComparison.Ordinal) || route == "/hubs/executions")
            {
                // OpenAPI: anonymous, Development/Testing only (checked in ContractTests). Hub connect: same policy as negotiate (below).
                Assert.True(endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null || HasPolicy(endpoint), $"{route} has no explicit access rule.");
                continue;
            }

            // SignalR's negotiate endpoint declares no HTTP method: match it by route.
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                ?? [.. known.Where(k => k.EndsWith(" " + route, StringComparison.Ordinal)).Select(k => k.Split(' ')[0]).DefaultIfEmpty("*")];
            foreach (var method in methods)
            {
                if (!known.Contains($"{method} {route}"))
                {
                    problems.Add($"{method} {route} is not in the role matrix (add a row to RoleMatrixTests.Matrix and docs/api.md).");
                }
            }

            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null && !HasPolicy(endpoint))
            {
                problems.Add($"{route} names no policy (it would fall back to 'any Verso role'); name one explicitly.");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static bool HasPolicy(Endpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => !string.IsNullOrEmpty(a.Policy));
}
