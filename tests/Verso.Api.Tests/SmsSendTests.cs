using System.Net;
using System.Text;
using Verso.Application.Connections;
using Verso.Infrastructure.Connections;

namespace Verso.Api.Tests;

/// <summary>The connection test's request must stay byte for byte what it was before the Brevo helpers were shared with the Send SMS step.</summary>
public sealed class SmsProbeRequestUnchangedTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Uri { get; private set; }
        public byte[]? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri!.AbsoluteUri;
            Body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }
    }

    [Theory]
    [InlineData("TestOrg", "marketing", false, "{\"sender\":\"TestSender\",\"recipient\":\"35799000000\",\"content\":\"Verso test message\",\"type\":\"marketing\",\"unicodeEnabled\":false,\"organisationPrefix\":\"TestOrg\"}")]
    [InlineData("", "transactional", true, "{\"sender\":\"TestSender\",\"recipient\":\"35799000000\",\"content\":\"Verso test message\",\"type\":\"transactional\",\"unicodeEnabled\":true}")]
    [InlineData("Café & <Co>", "transactional", true, "{\"sender\":\"TestSender\",\"recipient\":\"35799000000\",\"content\":\"Verso test message\",\"type\":\"transactional\",\"unicodeEnabled\":true,\"organisationPrefix\":\"Caf\\u00E9 \\u0026 \\u003CCo\\u003E\"}")]
    public async Task The_probe_sends_exactly_the_same_address_and_body_bytes_as_before(string prefix, string type, bool unicode, string expectedBody)
    {
        var handler = new CapturingHandler();
        var settings = new SmsConnectionSettings("https://api.brevo.test/v3/", "TestSender", type, unicode, prefix, "test-key-not-real", TimeSpan.FromSeconds(10));

        var result = await new BrevoSmsProbe(() => handler).SendTestAsync(settings, "35799000000", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("https://api.brevo.test/v3/transactionalSMS/sms", handler.Uri);
        Assert.Equal(expectedBody, Encoding.UTF8.GetString(handler.Body!));
    }
}
