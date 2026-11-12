using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using Microsoft.Extensions.Options;
using Verso.Application.Executions;

namespace Verso.Infrastructure.Email;

/// <summary>
/// The bank's SMTP relay, set once by the administrator (configuration section <c>Email</c>). The password, when the relay needs one,
/// comes from the environment or a secret store (<c>Email__Smtp__Password</c>), never from the repository or from a workflow.
/// </summary>
public sealed class EmailOptions
{
    public const string Section = "Email";

    /// <summary>Relay host name. Empty: email is not set up, and a Send email step fails with <c>email.not_configured</c>.</summary>
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    /// <summary>TLS (STARTTLS or implicit) to the relay. Leave on outside a closed development setup.</summary>
    public bool EnableSsl { get; set; } = true;
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>The sender address (the relay may require it to be one it knows).</summary>
    public string From { get; set; } = "";
    public string FromName { get; set; } = "Verso";
    /// <summary>When not empty, only these recipient domains (such as <c>example.com</c>) can receive mail; any other recipient refuses the whole message.</summary>
    public string[] AllowedRecipientDomains { get; set; } = [];
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Sends through <see cref="SmtpClient"/> (built in; no extra package). One connection per message.</summary>
public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task<string> SendAsync(EmailMessage message, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.Host) || string.IsNullOrWhiteSpace(o.From))
        {
            throw new EmailSendException("email.not_configured", "Email is not set up: the administrator must set Email:Host and Email:From for the Worker.", retryable: false);
        }

        if (o.AllowedRecipientDomains.Length > 0)
        {
            var blocked = message.To.FirstOrDefault(a => !o.AllowedRecipientDomains.Contains(a[(a.LastIndexOf('@') + 1)..], StringComparer.OrdinalIgnoreCase));
            if (blocked is not null)
            {
                throw new EmailSendException("email.recipient_not_allowed", "A recipient's domain is not on the list of domains Verso may send to (Email:AllowedRecipientDomains).", retryable: false);
            }
        }

        using var mail = new MailMessage { From = new MailAddress(o.From, o.FromName), Subject = message.Subject, SubjectEncoding = System.Text.Encoding.UTF8 };
        foreach (var to in message.To)
        {
            mail.To.Add(to);
        }

        var messageId = $"<{Guid.NewGuid():N}@verso>";
        mail.Headers.Add("Message-ID", messageId);
        mail.BodyEncoding = System.Text.Encoding.UTF8;
        if (message.Html is null)
        {
            mail.Body = message.Text;
        }
        else
        {
            mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.Text, System.Text.Encoding.UTF8, MediaTypeNames.Text.Plain));
            mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.Html, System.Text.Encoding.UTF8, MediaTypeNames.Text.Html));
        }

        foreach (var file in message.Attachments)
        {
            mail.Attachments.Add(new Attachment(new MemoryStream(file.Content), file.FileName, file.ContentType));
        }

        using var client = new SmtpClient(o.Host, o.Port) { EnableSsl = o.EnableSsl, Timeout = (int)o.Timeout.TotalMilliseconds, DeliveryMethod = SmtpDeliveryMethod.Network };
        if (!string.IsNullOrEmpty(o.UserName))
        {
            client.Credentials = new NetworkCredential(o.UserName, o.Password);
        }

        try
        {
            await client.SendMailAsync(mail, ct);
        }
        catch (SmtpFailedRecipientException ex)
        {
            throw new EmailSendException("email.recipient_refused", $"The relay refused a recipient ({(int)ex.StatusCode}).", retryable: false, ex);
        }
        catch (SmtpException ex) when (ex.StatusCode is SmtpStatusCode.MustIssueStartTlsFirst or SmtpStatusCode.ClientNotPermitted or SmtpStatusCode.MailboxUnavailable or SmtpStatusCode.SyntaxError or SmtpStatusCode.CommandUnrecognized)
        {
            throw new EmailSendException("email.relay_refused", $"The relay refused the message ({(int)ex.StatusCode}).", retryable: false, ex);
        }
        catch (Exception ex) when ((ex is SmtpException or IOException or System.Net.Sockets.SocketException or OperationCanceledException) && !ct.IsCancellationRequested)
        {
            // The message never names the content or the addresses.
            throw new EmailSendException("email.relay_unavailable", "The SMTP relay could not be reached or did not accept the message.", retryable: true, ex);
        }

        return messageId;
    }
}
