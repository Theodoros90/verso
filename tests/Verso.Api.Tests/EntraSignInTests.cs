using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Verso.Api.Auth;

namespace Verso.Api.Tests;

/// <summary>
/// Entra ID mode without a tenant: the test issues its own tokens (RSA key, Entra-shaped claims) and points the
/// API's JWT handler at that key instead of Entra's metadata. Covers what our code does with a token: audience,
/// expiry, role / group mapping to Verso roles, the hub's query-string token, and that dev tokens mean nothing here.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class EntraSignInTests : IDisposable
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string ApiClientId = "22222222-2222-2222-2222-222222222222";
    private const string OperatorsGroup = "33333333-3333-3333-3333-333333333333";
    private static readonly string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly WebApplicationFactory<Program> _host;

    public EntraSignInTests(MigratedDatabaseFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var key = new RsaSecurityKey(_rsa) { KeyId = "test-key" };
        _host = fixture.Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:Mode", "Entra");
            b.UseSetting("Auth:Entra:TenantId", Tenant);
            b.UseSetting("Auth:Entra:ApiClientId", ApiClientId);
            b.UseSetting($"Auth:RoleMap:{OperatorsGroup}", "Operator");
            b.ConfigureTestServices(s => s.PostConfigure<JwtBearerOptions>(AuthSetup.EntraScheme, o =>
            {
                // Same validation as production, but the signing key comes from here instead of Entra's metadata.
                var config = new OpenIdConnectConfiguration { Issuer = Issuer };
                config.SigningKeys.Add(key);
                o.Configuration = config;
                o.ConfigurationManager = new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<OpenIdConnectConfiguration>(config);
                o.TokenValidationParameters.ValidIssuer = Issuer;
            }));
        });
    }

    public void Dispose()
    {
        _host.Dispose();
        _rsa.Dispose();
    }

    [Fact]
    public async Task AppRole_MapsToTheVersoRole_AndIdentityComesFromTheToken()
    {
        using var client = Client(Token(roles: ["Verso.Auditor"]));

        var me = await ReadAsync(await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative)));
        using var audit = await client.GetAsync(new Uri("/api/v1/audit?pageSize=1", UriKind.Relative));
        using var create = await client.PostAsync(new Uri("/api/v1/templates", UriKind.Relative), JsonBody("{}"));

        Assert.Equal("44444444-0000-0000-0000-000000000001", me.GetProperty("id").GetString());
        Assert.Equal("Ada Synthetic", me.GetProperty("name").GetString());
        Assert.Equal(["Auditor"], me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.True(me.GetProperty("permissions").GetProperty("readAudit").GetBoolean());
        Assert.False(me.GetProperty("permissions").GetProperty("templateAuthoring").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [Fact]
    public async Task SecurityGroup_MapsThroughTheConfiguredRoleMap()
    {
        using var client = Client(Token(groups: [OperatorsGroup, "55555555-5555-5555-5555-555555555555"]));

        var me = await ReadAsync(await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative)));
        using var submit = await client.PostAsync(new Uri("/api/v1/workflows/no-such-workflow/executions", UriKind.Relative), JsonBody("{}"));

        Assert.Equal(["Operator"], me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.NotEqual(HttpStatusCode.Forbidden, submit.StatusCode); // allowed (it fails later: no Idempotency-Key)
    }

    [Fact]
    public async Task UnknownRolesGiveNoAccess_ButTheUserIsStillRecognised()
    {
        using var client = Client(Token(roles: ["Contoso.Reader", "Admin.Of.Something.Else"]));

        using var me = await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative));
        using var templates = await client.GetAsync(new Uri("/api/v1/templates", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Empty((await ReadAsync(me)).GetProperty("roles").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, templates.StatusCode);
    }

    [Theory]
    [InlineData("wrong-audience")]
    [InlineData("expired")]
    [InlineData("other-key")]
    [InlineData("development-token")]
    [InlineData("none")]
    public async Task InvalidTokens_AreRejected(string problem)
    {
        var token = problem switch
        {
            "wrong-audience" => Token(roles: ["Verso.Admin"], audience: "api://some-other-app"),
            "expired" => Token(roles: ["Verso.Admin"], expires: DateTime.UtcNow.AddMinutes(-10)),
            "other-key" => Token(roles: ["Verso.Admin"], signingKey: new RsaSecurityKey(RSA.Create(2048)) { KeyId = "attacker" }),
            "development-token" => DevUsers.Token("dev.admin"),
            _ => null,
        };
        using var client = Client(token);

        using var response = await client.GetAsync(new Uri("/api/v1/templates", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task QueryStringTokens_AreAcceptedOnTheHubOnly()
    {
        using var client = Client(null);
        var token = Uri.EscapeDataString(Token(roles: ["Verso.Viewer"]));

        using var hub = await client.PostAsync(new Uri($"/hubs/executions/negotiate?negotiateVersion=1&access_token={token}", UriKind.Relative), null);
        using var api = await client.GetAsync(new Uri($"/api/v1/templates?access_token={token}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, hub.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode); // tokens in URLs end up in logs: only where browsers need it
    }

    // ----- Helpers -----

    private HttpClient Client(string? token)
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization = token is null ? null : new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private string Token(string[]? roles = null, string[]? groups = null, string? audience = null, DateTime? expires = null, SecurityKey? signingKey = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["oid"] = "44444444-0000-0000-0000-000000000001",
            ["name"] = "Ada Synthetic",
            ["tid"] = Tenant,
        };
        if (roles is not null)
        {
            claims["roles"] = roles;
        }

        if (groups is not null)
        {
            claims["groups"] = groups;
        }

        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience ?? ApiClientId,
            Claims = claims,
            NotBefore = (expires ?? now.AddHours(1)) < now ? now.AddHours(-1) : now.AddMinutes(-1),
            IssuedAt = (expires ?? now.AddHours(1)) < now ? now.AddHours(-1) : now.AddMinutes(-1),
            Expires = expires ?? now.AddHours(1),
            SigningCredentials = new SigningCredentials(signingKey ?? new RsaSecurityKey(_rsa) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256),
        });
    }

    private static StringContent JsonBody(string json) => new(json, System.Text.Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        }
    }
}
