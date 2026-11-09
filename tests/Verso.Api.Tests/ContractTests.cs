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
    public void Api_RefusesToStart_OutsideDevelopmentAndTesting(string environment)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            b.UseSetting("ConnectionStrings:Verso", new TestDatabase("guard").ConnectionString);
        });

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("no authentication yet", ex.Message, StringComparison.Ordinal);
    }

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
