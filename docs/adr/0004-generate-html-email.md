# ADR 0004: Generate HTML email step (an email body as a document)

- Status: accepted (implemented 2026-11-12, awaiting acceptance)
- Requested by the product owner on 2026-11-12: build the prototype's *Generate HTML email* step ("Builds a responsive email body") for real. Plan §9 listed HTML email output under "Not in MVP".

## Context

PDF and DOCX steps render a published template version (a page layout). An email is not a page: it has a subject, a short preview line, running text and at most one button. The prototype offers three fixed email templates and a dark-mode switch. Nothing is sent: plan §2 still rules out email delivery, and the prototype's *Send email* step stays simulated.

## Decision

1. **No new template kind.** The email's content is typed into the step (the product owner chose this over a new template type with its own designer and versions). Settings: `layout` (`invoice`, `statement`, `reminder`: the prototype's three choices, changing only the heading label and accent colour), `subject`, `preheader`, `body`, `buttonLabel`, `buttonUrl`, `footer`, `fileName`, `darkMode`. Every text may contain `{{path|filter}}` fields, resolved with the same grammar and filters as file name patterns (`PatternText`). The step is therefore not pinned to a template version and `Documents.TemplateVersionId` became nullable (migration `DocumentTemplateVersionOptional`, no data change).
2. **Pure renderer.** `Verso.Rendering/Email/EmailRenderer` (layout-free: settings + data → subject, HTML, plain text, issues). No new project, no new dependency, no I/O; it runs in the Worker's engine but needs neither Chromium nor a writer library.
3. **Output.** One self-contained `.html` file: table layout and inline styles (works in Outlook-style clients), a hidden preview line, a responsive rule for narrow screens, and, when `darkMode` is on, `color-scheme` meta tags and a `prefers-color-scheme: dark` rule. No scripts, no images, no remote resources, no `url()`.
4. **Safety.** Resolved values are plain text, HTML-encoded once on the way into the markup (line breaks are encoded line by line). The subject is forced to one line, so data can't start another mail header. The button link must resolve to `http`, `https` or `mailto`; anything else (`javascript:`, `data:`, relative or network-path addresses) is an error that rejects the run (`email.url_invalid`). The workflow validator also checks a link typed without fields, and that the label and link are set together.
5. **Same lifecycle as the other document steps.** `success` and `failed` ports; data problems (a missing value, an empty subject or body, a bad link) end the run as Rejected with `document.data_incomplete`; the file is stored through `IDocumentStore` under a Verso-generated key (`.html`), is idempotent per (execution, node, iteration), and is archived by the Archive step. The download endpoint already serves archived records as attachments with `nosniff`, so an email file is never rendered inside Verso's own origin.
6. **History carries no message text.** The step result records the file (name, size, hash) only. The plain-text alternative that the renderer builds is tested but not stored (it would put customer data into execution history); a future Send step rebuilds it from the step's settings.

## Consequences

- An HTML email file is not previewed in the UI (download only); a preview would need a sandboxed frame and is not part of this change.
- Layout is a fixed single column. More presets, images (logo) or several buttons need renderer changes.
- Not done: sending (SMTP), attaching the PDF, a plain-text file next to the HTML, email templates with their own versions.
