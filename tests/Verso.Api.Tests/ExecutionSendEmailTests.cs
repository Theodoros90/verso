using System.Text.Json;
using System.Text.Json.Nodes;
using Verso.Application.Executions;

namespace Verso.Api.Tests;

/// <summary>The Send email step in a real run (ADR 0006): recipients and text come from the data, the document is attached, and failures end the run the right way.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ExecutionSendEmailTests(MigratedDatabaseFixture fixture) : IDisposable
{
    private readonly HttpClient _client = fixture.Factory.CreateClient();
    private readonly TestDocumentStore _store = new();
    private readonly FakePdfPrinter _printer = new();
    private readonly FakeEmailSender _email = new();
    private readonly RecordingExecutionJobs _jobs = new();
    private ExecutionTestKit Kit => new(fixture.Factory, _client);

    private static JsonObject Send(string to = "{{customer.email}}", string subject = "Invoice {{invoice.number}}", string attach = "document") =>
        new() { ["to"] = to, ["subject"] = subject, ["body"] = "Dear {{customer.name}}, your invoice {{invoice.number}}.", ["attach"] = attach };

    public void Dispose()
    {
        _store.Dispose();
        _client.Dispose();
    }

    private async Task<JsonElement> RunAsync(JsonObject config, JsonNode? data = null)
    {
        var key = await Kit.PublishInvoiceWorkflowAsync(sendConfig: config);
        var id = await Kit.SubmitNewAsync(key, data ?? ExecutionTestKit.Sample);
        await Kit.RunEngineAsync(id, _printer, _store, _jobs, email: _email);
        return await Kit.GetExecutionAsync(id);
    }

    [Fact]
    public async Task TheMail_GoesToTheCustomer_WithThePdfAttached_AndTheRunStillArchives()
    {
        var done = await RunAsync(Send());

        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        var mail = Assert.Single(_email.Sent);
        Assert.Equal(["ap@kestrel-logistics.example"], mail.To);
        Assert.Equal("Invoice INV-2026-003901", mail.Subject);
        Assert.Contains("Kestrel Logistics Ltd", mail.Text, StringComparison.Ordinal);
        Assert.Null(mail.Html);
        var file = Assert.Single(mail.Attachments);
        Assert.Equal("INV-2026-003901.pdf", file.FileName);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Single(_store.Files("archive"));

        var step = done.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("nodeId").GetString() == "send");
        Assert.Equal("sent", step.GetProperty("outputPort").GetString());
        var result = step.GetProperty("result");
        Assert.Equal(1, result.GetProperty("recipients").GetInt32());
        Assert.DoesNotContain("kestrel", result.GetRawText(), StringComparison.OrdinalIgnoreCase); // no address, no text in history
    }

    [Fact]
    public async Task NoAttachment_SendsTextOnly()
    {
        await RunAsync(Send(attach: "none"));

        Assert.Empty(Assert.Single(_email.Sent).Attachments);
    }

    [Fact]
    public async Task AMissingRecipient_RejectsTheRun_AndNothingIsSent()
    {
        var data = ExecutionTestKit.Sample.DeepClone();
        data["customer"]!.AsObject().Remove("email");

        var done = await RunAsync(Send(), data);

        Assert.Equal("Rejected", done.GetProperty("status").GetString());
        Assert.Equal("email.data_incomplete", done.GetProperty("failureCode").GetString());
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task ARecipientThatIsNotAnAddress_OrHasALineBreak_RejectsTheRun()
    {
        var data = ExecutionTestKit.Sample.DeepClone();
        data["customer"]!["email"] = "a@b.example\r\nBcc: spy@evil.example";

        var done = await RunAsync(Send(), data);

        Assert.Equal("Rejected", done.GetProperty("status").GetString());
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task ALineBreakInTheSubject_CannotStartAnotherHeader()
    {
        var data = ExecutionTestKit.Sample.DeepClone();
        data["invoice"]!["number"] = "X\r\nBcc: spy@evil.example";

        await RunAsync(Send(), data);

        Assert.DoesNotContain('\n', Assert.Single(_email.Sent).Subject);
        Assert.DoesNotContain('\r', _email.Sent[0].Subject);
    }

    [Fact]
    public async Task ARelayThatIsDown_IsRetried_ThenTheMailGoesOut()
    {
        _email.Failure = new EmailSendException("email.relay_unavailable", "down", retryable: true);
        _email.FailTimes = 1;
        var key = await Kit.PublishInvoiceWorkflowAsync(sendConfig: Send());
        var id = await Kit.SubmitNewAsync(key, ExecutionTestKit.Sample);

        await Kit.RunEngineAsync(id, _printer, _store, _jobs, email: _email);
        Assert.Equal("Running", (await Kit.GetExecutionAsync(id)).GetProperty("status").GetString()); // a retry is scheduled
        Assert.NotEmpty(_jobs.Scheduled);
        Assert.Empty(_email.Sent);

        await Kit.RunEngineAsync(id, _printer, _store, _jobs, email: _email);
        Assert.Equal("Succeeded", (await Kit.GetExecutionAsync(id)).GetProperty("status").GetString());
        Assert.Single(_email.Sent);
    }

    [Fact]
    public async Task ARelayThatIsNotSetUp_FailsTheRun_WithoutRetry()
    {
        _email.Failure = new EmailSendException("email.not_configured", "not set up", retryable: false);

        var done = await RunAsync(Send());

        Assert.Equal("Failed", done.GetProperty("status").GetString());
        Assert.Equal("email.not_configured", done.GetProperty("failureCode").GetString());
        Assert.Empty(_jobs.Scheduled);
    }
}
