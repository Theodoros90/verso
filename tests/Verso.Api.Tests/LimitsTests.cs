using System.Net;
using Microsoft.AspNetCore.Hosting;

namespace Verso.Api.Tests;

/// <summary>
/// Plan §7 / M9 limits that the in-memory host can show: submissions are rate-limited per user. (Request body caps are
/// enforced by Kestrel/IIS, which the in-memory test server doesn't emulate; the E2E suite checks them on Kestrel.)
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class LimitsTests(MigratedDatabaseFixture fixture)
{
    [Fact]
    public async Task Submissions_AreRateLimitedPerUser_WithA429ProblemAndRetryAfter()
    {
        await using var host = fixture.Factory.WithWebHostBuilder(b => b.UseSetting("Limits:SubmissionsPerMinute", "3"));
        using var operatorClient = host.CreateClientAs("dev.operator");
        using var adminClient = host.CreateClientAs("dev.admin");
        var key = await new ExecutionTestKit(fixture.Factory, fixture.Factory.CreateClient()).PublishInvoiceWorkflowAsync();
        var kit = new ExecutionTestKit(fixture.Factory, operatorClient);

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? limited = null;
        for (var i = 0; i < 4; i++)
        {
            var response = await kit.SubmitAsync(key, Guid.NewGuid().ToString(), ExecutionTestKit.Sample);
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                limited = response;
            }
            else
            {
                response.Dispose();
            }
        }

        Assert.Equal([HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.TooManyRequests], statuses);
        using (limited)
        {
            Assert.Equal("60", limited!.Headers.GetValues("Retry-After").Single());
            Assert.Equal("rate_limited", (await ExecutionTestKit.ReadAsync(limited)).GetProperty("code").GetString());
        }

        // Another user has their own allowance, and reads are not limited.
        using var other = await new ExecutionTestKit(fixture.Factory, adminClient).SubmitAsync(key, Guid.NewGuid().ToString(), ExecutionTestKit.Sample);
        Assert.Equal(HttpStatusCode.Accepted, other.StatusCode);
        using var read = await operatorClient.GetAsync(new Uri("/api/v1/executions", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }
}
