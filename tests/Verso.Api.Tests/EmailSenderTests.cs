using Microsoft.Extensions.Options;
using Verso.Application.Executions;
using Verso.Infrastructure.Email;

namespace Verso.Api.Tests;

/// <summary>Recipient parsing and the SMTP sender's refusals that happen before anything is sent (no network is used).</summary>
public sealed class EmailSenderTests
{
    private static EmailMessage Message(params string[] to) => new(to, "Subject", "Text", null, []);

    [Theory]
    [InlineData("a@b.example", 1)]
    [InlineData("a@b.example, c@d.example; A@B.example", 2)] // separators, trimming, case-insensitive duplicates
    public void Valid_Recipients_AreAccepted(string text, int count)
    {
        var (addresses, problem) = EmailAddresses.Parse(text);

        Assert.Null(problem);
        Assert.Equal(count, addresses.Count);
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("Jane <a@b.example>")]
    [InlineData("a@b.example\r\nBcc: x@y.example")]
    [InlineData("a@localhost")]
    [InlineData("a b@c.example")]
    public void Invalid_Recipients_AreRefused(string text)
    {
        var (addresses, problem) = EmailAddresses.Parse(text);

        Assert.NotNull(problem);
        Assert.Empty(addresses);
    }

    [Fact]
    public void MoreThanTenRecipients_AreRefused() =>
        Assert.NotNull(EmailAddresses.Parse(string.Join(',', Enumerable.Range(1, 11).Select(i => $"u{i}@b.example"))).Problem);

    [Fact]
    public void OneLine_RemovesLineBreaks() => Assert.Equal("a  Bcc: x", EmailAddresses.OneLine("a\r\nBcc: x"));

    [Fact]
    public async Task WithoutAHost_NothingIsSent_AndItIsNotRetried()
    {
        var sender = new SmtpEmailSender(Options.Create(new EmailOptions()));

        var ex = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(Message("a@b.example"), CancellationToken.None));

        Assert.Equal("email.not_configured", ex.Code);
        Assert.False(ex.Retryable);
    }

    [Fact]
    public async Task ARecipientOutsideTheAllowedDomains_RefusesTheWholeMessage()
    {
        var sender = new SmtpEmailSender(Options.Create(new EmailOptions { Host = "relay.invalid", From = "verso@bank.example", AllowedRecipientDomains = ["bank.example"] }));

        var ex = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(Message("ok@bank.example", "x@elsewhere.example"), CancellationToken.None));

        Assert.Equal("email.recipient_not_allowed", ex.Code);
        Assert.False(ex.Retryable);
    }
}
