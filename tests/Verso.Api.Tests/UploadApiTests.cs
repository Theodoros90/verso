using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Verso.Application.Uploads;

namespace Verso.Api.Tests;

/// <summary>
/// The upload endpoints (ADR 0023): <c>POST /api/v1/workflows/{key}/uploads</c> (multipart, one part <c>file</c>) and
/// <c>GET /api/v1/workflows/{key}/upload-settings</c>, their roles, size limits, rate limit, and <c>acceptsUpload</c> on the
/// workflow list. The in-memory server shows the streaming size check; the per-request body limit (and the 2 MB cap on every
/// other route) is enforced by Kestrel, so those tests host the API on Kestrel. Synthetic files only.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class UploadApiTests : IDisposable
{
    private const string Boundary = "verso-test-boundary-7d1f";
    private const long OneMb = 1_048_576;

    private readonly MigratedDatabaseFixture _fixture;
    private readonly HttpClient _admin;
    private readonly HttpClient _operator;
    private readonly ExecutionTestKit _kit;

    public UploadApiTests(MigratedDatabaseFixture fixture)
    {
        _fixture = fixture;
        _admin = fixture.Factory.CreateClient();
        _operator = fixture.Factory.CreateClientAs("dev.operator");
        _kit = new ExecutionTestKit(fixture.Factory, _admin);
    }

    public void Dispose()
    {
        _admin.Dispose();
        _operator.Dispose();
    }

    private static byte[] Pdf(string marker = "") => Encoding.ASCII.GetBytes($"%PDF-1.7\n% synthetic statement {marker}{Guid.NewGuid():N}\n%%EOF\n");

    private static JsonObject UploadTrigger(string accept, string maxSize) => new()
    {
        ["id"] = "trigger", ["type"] = "file-upload", ["title"] = "File upload", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 80 },
        ["config"] = new JsonObject { ["accept"] = accept, ["maxSize"] = maxSize, ["who"] = "staff" },
    };

    private Task<string> PublishAsync(string accept = "PDF, XML, CSV", string maxSize = "1mb") =>
        _kit.PublishInvoiceWorkflowAsync(trigger: UploadTrigger(accept, maxSize));

    /// <summary>
    /// A client without the test factory's redirect handler, which copies every request body into memory before sending it: with
    /// this one the server pulls the body as it reads, so a test can count what the endpoint read.
    /// </summary>
    private HttpClient StreamingClient(string user)
    {
        var client = _fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Verso.Api.Auth.DevUsers.Token(user));
        return client;
    }

    private static Uri UploadUri(string key) => new($"/api/v1/workflows/{key}/uploads", UriKind.Relative);

    private static MultipartFormDataContent FileForm(string fileName, byte[] content, string partName = "file")
    {
        var form = new MultipartFormDataContent(Boundary);
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, partName, fileName);
        return form;
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string key, string fileName, byte[] content)
    {
        using var form = FileForm(fileName, content);
        return await client.PostAsync(UploadUri(key), form);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) => await ExecutionTestKit.ReadAsync(response);

    private async Task<int> UploadRowsAsync(string key) =>
        await _kit.WithDbAsync(async db =>
        {
            var id = await db.Workflows.Where(w => w.Key == key).Select(w => w.Id).SingleAsync();
            return await db.Uploads.CountAsync(u => u.WorkflowId == id);
        });

    private Task<List<string>> RejectedRulesAsync(string key) =>
        _kit.WithDbAsync(async db => (await db.AuditEntries.AsNoTracking()
                .Where(a => a.Action == UploadService.Actions.FileUploadRejected && a.Details!.Contains(key)).Select(a => a.Details!).ToListAsync())
            .Select(d => JsonSerializer.Deserialize<JsonElement>(d).GetProperty("rule").GetString()!).ToList());

    [Fact]
    public async Task Operator_uploads_pdf_and_gets_202_with_location()
    {
        var key = await PublishAsync();

        using var response = await UploadAsync(_operator, key, "statement.pdf", Pdf());

        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var body = await ReadAsync(response);
        var id = body.GetProperty("executionId").GetGuid();
        Assert.Equal($"/api/v1/executions/{id}", response.Headers.Location!.OriginalString);
        Assert.Equal("Queued", body.GetProperty("status").GetString());
        Assert.Equal(key, body.GetProperty("workflowKey").GetString());
        Assert.Equal(1, await UploadRowsAsync(key));
    }

    [Fact]
    public async Task Same_file_again_gets_200_with_same_execution()
    {
        var key = await PublishAsync();
        var content = Pdf();

        using var first = await UploadAsync(_operator, key, "statement.pdf", content);
        using var second = await UploadAsync(_operator, key, "statement-copy.pdf", content);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.True(second.StatusCode == HttpStatusCode.OK, await second.Content.ReadAsStringAsync());
        Assert.Equal((await ReadAsync(first)).GetProperty("executionId").GetGuid(), (await ReadAsync(second)).GetProperty("executionId").GetGuid());
        Assert.Null(second.Headers.Location);
    }

    [Fact]
    public async Task Viewer_template_author_workflow_author_auditor_get_403_anonymous_401()
    {
        var key = await PublishAsync();
        foreach (var user in new string?[] { "dev.viewer", "dev.template-author", "dev.workflow-author", "dev.auditor", "dev.no-access", null })
        {
            using var client = _fixture.Factory.CreateClientAs(user);
            var expected = user is null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;

            using var upload = await UploadAsync(client, key, "statement.pdf", Pdf());
            using var settings = await client.GetAsync(new Uri($"/api/v1/workflows/{key}/upload-settings", UriKind.Relative));

            Assert.True(upload.StatusCode == expected, $"{user ?? "anonymous"} upload: {upload.StatusCode}");
            Assert.True(settings.StatusCode == expected, $"{user ?? "anonymous"} settings: {settings.StatusCode}");
        }

        Assert.Equal(0, await UploadRowsAsync(key));
    }

    [Fact]
    public async Task Missing_file_part_or_extra_part_is_400()
    {
        var key = await PublishAsync();

        var cases = new List<(string Name, HttpContent Content)>();

        var noFile = new MultipartFormDataContent(Boundary) { { new StringContent("hello"), "note" } };
        cases.Add(("only a text field", noFile));

        var extra = FileForm("statement.pdf", Pdf());
        extra.Add(new StringContent("hello"), "note");
        cases.Add(("file plus a text field", extra));

        var textFirst = new MultipartFormDataContent(Boundary) { { new StringContent("hello"), "note" } };
        textFirst.Add(new ByteArrayContent(Pdf()), "file", "statement.pdf");
        cases.Add(("text field before the file", textFirst));

        var two = FileForm("a.pdf", Pdf());
        two.Add(new ByteArrayContent(Pdf()), "file", "b.pdf");
        cases.Add(("two file parts", two));

        cases.Add(("file part with another name", FileForm("statement.pdf", Pdf(), partName: "document")));

        var noName = new MultipartFormDataContent(Boundary) { { new ByteArrayContent(Pdf()), "file" } };
        cases.Add(("file part without a file name", noName));

        cases.Add(("empty multipart body", new MultipartFormDataContent(Boundary)));
        cases.Add(("JSON instead of multipart", JsonContent.Create(new { file = "statement.pdf" })));

        foreach (var (name, content) in cases)
        {
            using (content)
            using (var response = await _operator.PostAsync(UploadUri(key), content))
            {
                var text = await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{name}: {response.StatusCode} {text}");
                Assert.Equal("validation", JsonSerializer.Deserialize<JsonElement>(text).GetProperty("code").GetString());
            }
        }

        Assert.Equal(0, await UploadRowsAsync(key));
    }

    [Fact]
    public async Task Rejected_file_is_400_with_its_rule_code()
    {
        var key = await PublishAsync(accept: "PDF, CSV");

        using var response = await UploadAsync(_operator, key, "customers.csv", Pdf());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("upload.content_mismatch", (await ReadAsync(response)).GetProperty("code").GetString());
        Assert.Equal(0, await UploadRowsAsync(key));
    }

    [Fact]
    public async Task Unknown_workflow_is_404_and_workflow_without_upload_step_is_409_before_reading_the_body()
    {
        using var missing = await UploadAsync(_operator, "no-such-workflow-" + Guid.NewGuid().ToString("N")[..8], "statement.pdf", Pdf());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("workflow.not_found", (await ReadAsync(missing)).GetProperty("code").GetString());

        var apiKey = await _kit.PublishInvoiceWorkflowAsync();
        using var body = new GeneratedBody(Pdf(), 10 * OneMb);
        using var streaming = StreamingClient("dev.operator");
        using var notAvailable = await streaming.PostAsync(UploadUri(apiKey), body.Content(withLength: false));
        Assert.Equal(HttpStatusCode.Conflict, notAvailable.StatusCode);
        Assert.Equal("upload.not_available", (await ReadAsync(notAvailable)).GetProperty("code").GetString());
        Assert.True(body.BytesRead < OneMb, $"The body was read ({body.BytesRead:N0} bytes) for a workflow that takes no uploads.");
    }

    [Fact]
    public async Task Body_over_ceiling_is_413_and_not_buffered()
    {
        var key = await PublishAsync(maxSize: "1mb"); // the step's limit: 1 MB
        using var body = new GeneratedBody(Pdf(), UploadRules.DefaultCeilingBytes + 2 * OneMb);
        using var streaming = StreamingClient("dev.operator");

        using var response = await streaming.PostAsync(UploadUri(key), body.Content(withLength: true));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("upload.too_large", (await ReadAsync(response)).GetProperty("code").GetString());
        // Reading stopped at the step's limit + 1 byte; what else was read is the in-memory server's own buffering, not the handler's.
        Assert.True(body.BytesRead < OneMb + 1 + OneMb, $"Read {body.BytesRead:N0} of {body.Length:N0} bytes.");
        Assert.Equal(0, await UploadRowsAsync(key));
        Assert.Equal(["upload.too_large"], await RejectedRulesAsync(key));
    }

    [Fact]
    public async Task Chunked_body_without_content_length_over_limit_is_stopped()
    {
        var key = await PublishAsync(maxSize: "1mb");
        using var body = new GeneratedBody(Pdf(), long.MaxValue / 2); // never ends
        using var streaming = StreamingClient("dev.operator");

        using var response = await streaming.PostAsync(UploadUri(key), body.Content(withLength: false));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("upload.too_large", (await ReadAsync(response)).GetProperty("code").GetString());
        Assert.True(body.BytesRead < OneMb + 1 + OneMb, $"Read {body.BytesRead:N0} bytes.");
        Assert.Equal(0, await UploadRowsAsync(key));
    }

    [Fact]
    public async Task File_of_exactly_the_limit_is_accepted()
    {
        var key = await PublishAsync(maxSize: "1mb");
        var content = new byte[OneMb];
        Array.Fill(content, (byte)'a');
        Pdf().CopyTo(content, 0);

        using var response = await UploadAsync(_operator, key, "statement.pdf", content);

        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Rate_limit_returns_429_after_the_per_minute_budget()
    {
        await using var host = _fixture.Factory.WithWebHostBuilder(b => b.UseSetting("Limits:UploadsPerMinute", "2"));
        using var operatorClient = host.CreateClientAs("dev.operator");
        using var adminClient = host.CreateClientAs("dev.admin");
        var key = await PublishAsync();

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? limited = null;
        for (var i = 0; i < 3; i++)
        {
            var response = await UploadAsync(operatorClient, key, $"statement-{i}.pdf", Pdf());
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

        Assert.Equal([HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.TooManyRequests], statuses);
        using (limited)
        {
            Assert.Equal("60", limited!.Headers.GetValues("Retry-After").Single());
            Assert.Equal("rate_limited", (await ReadAsync(limited)).GetProperty("code").GetString());
        }

        // Another user has their own allowance, and submissions have their own budget.
        using var other = await UploadAsync(adminClient, key, "statement-admin.pdf", Pdf());
        Assert.Equal(HttpStatusCode.Accepted, other.StatusCode);
    }

    [Fact]
    public async Task Settings_endpoint_returns_accept_and_max_for_upload_workflow_and_409_for_others()
    {
        var key = await PublishAsync(accept: "PDF, XML", maxSize: "1mb");
        var apiKey = await _kit.PublishInvoiceWorkflowAsync();

        using var settings = await _operator.GetAsync(new Uri($"/api/v1/workflows/{key}/upload-settings", UriKind.Relative));
        using var other = await _operator.GetAsync(new Uri($"/api/v1/workflows/{apiKey}/upload-settings", UriKind.Relative));
        using var missing = await _operator.GetAsync(new Uri("/api/v1/workflows/no-such-workflow-x/upload-settings", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        var body = await ReadAsync(settings);
        Assert.Equal([".pdf", ".xml"], body.GetProperty("accept").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(OneMb, body.GetProperty("maxBytes").GetInt64());
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        Assert.Equal("upload.not_available", (await ReadAsync(other)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Summary_dto_flags_AcceptsUpload()
    {
        var upload = await PublishAsync();
        var api = await _kit.PublishInvoiceWorkflowAsync();
        var draftOnly = "up-" + Guid.NewGuid().ToString("N")[..12];
        using (var create = await _admin.PostAsJsonAsync(new Uri("/api/v1/workflows", UriKind.Relative), new { key = draftOnly, name = "Draft only" }))
        {
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        }

        var deactivated = await PublishAsync();
        var deactivatedId = await _kit.WithDbAsync(db => db.Workflows.Where(w => w.Key == deactivated).Select(w => w.Id).SingleAsync());
        using (var off = await _admin.PostAsync(new Uri($"/api/v1/workflows/{deactivatedId}/deactivate", UriKind.Relative), null))
        {
            Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        }

        var flags = await AcceptsUploadAsync(inactive: false);
        var inactiveFlags = await AcceptsUploadAsync(inactive: true);

        Assert.True(flags[upload]);
        Assert.False(flags[api]);
        Assert.False(flags[draftOnly]);
        Assert.False(inactiveFlags[deactivated]); // a deactivated workflow takes no uploads
    }

    private async Task<Dictionary<string, bool>> AcceptsUploadAsync(bool inactive)
    {
        var flags = new Dictionary<string, bool>();
        for (var page = 1; ; page++)
        {
            using var response = await _operator.GetAsync(new Uri($"/api/v1/workflows?page={page}&pageSize=100{(inactive ? "&inactive=true" : "")}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await ReadAsync(response);
            foreach (var item in body.GetProperty("items").EnumerateArray())
            {
                flags[item.GetProperty("key").GetString()!] = item.GetProperty("acceptsUpload").GetBoolean();
            }

            if (page * 100 >= body.GetProperty("total").GetInt32())
            {
                return flags;
            }
        }
    }

    /// <summary>
    /// On Kestrel (which enforces body limits; the in-memory server does not): the upload route raises the 2 MB cap to its own
    /// limit (ceiling + 1 MB), stops a body declared larger than that, and every other route keeps its cap.
    /// </summary>
    [Fact]
    public async Task On_kestrel_upload_route_has_its_own_limit_and_other_routes_keep_the_2_MB_cap()
    {
        const long ceiling = 4 * OneMb; // per-request limit 5 MB
        await using var host = _fixture.Factory.WithWebHostBuilder(b => b.UseSetting("Limits:MaxUploadBytes", ceiling.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        host.UseKestrel(0);
        using var client = host.CreateClientAs("dev.operator");
        using var admin = host.CreateClientAs("dev.admin");
        var key = await PublishAsync(maxSize: "25mb"); // clamped to the 4 MB ceiling

        // 3 MB: over the global 2 MB cap, within this route's limit.
        var big = new byte[3 * OneMb];
        Array.Fill(big, (byte)'a');
        Pdf().CopyTo(big, 0);
        using (var accepted = await UploadAsync(client, key, "big.pdf", big))
        {
            Assert.True(accepted.StatusCode == HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync());
        }

        Assert.Equal(1, await UploadRowsAsync(key));

        // A small file followed by 6 MB of padding after the closing boundary: the declared length is over ceiling + 1 MB.
        using (var padded = new GeneratedBody(Pdf(), 0, epilogueBytes: 6 * OneMb))
        using (var request = new HttpRequestMessage(HttpMethod.Post, UploadUri(key)) { Content = padded.Content(withLength: true) })
        using (var tooLarge = await SendOverLimitAsync(client, request))
        {
            if (tooLarge is not null)
            {
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
                Assert.Equal("upload.too_large", (await ReadAsync(tooLarge)).GetProperty("code").GetString());
            }
        }

        Assert.Equal(1, await UploadRowsAsync(key));

        // Every other route keeps its cap: 3 MB to submit (its own 1.1 MB limit) and to create a workflow (the global 2 MB).
        var apiKey = await new ExecutionTestKit(_fixture.Factory, _admin).PublishInvoiceWorkflowAsync();
        var padding = new string('x', (int)(3 * OneMb));
        using (var submit = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/workflows/{apiKey}/executions", UriKind.Relative)))
        {
            submit.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
            submit.Content = new StringContent($$"""{ "data": { "padding": "{{padding}}" } }""", Encoding.UTF8, "application/json");
            using var response = await SendOverLimitAsync(client, submit);
            Assert.True(response is null || response.StatusCode == HttpStatusCode.RequestEntityTooLarge, $"Expected 413 or a reset, got {(int?)response?.StatusCode}.");
        }

        using (var create = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/workflows", UriKind.Relative)))
        {
            create.Content = new StringContent($$"""{ "key": "too-big", "name": "x", "description": "{{padding}}" }""", Encoding.UTF8, "application/json");
            using var response = await SendOverLimitAsync(admin, create);
            Assert.True(response is null || response.StatusCode == HttpStatusCode.RequestEntityTooLarge, $"Expected 413 or a reset, got {(int?)response?.StatusCode}.");
        }

        Assert.Equal(1, await UploadRowsAsync(key)); // nothing refused left a row
        Assert.Equal(0, await _kit.WithDbAsync(db => db.Workflows.CountAsync(w => w.Key == "too-big")));
    }

    /// <summary>
    /// Sends a body Kestrel must refuse. <c>Expect: 100-continue</c> keeps the client from sending the body before the answer; if
    /// Kestrel still answers 413 and closes the connection while the body is being written, the client sees a reset instead of
    /// the 413 (a race in the transport, not a different outcome): null then. Never a success either way.
    /// </summary>
    private static async Task<HttpResponseMessage?> SendOverLimitAsync(HttpClient client, HttpRequestMessage request)
    {
        request.Headers.ExpectContinue = true;
        try
        {
            return await client.SendAsync(request);
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// A multipart body with one file part (<c>file</c>, <c>statement.pdf</c>) whose content is <paramref name="head"/> followed by
    /// filler bytes, produced while it is read and never held in memory, then the closing boundary and an optional epilogue.
    /// Counts the bytes the server pulled.
    /// </summary>
    private sealed class GeneratedBody(byte[] head, long fillerBytes, long epilogueBytes = 0) : Stream
    {
        private readonly byte[] _prefix = [.. Encoding.ASCII.GetBytes($"--{Boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"statement.pdf\"\r\nContent-Type: application/pdf\r\n\r\n"), .. head];
        private readonly byte[] _suffix = Encoding.ASCII.GetBytes($"\r\n--{Boundary}--\r\n");
        private long _position;

        public long BytesRead => Interlocked.Read(ref _position);

        public override long Length => _prefix.Length + fillerBytes + _suffix.Length + epilogueBytes;

        public StreamContent Content(bool withLength)
        {
            var content = new StreamContent(this, 64 * 1024);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/form-data; boundary={Boundary}");
            if (withLength)
            {
                content.Headers.ContentLength = Length;
            }

            return content;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var written = 0;
            while (written < count && _position < Length)
            {
                buffer[offset + written] = ByteAt(_position);
                written++;
                Interlocked.Increment(ref _position);
            }

            return written;
        }

        private byte ByteAt(long position)
        {
            if (position < _prefix.Length)
            {
                return _prefix[position];
            }

            position -= _prefix.Length;
            if (position < fillerBytes)
            {
                return (byte)'a';
            }

            position -= fillerBytes;
            return position < _suffix.Length ? _suffix[position] : (byte)'z';
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
