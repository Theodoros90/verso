using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verso.Application;
using Verso.Application.Connections;
using Verso.Application.Executions;
using Verso.Domain.Connections;
using Verso.Domain.Executions;
using Verso.Infrastructure.Connections;
using Verso.Infrastructure.Security;
using Verso.Rendering;

namespace Verso.Api.Tests;

/// <summary>
/// The Fetch from API step in a real run with the REAL <see cref="ApiFetchService"/> and <see cref="HttpApiFetchSender"/> (wired as the Worker wires them:
/// a service built from a scoped database context, the master-key secret protector, the connection options), talking to a fake HTTP message handler: no
/// network. FetchApiExecutionTests uses a FakeApiFetch for the engine's own behaviour; these tests cover the seam between the two (the resolved path that
/// reaches the service's own limits, the status codes and the engine's retry rules).
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class FetchApiRealServiceExecutionTests(MigratedDatabaseFixture fixture) : IDisposable
{
    private const string AnswerMarker = "Quokka Real Service Holdings";

    private static readonly AesGcmSecretProtector Protector = new(AesGcmSecretProtector.GenerateKey());

    private readonly HttpClient _client = fixture.Factory.CreateClient();
    private readonly TestDocumentStore _store = new();
    private readonly CapturingPdfPrinter _printer = new();
    private readonly RecordingExecutionJobs _jobs = new();
    private ExecutionTestKit Kit => new(fixture.Factory, _client);

    public void Dispose()
    {
        _store.Dispose();
        _client.Dispose();
    }

    /// <summary>The real service, built per call from a fresh scope (the engine's own context is scoped to its run, the service's to the call).</summary>
    private sealed class ScopedRealFetch(IServiceProvider services, ApiFetchTests.FakeHandler handler) : IApiFetch
    {
        public async Task<ApiFetchResult> RunAsync(ApiFetchRequest request, CancellationToken ct)
        {
            using var scope = services.CreateScope();
            var service = new ApiFetchService(scope.ServiceProvider.GetRequiredService<IVersoDbContext>(), Protector, new HttpApiFetchSender(() => handler), new ApiConnectionOptions());
            return await service.RunAsync(request, ct);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Answer() => $$"""{ "name": "{{AnswerMarker}}", "address": "1 Synthetic Way", "vatNumber": "CY00000000Z" }""";

    private async Task<string> CreateConnectionAsync()
    {
        var key = $"real-{Guid.NewGuid():N}"[..20];
        var now = DateTimeOffset.UtcNow;
        var connection = ApiConnection.Create(key, "Real " + key, "https://api.real-service.example/v1", "/status", "None", "", "", "", 15, "tests", now);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IVersoDbContext>();
        db.ApiConnections.Add(connection);
        await db.SaveChangesAsync(CancellationToken.None);
        return key;
    }

    private static JsonObject FetchConfig(string key, string path) => new()
    {
        ["connection"] = key,
        ["path"] = path,
        ["query"] = "",
        ["resultName"] = "customer",
        ["maxResponseKB"] = 32,
        ["timeoutSeconds"] = 20,
    };

    private static JsonNode Data(string poNumber)
    {
        var data = ExecutionTestKit.Sample.DeepClone();
        data["invoice"]!["poNumber"] = poNumber;
        return data;
    }

    private async Task<Guid> SubmitAsync(string poNumber)
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(fetchConfig: FetchConfig(await CreateConnectionAsync(), "/customers/{{invoice.poNumber}}"));
        return await Kit.SubmitNewAsync(key, Data(poNumber));
    }

    private Task RunEngineAsync(Guid id, ApiFetchTests.FakeHandler handler) =>
        Kit.RunEngineAsync(id, _printer, _store, _jobs, apiFetch: new ScopedRealFetch(fixture.Factory.Services, handler));

    private Task<List<string>> EventTypesAsync(Guid id) =>
        Kit.WithDbAsync(db => db.ExecutionEvents.Where(e => e.ExecutionId == id).OrderBy(e => e.Id).Select(e => e.Type).ToListAsync());

    [Fact]
    public async Task Found_answer_is_merged_and_the_document_uses_it()
    {
        var handler = new ApiFetchTests.FakeHandler((_, _) => Task.FromResult(Json(Answer())));
        var id = await SubmitAsync("PO-REAL-1");

        await RunEngineAsync(id, handler);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal("https://api.real-service.example/v1/customers/PO-REAL-1", Assert.Single(handler.Uris));
        Assert.Contains(AnswerMarker, Assert.Single(_printer.Bodies), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Over_long_encoded_path_rejects_the_run_and_nothing_is_sent()
    {
        var handler = new ApiFetchTests.FakeHandler((_, _) => Task.FromResult(Json("{}")));
        var id = await SubmitAsync(string.Concat(Enumerable.Repeat("α", 90))); // 540 encoded characters

        await RunEngineAsync(id, handler);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Rejected", done.GetProperty("status").GetString());
        Assert.Equal("data.fetch_invalid", done.GetProperty("failureCode").GetString());
        Assert.Empty(handler.Requests);
        Assert.Empty(_jobs.Scheduled);
    }

    [Fact]
    public async Task Hostile_dot_segment_value_rejects_the_run_and_nothing_is_sent()
    {
        var handler = new ApiFetchTests.FakeHandler((_, _) => Task.FromResult(Json("{}")));
        var id = await SubmitAsync("..");

        await RunEngineAsync(id, handler);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Rejected", done.GetProperty("status").GetString());
        Assert.Equal("data.fetch_invalid", done.GetProperty("failureCode").GetString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_503_answer_is_retried_and_the_next_attempt_succeeds()
    {
        var calls = 0;
        var handler = new ApiFetchTests.FakeHandler((_, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
            ? Json("{}", HttpStatusCode.ServiceUnavailable)
            : Json(Answer())));
        var id = await SubmitAsync("PO-REAL-503");

        await RunEngineAsync(id, handler);

        Assert.Equal("Running", (await Kit.GetExecutionAsync(id)).GetProperty("status").GetString()); // a retry is scheduled
        Assert.Contains(ExecutionEventTypes.RetryScheduled, await EventTypesAsync(id));
        Assert.NotEmpty(_jobs.Scheduled);

        await RunEngineAsync(id, handler);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(AnswerMarker, Assert.Single(_printer.Bodies), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_401_answer_fails_the_run_without_retry()
    {
        var handler = new ApiFetchTests.FakeHandler((_, _) => Task.FromResult(Json("{}", HttpStatusCode.Unauthorized)));
        var id = await SubmitAsync("PO-REAL-401");

        await RunEngineAsync(id, handler);

        var done = await Kit.GetExecutionAsync(id);
        Assert.Equal("Failed", done.GetProperty("status").GetString());
        Assert.Equal("api.unauthorized", done.GetProperty("failureCode").GetString());
        Assert.Single(handler.Requests);
        Assert.Empty(_jobs.Scheduled);
        Assert.DoesNotContain(ExecutionEventTypes.RetryScheduled, await EventTypesAsync(id));
    }
    /// <summary>Stands in for Chromium and keeps the HTML of every print (header, body and footer), so a test can see which values the document used.</summary>
    private sealed class CapturingPdfPrinter : IPdfPrinter
    {
        private readonly List<string> _bodies = [];

        public IReadOnlyList<string> Bodies
        {
            get
            {
                lock (_bodies)
                {
                    return [.. _bodies];
                }
            }
        }

        public Task<PdfOutput> PrintAsync(PrintDocument document, CancellationToken cancellationToken)
        {
            lock (_bodies)
            {
                _bodies.Add(document.HeaderTemplate + document.BodyHtml + document.FooterTemplate);
            }

            return Task.FromResult(new PdfOutput(Encoding.ASCII.GetBytes($"%PDF-1.7\n% fake {Guid.NewGuid():N}\n%%EOF\n"), 0, 1));
        }
    }
}
