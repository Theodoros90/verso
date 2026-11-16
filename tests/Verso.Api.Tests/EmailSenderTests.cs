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
        var sender = new SmtpEmailSender(new EmailRelayStore(Options.Create(new EmailOptions())));

        var ex = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(Message("a@b.example"), CancellationToken.None));

        Assert.Equal("email.not_configured", ex.Code);
        Assert.False(ex.Retryable);
    }

    private static EmailOptions TwoRelays() => new()
    {
        Host = "default.invalid", From = "verso@bank.example",
        Relays =
        {
            ["statements"] = new EmailOptions { Host = "statements.invalid", From = "statements@bank.example", AllowedRecipientDomains = ["customers.example"] },
            ["half-set"] = new EmailOptions { Host = "half.invalid" },
        },
    };

    [Theory]
    [InlineData("no-such-relay")]
    [InlineData("Bad Key")]
    [InlineData("../x")]
    public async Task AnUnknownOrInvalidRelay_IsRefused_AndNotRetried(string relay)
    {
        var sender = new SmtpEmailSender(new EmailRelayStore(Options.Create(TwoRelays())));

        var ex = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(Message("a@b.example") with { Relay = relay }, CancellationToken.None));

        Assert.Equal("email.relay_unknown", ex.Code);
        Assert.False(ex.Retryable);
    }

    [Fact]
    public async Task ARelayWithoutHostOrFrom_IsNotConfigured_EvenWhenTheDefaultIsSetUp()
    {
        var sender = new SmtpEmailSender(new EmailRelayStore(Options.Create(TwoRelays())));

        var ex = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(Message("a@b.example") with { Relay = "half-set" }, CancellationToken.None));

        Assert.Equal("email.not_configured", ex.Code);
        Assert.Contains("half-set", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachRelay_UsesItsOwnAllowedDomains()
    {
        var sender = new SmtpEmailSender(new EmailRelayStore(Options.Create(TwoRelays())));

        // "statements" only allows customers.example; the default relay has no list, so it gets past the check (and then fails to connect, not with recipient_not_allowed).
        var ex = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(Message("a@bank.example") with { Relay = "statements" }, CancellationToken.None));

        Assert.Equal("email.recipient_not_allowed", ex.Code);
    }

    [Fact]
    public void RelayKeys_FollowTheSamePatternAsPrintQueues()
    {
        Assert.True(EmailRelays.IsValidKey("statements"));
        Assert.True(EmailRelays.IsValidKey("internal-2"));
        Assert.False(EmailRelays.IsValidKey("Statements"));
        Assert.False(EmailRelays.IsValidKey("a"));
        Assert.Equal(EmailRelays.DefaultKey, EmailRelays.Resolve("  "));
        Assert.Equal(EmailRelays.DefaultKey, EmailRelays.Resolve(null));
    }

    [Fact]
    public async Task ARecipientOutsideTheAllowedDomains_RefusesTheWholeMessage()
    {
        var sender = new SmtpEmailSender(new EmailRelayStore(Options.Create(new EmailOptions { Host = "relay.invalid", From = "verso@bank.example", AllowedRecipientDomains = ["bank.example"] })));

        var ex = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(Message("ok@bank.example", "x@elsewhere.example"), CancellationToken.None));

        Assert.Equal("email.recipient_not_allowed", ex.Code);
        Assert.False(ex.Retryable);
    }
}
