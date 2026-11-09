using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Verso.Api.Tests;

/// <summary>Hosts the real API in memory, pointed at a given test database connection string.</summary>
internal sealed class VersoApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Verso", connectionString);
    }
}
