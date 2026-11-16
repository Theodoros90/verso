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

## Addendum 1 (2026-11-16): several named relays

- Requested 2026-11-16 by the product owner ("a bank may have more than one email connector and need to pick one in different flows"). Status: **accepted, implemented 2026-11-16, awaiting acceptance**.

**Context.** Decision 1 allows exactly one relay and says a per-workflow sender is not done. A bank may run several (customer statements, internal notices, a bulk relay) and want different workflows to use different ones. The Connections page cannot carry this: it is frontend-only and simulated, and the Worker cannot read it (see ADR 0010).

**Decision.**
1. **Named relays in Worker configuration.** `Email:Relays:<key>` holds the same fields as today's section (`Host`, `Port`, `EnableSsl`, `UserName`, `From`, `FromName`, `AllowedRecipientDomains`, `Timeout`). Keys use the same pattern as print queue keys (lower-case letters, digits, hyphens). Passwords come only from the environment or a secret store (`Email__Relays__<key>__Password`), never from the repository or a workflow.
2. **The existing section is the relay `default`.** The current top-level `Email` settings keep working unchanged and are the relay used when a step names none. No migration of configuration is needed.
3. **Step setting.** Send email gets an optional `relay` setting (a key). Absent or empty means `default`. A workflow stores the key only, never host, sender or credentials. Published workflow versions without the setting keep sending through `default`.
4. **Same safety rules per relay.** Each relay has its own `AllowedRecipientDomains`; all checks of decision 7 apply to the chosen relay. A relay's domain list is not merged with another's.
5. **Errors.** An unknown key ends the run **Failed**, not retried: `email.relay_unknown`. A known relay with no `Host` or `From` fails with `email.not_configured` as before. History records the relay key (not host or sender) in the step result.
6. **Authoring.** Like the Print queue step, the author types the key (the field's help names the administrator's keys). Offering a dropdown needs the API to know the relay names, which means the API reads the same `Email:Relays` names; that is a follow-up, not part of this addendum.
7. **Layering.** The relay key travels on `EmailMessage.Relay` (an optional last field, so `IEmailSender` keeps its signature); `SmtpEmailSender` resolves options per key (a dictionary lookup, no new package, no new project). Only the Worker registers it.

**Consequences.**
- Adding a relay is an administrator configuration change plus a Worker restart; no migration, no new package, no API change.
- A workflow naming a relay that exists on one server but not another fails at run time; the runbook must say relay keys have to match across environments.
- Not done: a relay list in the designer, a relay picker on the Connections page, per-relay rate limits, OAuth.
8. **Connections page.** Superseded by ADR 0015: an email connection is now created in Connections with the authentication SMTP credentials and is stored on the server; the Send email step picks it from a dropdown (ADR 0015 addendum 1).
