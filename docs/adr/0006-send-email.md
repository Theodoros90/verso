# ADR 0006: Send email step (SMTP relay, real)

- Status: accepted (implemented 2026-11-12, awaiting acceptance)
- Requested by the product owner on 2026-11-12: build the prototype's *Send email* output for real, through SMTP ("Real SMTP sending", chosen when asked). CLAUDE.md listed email among the integrations that stay simulated until approved; this ADR records that approval, scoped to one SMTP relay.

## Context

Verso can already build an HTML email body (ADR 0004) and a PDF or Word file, and it archives them. Nothing left the system. The prototype's *Send email* step takes a recipient, subject, body, an attachment choice and retries.

## Decision

1. **One relay, set by the administrator.** The Worker (only the Worker; the API never sends mail) talks to the bank's SMTP relay named in its `Email` configuration section: `Host`, `Port` (587), `EnableSsl` (on), `UserName`, `From`, `FromName`, `AllowedRecipientDomains`. The password comes only from the environment or a secret store (`Email__Password`), never from the repository and never from a workflow. Workflow authors choose recipients and text, not servers or credentials. With no `Host` or `From`, a Send email step fails with `email.not_configured` (technical, not retried): a workflow can be designed and published before mail is set up.
2. **No new dependency.** The built-in `System.Net.Mail.SmtpClient` is used. Microsoft marks it as not recommended for new development (no OAuth, basic feature set), but it suffices for an authenticated or IP-allowed relay with TLS. If the bank needs OAuth (Exchange Online) or finer error handling, replacing it with MailKit (MIT) behind `IEmailSender` is a contained change and needs its own approval.
3. **Layering.** `IEmailSender` (with `EmailMessage`, `EmailSendException`, `EmailAddresses`) lives in `Verso.Application/Executions`; `SmtpEmailSender` in `Verso.Infrastructure/Email`; only the Worker registers it (`AddVersoEmail`). No new project.
4. **Step.** `send-email`, category *output*; ports `sent` and `failed`. Settings: `to` (up to 10 addresses separated by commas or semicolons, or fields such as `{{customer.email}}`), `subject`, `body` (plain text), `attach` (`none` or `document`). `sent` may lead on to Archive.
5. **Attachment.** With `attach: document` the run's current document (made by the Generate step before it on the path) is read back from storage, its SHA-256 is checked, and it is attached (largest 10 MB, else `email.attachment_too_large`). When that document is a *Generate HTML email* file, it becomes the HTML body instead, and `body` is the plain-text alternative. No document on the path: `email.no_document` (not retried).
6. **Data problems vs technical failures.** A recipient that is missing or not a plain address, or an empty subject, is a data problem: port `failed`, and the run ends **Rejected** with `email.data_incomplete` unless the workflow handles the port. A relay that can't be reached or times out is retryable (the engine's usual 3 attempts with backoff). A refused recipient or message, a missing configuration, or a recipient outside `AllowedRecipientDomains` ends the run **Failed** without retry.
7. **Safety.** Recipients must be plain `user@host.tld` addresses (no display names, no control characters), so data can't add recipients or headers; the subject is forced to one line; the sender and relay are fixed by configuration; `AllowedRecipientDomains` (recommended in production) refuses the whole message if any recipient is outside the list. Text is sent as plain text; HTML comes only from the HTML email renderer, which encodes values (ADR 0004).
8. **History carries no content.** The step result holds the number of recipients, the attachment's file name, whether the body was HTML, the message id and warnings for fields with no value, never an address or the text.

## Consequences

- **At-least-once delivery.** If the Worker stops after the relay accepted the message but before the step is saved, or the relay times out after accepting, a retry sends it again. SMTP has no exactly-once guarantee. Downstream consumers should tolerate a duplicate.
- Bounces are not tracked: `sent` means the relay accepted the message.
- Not done: cc/bcc, reply-to, several attachments, a per-workflow sender, a mail log screen, OAuth, a test-send button.
