using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Verso.Api.Tests;

public sealed class ContractTests
{
    /// <summary>
    /// The checked-in OpenAPI document (docs/openapi/v1.json) must match what the API generates, so contract
    /// changes are always visible in review. After an intended change, regenerate it:
    /// <c>$env:VERSO_UPDATE_OPENAPI = "1"; dotnet test --filter OpenApiDocument</c>, then review the diff.
    /// </summary>
    [Fact]
    public async Task OpenApiDocument_MatchesCheckedInCopy()
    {
        await using var factory = new VersoApiFactory(new TestDatabase("openapi").ConnectionString); // no DB access needed
        var generated = Normalize(await factory.CreateClient().GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative)));
        var path = Path.Combine(RepoRoot(), "docs", "openapi", "v1.json");

        if (Environment.GetEnvironmentVariable("VERSO_UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, generated);
        }

        Assert.True(File.Exists(path), $"Missing {path}. Generate it with VERSO_UPDATE_OPENAPI=1.");
        var checkedIn = Normalize(await File.ReadAllTextAsync(path));
        Assert.True(checkedIn == generated,
            "docs/openapi/v1.json is out of date with the API. Regenerate it (see this test's summary) and update docs/api.md.");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void DevelopmentSignIn_RefusesToStart_OutsideDevelopmentAndTesting(string environment)
    {
        using var factory = Host(environment, ("Auth:Mode", "Development"));

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("refuses to run", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EntraSignIn_WithoutTenantOrClientId_RefusesToStart()
    {
        using var factory = Host("Production"); // appsettings.json: Mode Entra, nothing configured

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("Auth:Entra:TenantId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntraSignIn_Configured_StartsInProduction_RequiresATokenEverywhere_AndHidesTheContract()
    {
        using var factory = Host("Production", ("Auth:Entra:TenantId", "00000000-0000-0000-0000-000000000001"), ("Auth:Entra:ApiClientId", "00000000-0000-0000-0000-000000000002"));
        using var client = factory.CreateClient();

        using var templates = await client.GetAsync(new Uri("/api/v1/templates", UriKind.Relative));
        using var devToken = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/templates", UriKind.Relative));
        devToken.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "dev:dev.admin");
        using var withDevToken = await client.SendAsync(devToken);
        using var openApi = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        using var config = await client.GetAsync(new Uri("/api/auth/config", UriKind.Relative));

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, templates.StatusCode);
        Assert.Contains("\"code\":\"unauthenticated\"", await templates.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, withDevToken.StatusCode); // development tokens mean nothing here
        // Not mapped outside Development/Testing; like any unknown route it needs sign-in first (fallback policy), so 401.
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, openApi.StatusCode);
        Assert.DoesNotContain("openapi", await openApi.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        var body = await config.Content.ReadAsStringAsync();
        Assert.Contains("\"mode\":\"Entra\"", body, StringComparison.Ordinal);
        Assert.Contains("\"devUsers\":null", body, StringComparison.Ordinal); // never lists synthetic users outside Development
    }

    private static WebApplicationFactory<Program> Host(string environment, params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            b.UseSetting("ConnectionStrings:Verso", new TestDatabase("guard").ConnectionString);
            foreach (var (key, value) in settings)
            {
                b.UseSetting(key, value);
            }
        });

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Verso.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root (Verso.slnx).");
    }
}
