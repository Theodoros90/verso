# Verso API

Endpoints that exist today. Planned contracts are in [plan §5](plan/2026-11-09-verso-mvp-plan.md#5-api-contracts).

- The machine-readable contract is [openapi/v1.json](openapi/v1.json), also served live at `/openapi/v1.json`. A test fails when the API and that file drift apart. After an intended change, regenerate it with `$env:VERSO_UPDATE_OPENAPI = "1"; dotnet test --filter OpenApiDocument`, review the diff, and update this page in the same change.
- **Sign-in is required** for everything except `GET /api/health` and `GET /api/auth/config`: `Authorization: Bearer <token>`. See [Sign-in and roles](#sign-in-and-roles).

## Conventions

| Topic | Rule |
|---|---|
| Base path | `/api/v1` (health: `/api/health`) |
| Format | JSON, camelCase. Timestamps are ISO 8601 with offset (UTC). Ids are GUIDs. |
| Errors | RFC 9457 `application/problem+json` with a stable `code` and a `traceId`. Validation errors (400) add `errors: { "<json path>": ["message"] }`. |
| Concurrency | Reads of a template version return an `ETag` header. Changing the draft (`PUT …/draft`, `POST …/draft/publish`) requires `If-Match: <that ETag>`. Missing or `*` → 428; stale → 412. |
| Paging | `?page=1&pageSize=25` (max 100) → `{ items, page, pageSize, total }` |
| Correlation | Send `X-Correlation-Id` (1–64 of `A–Z a–z 0–9 . _ : -`) to tie a request to your own logs; otherwise one is generated. It comes back in the response header, appears in every log line of the request, and a submission stores it on the execution (the Worker logs it too). |
| Limits | Request bodies up to 2 MB (`Limits:MaxRequestBodyBytes`; submissions 1.1 MB) → 413. Submissions per user and minute: 120 (`Limits:SubmissionsPerMinute`) → 429 with `Retry-After`. |

### Error codes

| HTTP | `code` | When |
|---|---|---|
| 400 | `validation` | Field errors; see `errors` |
| 404 | `template.not_found` | Unknown template id |
| 404 | `template.version_not_found` | Version id doesn't belong to the template |
| 409 | `template.key_taken` | Create with a key that already exists |
| 409 | `template.no_draft` | Reading, saving or publishing a draft when there is none (e.g. right after publishing). Published versions are immutable. Start a new draft first. |
| 409 | `template.draft_exists` | Starting a draft when one already exists |
| 412 | `precondition.failed` | `If-Match` doesn't match the current draft: someone else changed it, or you used an old ETag |
| 428 | `precondition.required` | `If-Match` missing (or `*`) |
| 404 | `preview.not_found` | Unknown PDF preview id (or deleted after 24 hours) |
| 409 | `preview.not_ready` | PDF content requested while the preview is still queued or rendering |
| 409 | `preview.failed` | PDF content requested for a preview that failed; read its `error` |
| 401 | `unauthenticated` | No token, or a token that is invalid, expired or for another audience |
| 403 | `forbidden` | Signed in, but your roles don't allow this ([matrix](#roles)) |
| 413 | `request_too_large` | Request body over the limit |
| 429 | `rate_limited` | Too many submissions in a minute; see `Retry-After` |
| 404 | `document.not_found` | Unknown document id, or a document that isn't archived yet |
| 409 | `document.not_archived` | Content requested for a document that is generated but not archived yet |
| 409 | `document.content_missing` | The archived file is missing from storage |
| 409 | `document.integrity_failed` | The archived file doesn't match its recorded SHA-256; it is not served |
| 400 | `execution.idempotency_key_required` | Submission without an `Idempotency-Key` header |
| 404 | `execution.not_found` | Unknown execution id |
| 409 | `execution.idempotency_conflict` | `Idempotency-Key` already used for this workflow with **different** data; `executionId` names the original |
| 409 | `workflow.not_published` | Submission to a workflow with no published version |
| 409 | `execution.not_retryable` | Retry of an execution that isn't `Failed` (Rejected runs need corrected data, not a retry) |
| 404 | `folder_access_test.not_found` | Unknown access test id (or deleted after 24 hours) |
| 409 | `folder_location.key_taken` | Administration: the key is set in the Worker's configuration |
| 409 | `folder_location.in_use` | Administration: files from or to the location are still being processed |
| 404 | `folder_location.not_found` | Administration: no managed location with this key |

## Sign-in and roles

| Mode (`Auth:Mode`) | Where | Token |
|---|---|---|
| `Entra` (default) | everywhere | An Entra ID access token for the Verso API registration (audience = its client id or `api://<client id>`), obtained by the SPA with MSAL (auth code + PKCE). Startup fails if `Auth:Entra:TenantId` or `Auth:Entra:ApiClientId` is missing. |
| `Development` | Development and Testing environments only (startup refuses it elsewhere) | `dev:<user id>` for one of the synthetic users listed by `GET /api/auth/config`: `dev.viewer`, `dev.operator`, `dev.template-author`, `dev.workflow-author`, `dev.auditor`, `dev.admin`, `dev.no-access` |

Browsers can't send headers on WebSockets, so the SignalR hub also accepts the token as `?access_token=`. No other endpoint does, because URLs end up in logs.

**Roles** come from the token's `roles` claim (Entra app roles `Verso.Viewer`, `Verso.Operator`, …) or from security groups mapped in `Auth:RoleMap` (`"<group object id or role value>": "Operator"`). A signed-in user without a Verso role can only call `/me`.

### `GET /api/auth/config` (anonymous)

`{ "mode": "Entra", "entra": { "authority": "https://login.microsoftonline.com/<tenant>", "clientId": "<spa client id>", "scope": "api://<api client id>/access_as_user" }, "devUsers": null }`. In Development, `entra` is null and `devUsers` lists `{ id, name, roles, token }`.

### `GET /api/v1/me`

`{ "id": "<oid>", "name": "…", "roles": ["Operator"], "permissions": { "read": true, "templateAuthoring": false, "workflowAuthoring": false, "operate": true, "readPayload": false, "readAudit": false, "administer": false } }`. The SPA uses it to hide what the user can't do; the API enforces it.

### Roles

Every endpoint names a policy; one with no policy would still require a Verso role (secure by default). `RoleMatrixTests` calls every endpoint as every role and fails when a new endpoint is missing from the matrix.

| Area | Viewer | Operator | Template author | Workflow author | Auditor | Admin |
|---|:-:|:-:|:-:|:-:|:-:|:-:|
| Read templates, workflows, node types, executions, events, documents; download; SignalR | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Create and edit templates, previews (HTML/PDF), publish templates |  |  | ✓ |  |  | ✓ |
| Create and edit workflows, validate, publish workflows |  |  |  | ✓ |  | ✓ |
| Submit executions, retry failed ones |  | ✓ |  |  |  | ✓ |
| Read submitted payloads (`/executions/{id}/input`) |  |  |  |  | ✓ | ✓ |
| Audit trail (`/audit`, `/audit/actions`) |  |  |  |  | ✓ | ✓ |
| Approved folder locations, a workflow's folder activity (`/folder-locations`, `/workflows/{id}/folder-input`) | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Folder "Test access" (`/folder-access-tests`) |  |  |  | ✓ |  | ✓ |
| Administer folder locations (`/admin/folder-locations`) |  |  |  |  |  | ✓ |

## Health

### `GET /api/health`

Anonymous. 200 when `Healthy` or `Degraded` (migrations pending), 503 when `Unhealthy` (database unreachable). Never includes connection details or exception text.

```json
{ "status": "Healthy", "totalDurationMs": 14.2,
  "checks": [ { "name": "database", "status": "Healthy", "description": "Connected; schema is up to date.",
                "durationMs": 13.8, "data": { "appliedMigrations": 2, "pendingMigrations": 0 } } ] }
```

## Templates

Lifecycle: **create** (starts with an empty draft) → **save draft** (any number of times) → **publish** (draft becomes version N, frozen) → **start draft** (copy of the latest published version) → save → publish (N+1) …

A template has at most one draft at a time. Version numbers start at 1 and are assigned on publish.

| Method | Path | Success | Purpose |
|---|---|---|---|
| GET | `/api/v1/templates` | 200 page of `TemplateSummary` | List, ordered by key |
| POST | `/api/v1/templates` | 201 `Template`, `Location` | Create with an empty draft |
| GET | `/api/v1/templates/{id}` | 200 `Template` | Metadata |
| GET | `/api/v1/templates/{id}/versions` | 200 `TemplateVersionSummary[]` | History: draft first, then published newest first |
| GET | `/api/v1/templates/{id}/versions/{versionId}` | 200 `TemplateVersion` + `ETag` | One version with full content |
| GET | `/api/v1/templates/{id}/draft` | 200 `TemplateVersion` + `ETag` | The current draft |
| POST | `/api/v1/templates/{id}/draft` | 201 `TemplateVersion` + `ETag` | Start a new draft from the latest published version |
| PUT | `/api/v1/templates/{id}/draft` | 200 `TemplateVersion` + new `ETag` | Replace the draft content (`If-Match`) |
| POST | `/api/v1/templates/{id}/draft/publish` | 200 `TemplateVersion` (published) | Freeze the draft as the next version (`If-Match`) |
| POST | `/api/v1/templates/{id}/draft/preview/html` | 200 `HtmlPreview` | Render the draft to HTML with sample or supplied data |
| POST | `/api/v1/templates/{id}/draft/preview/pdf` | 202 `PdfPreview`, `Location` | Queue a PDF rendering of the draft (Worker, Chromium); see [PDF previews](#pdf-previews) |

### Create: `POST /api/v1/templates`

```json
{ "key": "INV-STD", "name": "Commercial invoice", "description": "Optional", "documentType": "Invoice" }
```

- `key`: 2–32 characters, `A–Z`, `0–9`, `-`, starting with a letter. Unique; never changes.
- `name`: 1–200 characters. `description`: up to 1000.
- `documentType`: optional, defaults to `Invoice` (the only value in the MVP).

Response `Template`:

```json
{ "id": "…", "key": "INV-STD", "name": "Commercial invoice", "description": null, "documentType": "Invoice",
  "createdAt": "2026-11-09T08:49:25.9+00:00", "createdBy": "dev.user",
  "latestPublishedVersion": null, "draftVersionId": "…" }
```

### Save draft: `PUT /api/v1/templates/{id}/draft`

Headers: `If-Match: "AAAAAAAAB9I="` (the ETag from your last read or save). The body replaces the whole draft, and all four parts are required:

```json
{
  "pageSettings": { "size": "A4", "orientation": "portrait", "marginsMm": { "top": 18, "right": 16, "bottom": 18, "left": 16 } },
  "layout": { "layoutSchemaVersion": 1 },
  "dataSchema": { "type": "object" },
  "sampleData": { "invoice": { "number": "INV-2026-003901" } }
}
```

| Field | Rule (M2) |
|---|---|
| `pageSettings.size` | `A4` only |
| `pageSettings.orientation` | `portrait` or `landscape` |
| `pageSettings.marginsMm.*` | 0–50 |
| `layout` | A valid [layout model v1](layout-model.md) document; at most 1,000,000 characters. Structural errors come back per field, e.g. `layout.body.blocks[0].props.src`. Unresolved data bindings are not save errors; they show up as preview issues. |
| `dataSchema` | JSON object (intended: a JSON Schema for the template's data); at most 256,000 characters |
| `sampleData` | JSON object, **synthetic data only**; at most 256,000 characters |

Response `TemplateVersion` (also returned by the GET, start-draft and publish endpoints):

```json
{ "versionId": "…", "templateId": "…", "versionNumber": null, "status": "Draft",
  "pageSettings": { "size": "A4", "orientation": "portrait", "marginsMm": { "top": 18.00, "right": 16.00, "bottom": 18.00, "left": 16.00 } },
  "layout": { "layoutSchemaVersion": 1 }, "dataSchema": { "type": "object" }, "sampleData": { "invoice": { "number": "INV-2026-003901" } },
  "contentHash": null, "createdAt": "…", "createdBy": "dev.user", "updatedAt": "…", "updatedBy": "dev.user",
  "publishedAt": null, "publishedBy": null }
```

### Publish: `POST /api/v1/templates/{id}/draft/publish`

Headers: `If-Match` with the draft's current ETag. No body. Returns the now-published version with `versionNumber` and `contentHash`.

`contentHash` is the SHA-256 (hex) of the canonical JSON of `{ pageSettings, layout, dataSchema }`: properties sorted, no whitespace, margins without trailing zeros. Sample data isn't included, because it doesn't affect generated documents.

### Preview: `POST /api/v1/templates/{id}/draft/preview/html`

Renders the current draft in **preview mode**. The body is optional:

```json
{ "data": { "invoice": { "number": "INV-X" } } }
```

Without `data` (or without a body), the draft's `sampleData` is used. `data` must be a JSON object of at most 1,000,000 characters, **synthetic data only**. No `If-Match` is needed; nothing is changed or audited.

```json
{ "html": "<!doctype html>…",
  "issues": [ { "severity": "error", "code": "binding.missing", "message": "No value for 'customer.name'.",
                "elementId": "billto", "path": "customer.name" } ] }
```

- `html` is a self-contained document (inline CSS, embedded images, a CSP that blocks scripts and network access) showing one continuous A4 sheet. Unresolved values appear as dashed `{{path}}` placeholders. Display it in a sandboxed `<iframe srcdoc>`. Real page breaks are visible in the PDF preview (below).
- `issues`: codes and meanings are in [layout-model.md](layout-model.md#issue-codes). An empty list means every binding resolved.
- Errors: 400 `validation` (bad `data`), 404 `template.not_found`, 409 `template.no_draft`, 409 `template.layout_invalid` (only for drafts saved before layout validation existed; re-save to fix).

### Summaries

`TemplateSummary`: `{ id, key, name, documentType, latestPublishedVersion, hasDraft, updatedAt }`

`TemplateVersionSummary`: `{ versionId, versionNumber, status, contentHash, updatedAt, updatedBy, publishedAt, publishedBy }`

## PDF previews

A PDF rendering of a template draft, produced by the Worker with Chromium (the API never runs a browser). It is the real output: paginated, with the header and footer repeated on every page and the fonts embedded. Preview mode applies: unresolved values print as `{{path}}` placeholders and are listed in `issues`.

| Method | Path | Success | Purpose |
|---|---|---|---|
| POST | `/api/v1/templates/{id}/draft/preview/pdf` | 202 `PdfPreview`, `Location: /api/v1/previews/{previewId}` | Queue a rendering |
| GET | `/api/v1/previews/{previewId}` | 200 `PdfPreview` | Status, issues, error (the source of truth) |
| GET | `/api/v1/previews/{previewId}/content` | 200 `application/pdf` | The PDF, once `Succeeded` |

### Queue: `POST /api/v1/templates/{id}/draft/preview/pdf`

Same optional body and validation as the HTML preview: `{ "data": { … } }` (synthetic data, a JSON object of at most 1,000,000 characters); without it, the draft's sample data is used. The draft's page settings, layout and the data are **copied when you call this**, so later draft edits don't change a queued preview. No `If-Match`; nothing is audited.

Errors: 400 `validation`, 404 `template.not_found`, 409 `template.no_draft`, 409 `template.layout_invalid`.

`PdfPreview`:

```json
{ "previewId": "…", "templateId": "…", "status": "Succeeded",
  "issues": [ { "severity": "error", "code": "binding.missing", "message": "No value for 'customer.name'.", "elementId": "billto", "path": "customer.name" } ],
  "error": null, "sizeBytes": 21290, "createdAt": "…", "completedAt": "…" }
```

- `status`: `Queued` → `Running` → `Succeeded` | `Failed`. A technical failure (Chromium crash, timeout) is retried, 3 attempts in total; while a retry is pending the status is `Queued` again and `error` describes the last attempt. After the third, `Failed`.
- `sizeBytes`: PDF size once `Succeeded`, otherwise `null`.

### Content: `GET /api/v1/previews/{previewId}/content`

`200 application/pdf` with `Content-Disposition: inline`, `Cache-Control: no-store` and `X-Content-Type-Options: nosniff`. Errors: 404 `preview.not_found`, 409 `preview.not_ready`, 409 `preview.failed`.

Previews are temporary: the Worker deletes them 24 hours after they were requested.

## Real-time notifications (SignalR)

Hub: `/hubs/executions` (plan §5.5). Needs sign-in with any Verso role; browsers pass the token as `?access_token=` (the SignalR client's `accessTokenFactory` does this).

| Direction | Message | Payload |
|---|---|---|
| Client → server | `SubscribePreview(previewId)` / `UnsubscribePreview(previewId)` | — |
| Client → server | `SubscribeExecution(executionId)` / `UnsubscribeExecution(executionId)` | — |
| Client → server | `SubscribeWorkflow(workflowId)` / `UnsubscribeWorkflow(workflowId)` | — |
| Client → server | `SubscribeExecutions()` / `UnsubscribeExecutions()` | every execution update (the history list); an addition to the plan |
| Server → client | `PreviewReady` | `{ "previewId": "…", "status": "Succeeded" \| "Failed" }` |
| Server → client | `ExecutionUpdated` | `{ "executionId": "…", "status": "Running", "eventId": 36, "type": "StepStarted" }`, to the execution, workflow and all-executions groups |
| Server → client | `StepUpdated` | `{ "executionId": "…", "nodeId": "pdf", "status": "Running" \| "Succeeded" \| "Failed", "outputPort": "success" \| null, "eventId": 36 }`, to the execution group only |

Notifications are hints, not the source of truth: after one arrives, read the REST API. Subscribe first, then read once, because the work may have finished before you subscribed. `status` in `ExecutionUpdated` is the execution's status when the notification was sent, which can be newer than `eventId` when several events were relayed together. A client in more than one group (for example an execution and all executions) may receive the same notification twice, so handle them idempotently.

**Previews:** the API relays finished previews from the database about every 500 ms (the Worker and the API share only the database), so a notification arrives within about a second. **Executions:** the relay polls `ExecutionEvent` rows about every 500 ms, starting from the latest event when the API starts. Event ids are database identity values and concurrent runs can commit out of order, so each poll also re-reads the last 100 ids and skips those already sent; an event that becomes visible late is still delivered. Group memberships don't survive a reconnect: after SignalR reconnects, subscribe again and catch up with `GET /api/v1/executions/{id}/events?after={lastEventId}`. Clients should also poll slowly (the designer polls every 3 s, the run view every 3 s while a run is unfinished) in case WebSockets are blocked.

## Workflows

A workflow is a graph of steps: a trigger, then steps connected output → input. Its lifecycle is the same as a template's: **create** (a draft containing only an API / webhook trigger with default settings) → **save draft** (`If-Match`) → **publish** (version N, frozen) → **start draft** (copy of the latest published version). Every change is audited (`entityType=Workflow`).

| Method | Path | Success | Purpose |
|---|---|---|---|
| GET | `/api/v1/node-types` | 200 `NodeType[]` | Node catalog: type, category, ports, settings schema, defaults |
| GET | `/api/v1/workflows` | 200 page of `WorkflowSummary` | List, ordered by key |
| POST | `/api/v1/workflows` | 201 `Workflow`, `Location` | Create `{ key, name, description? }` |
| GET | `/api/v1/workflows/{id}` | 200 `Workflow` | Metadata |
| GET | `/api/v1/workflows/{id}/versions` | 200 `WorkflowVersionSummary[]` | History: draft first, then published newest first |
| GET | `/api/v1/workflows/{id}/versions/{versionId}` | 200 `WorkflowVersion` + `ETag` | One version |
| GET | `/api/v1/workflows/{id}/draft` | 200 `WorkflowVersion` + `ETag` | The draft |
| POST | `/api/v1/workflows/{id}/draft` | 201 `WorkflowVersion` + `ETag` | Start a draft from the latest published version |
| PUT | `/api/v1/workflows/{id}/draft` | 200 `WorkflowVersion` + new `ETag` | Save `{ definition }` (`If-Match`) |
| POST | `/api/v1/workflows/{id}/draft/validate` | 200 `WorkflowValidation` | Validate `{ definition }` (unsaved edits) or, without a body, the saved draft |
| POST | `/api/v1/workflows/{id}/draft/publish` | 200 `WorkflowVersion` (published) | Validate and freeze (`If-Match`) |

- `key`: 2–48 characters, lower-case letters, digits and single hyphens, starting with a letter (`invoice-standard`). Unique; never changes; used in the submission URL `POST /api/v1/workflows/{key}/executions` ([Executions](#executions)).
- Errors: 404 `workflow.not_found` / `workflow.version_not_found`, 409 `workflow.key_taken` / `workflow.no_draft` / `workflow.draft_exists` / `workflow.nothing_published` / `workflow.invalid`, 412 / 428 as for templates.

### Definition

```json
{ "nodes": [ { "id": "validate", "type": "validate", "title": "Validate invoice data", "position": { "x": 260, "y": 80 },
               "config": { "required": ["invoice.number"], "types": [ { "field": "invoice.date", "type": "date" } ] } } ],
  "edges": [ { "id": "e1", "source": "trigger", "sourcePort": "out", "target": "validate", "targetPort": "in" } ] }
```

Saving checks only the **structure** (400 `validation` with paths like `definition.nodes[0].id`): ids of 1–64 `A–Z a–z 0–9 - _`, unique; numeric positions; `config` an object; no unknown properties; at most 200 nodes and 400 edges; at most 500,000 characters. An unfinished graph can be saved; it is stored exactly as sent.

### Node types

| `type` | Category | Inputs → outputs | Settings |
|---|---|---|---|
| `api-trigger` ("API / webhook") | input | — → `out` | `path` (required; mock path such as `/hooks/invoices`, shown as `https://mock.verso.local/…`; nothing listens there, real runs use `POST /api/v1/workflows/{key}/executions`); `auth` (required; `none`, `api-key`, `hmac-sha256`, `oauth2`, `mtls`; descriptive only, not enforced); `expectedSchema` and `samplePayload` (optional JSON objects, ≤ 20 000 chars each; the designer checks the sample against the schema and suggests the sample's field names in later steps; real submissions are not checked against either). New workflows start with these settings filled in. |
| `erp-connector` ("ERP connector") | input | — → `out` | `system` (`dynamics-365`, `sap-s4hana`, `oracle-netsuite`), `entity` (record type, required, ≤ 100 chars), `mode` (`on-post`, `every-15-minutes`, `nightly`). **Simulated**: no ERP is connected and nothing polls it; runs are submitted to `POST /api/v1/workflows/{key}/executions` exactly as with `api-trigger`, and the step passes the submitted data on. A workflow has one trigger: either input type. |
| `file-upload` ("File upload") | input | — → `out` | `accept` (accepted file types, required, ≤ 200 chars, e.g. `PDF, XML, CSV`), `maxSize` (`10mb`, `25mb`, `100mb`), `who` (`staff`, `portal-customers`, `anyone-with-link`). **Simulated**: there is no upload screen or portal yet; runs are submitted to `POST /api/v1/workflows/{key}/executions` and the step passes the submitted data on. |
| `sftp-folder` ("SFTP folder") | input | — → `out` | `host` (required, a plain host name such as `sftp.example.com`: no user name, password or URL), `folder` (required, starts with `/`), `pattern` (optional file pattern such as `*.xml`, no path parts), `poll` (`every-minute`, `every-5-minutes`, `hourly`). A new step has an empty host. **Simulated**: nothing connects to an SFTP server and no credentials are stored; runs are submitted to `POST /api/v1/workflows/{key}/executions` and the step passes the submitted data on. |
| `scheduled-trigger` ("Scheduled trigger") | input | — → `out` | `cron` (required, five fields: minute hour day-of-month month day-of-week, e.g. `0 1 * * *`; only letters, digits and `* / , ? # -`), `tz` (`Europe/Nicosia`, `UTC`, `Europe/Luxembourg`). **Simulated**: nothing in Verso runs on a schedule yet, so the expression is stored and checked but never fires; runs are submitted to `POST /api/v1/workflows/{key}/executions` and the step passes the submitted data on. |
| `folder-input` ("Folder input") | input | — → `out` | `sourcePath` (required: the folder to scan, typed as an absolute path as the **Worker's** server sees it: `D:\Invoices\in`, `\\fileserver\share\in` or `/mnt/verso/in`; at most 400 characters; no `.` or `..` parts, no wildcards), `pattern` (required, such as `*.json`; `*` and `?`, no path parts, not starting with `.`), `includeSubfolders` (boolean), `interval` (`every-minute`, `every-5-minutes`, `every-15-minutes`, `hourly`), `processedPath` and `failedPath` (required folders the files move to, typed the same way). **Real**: the Worker scans the folder and starts a run per file (JSON only in this version); see [Folder input](#folder-input). There are no credential settings: folders are reached as the Worker's service identity. Processed or failed files must not land where the trigger scans. Workflows saved when folders were chosen from approved locations keep working: they carry `sourceLocation`/`sourceSubfolder` (and the processed/failed equivalents) instead of a path, which the designer no longer shows; a typed path takes precedence. Runs can still also be submitted to `POST /api/v1/workflows/{key}/executions`. |
| `manual-trigger` ("Manual trigger") | input | — → `out` | `who` (`template-leads`, `credit-control`, `workspace`: who can start it), `ask` (optional, ≤ 100 chars: what the person is asked to enter, e.g. `Invoice number`). **Simulated**: there is no Run button yet and the group is not enforced; runs are submitted to `POST /api/v1/workflows/{key}/executions` and the step passes the submitted data on. |
| `validate` | process | `in` → `valid`, `invalid` (optional) | `required`: data paths; `types`: `[{ field, type }]` with `type` one of `text`, `number`, `date`, `email`, `eu-vat`, `iban`. At least one rule. |
| `generate-pdf` | doc | `in` → `success`, `failed` (optional; taken when the data can't make a complete document) | `templateId`; `templateVersion`: `latest-published` (resolved when a run is submitted) or a published version number such as `"2"`; `fileName` pattern such as `{{invoice.number}}.pdf` |
| `archive` | output | `in` → — | `folderPattern`; `retention`: `7y`, `10y` or `permanent`; `metadata`: `[{ key, value }]` (at most 20) |

`configSchema` is a JSON Schema subset (`type`, `properties`, `required`, `items`, `minItems`/`maxItems`, `minLength`/`maxLength`, `pattern`, `enum`, `format: uuid`) plus designer hints `x-widget` and `x-enumLabels`.

### Validation

```json
{ "valid": false,
  "issues": [
    { "severity": "error", "code": "template.not_published", "message": "'Commercial invoice' has no published version yet. …", "nodeId": "pdf", "edgeId": null, "port": null, "field": "templateVersion" },
    { "severity": "warning", "code": "port.unconnected", "message": "Invalid records will end the run as Rejected.", "nodeId": "validate", "edgeId": null, "port": "invalid", "field": null } ] }
```

| `code` | Severity | Meaning |
|---|---|---|
| `graph.no_trigger` / `graph.multiple_triggers` | error | A workflow has exactly one trigger |
| `node.unknown_type` | error | Not in the node catalog |
| `edge.unknown_node` / `edge.unknown_port` | error | The connection points to a missing node or port |
| `edge.duplicate_output` | error | An output already leads somewhere; each output has one next step |
| `graph.cycle` | error | Loops (including a node connected to itself) |
| `node.unreachable` | error | Not reachable from the trigger |
| `port.unconnected` | error / warning | A required output isn't connected (error); an optional one (`invalid`, `failed`) is a warning |
| `config.required` / `config.invalid` / `config.unknown` | error | Settings don't match the node's schema; `field` is the settings path (e.g. `metadata[0].key`) |
| `template.not_found` / `template.not_published` | error | The referenced template doesn't exist, or the chosen version (or, for `latest-published`, any version) isn't published. Drafts can't be used in workflows. |
| `folder.path_invalid` | error | A typed folder path isn't absolute, has `.` or `..` parts, wildcards or control characters, or is over 400 characters (the Worker checks the rest where the path lives, and shows a scan error such as "Folder unavailable" or "Access denied") |
| `folder.location_unknown` / `folder.location_disabled` / `folder.location_purpose` | error | Only for workflows saved with approved locations: the location isn't approved, is currently not available on the Worker, or isn't approved for this purpose (input for the source, output for processed/failed files) |
| `folder.subfolder_invalid` | error | Only for workflows saved with approved locations: a subfolder would leave its location |
| `folder.destination_scanned` | error | Processed or failed files would land in the scanned folder (or below it, with subfolders included) and be picked up again |

`valid` is true when there are no errors. Publishing a draft with errors returns **409 `workflow.invalid`** with the same `issues` array in the problem body.

## Executions

An execution is one run of a workflow's **latest published version** with one submitted payload. It records the exact workflow version and template versions it used. The Worker runs it; the API only stores and reads it.

| Method | Path | Success | Purpose |
|---|---|---|---|
| POST | `/api/v1/workflows/{key}/executions` | 202 `Submission`, `Location` (new) · 200 `Submission` (replay) | Submit data |
| GET | `/api/v1/executions` | 200 page of `ExecutionSummary` | History, newest first, with filters |
| GET | `/api/v1/executions/{id}` | 200 `Execution` | Status, steps, pinned versions, documents |
| GET | `/api/v1/executions/{id}/events?after=` | 200 `ExecutionEvent[]` | Event timeline and catch-up for live views |
| GET | `/api/v1/executions/{id}/input` | 200 `ExecutionInput` | The submitted payload. **Auditor or Admin only**, audited |
| POST | `/api/v1/executions/{id}/retry` | 202 `Execution`, `Location` | Retry a `Failed` execution from its failed step |

### History: `GET /api/v1/executions`

Query (all optional): `workflow` (workflow key), `status` (`Queued`, `Running`, `Succeeded`, `Rejected`, `Failed`; case-insensitive), `from` (inclusive) and `to` (exclusive) as ISO 8601 instants on the submission time, `correlationId` (exact), `page`, `pageSize` (max 100, default 25). Newest first. 400 `validation` for an unknown status or `to` not after `from`.

```json
{ "items": [ { "id": "…", "status": "Rejected", "workflowKey": "invoice-standard", "workflowVersion": 1, "correlationId": "…",
               "submittedAt": "…", "completedAt": "…", "attempt": 1, "failureCode": "data.invalid", "failureMessage": "The data failed 6 validation rule(s) …" } ],
  "page": 1, "pageSize": 25, "total": 1 }
```

### Events: `GET /api/v1/executions/{id}/events?after={eventId}`

Events of one execution with an id above `after` (default 0), oldest first, at most 500 (`limit`). Event ids increase strictly, so a client remembers the last id it saw and asks for the rest: after a reconnect, or whenever a notification arrives. Reading overlapping ranges is harmless; clients merge by id. 404 `execution.not_found`.

```json
[ { "id": 36, "executionId": "…", "nodeId": "pdf", "type": "StepStarted", "occurredAt": "…", "data": { "attempt": 1 } } ]
```

`type`: `Submitted`, `Started`, `StepStarted`, `StepSucceeded` (`data.port`), `StepFailed`, `RetryScheduled`, `Succeeded`, `Rejected`, `Failed`, `RetryRequested`, `Redispatched`. `data` is a small object (attempt, port, code); never payload values.

### Submitted data: `GET /api/v1/executions/{id}/input`

`{ "executionId": "…", "inputHash": "…", "data": { … } }`, `Cache-Control: no-store`. The payload can hold personal data, so it needs the **Auditor** or **Admin** role: otherwise **403 `forbidden`**, answered before the execution is looked up, so it doesn't reveal which ids exist. Every successful read is audited (`ExecutionInputViewed`, with the input hash, not the data).

### Submit: `POST /api/v1/workflows/{key}/executions`

```http
POST /api/v1/workflows/invoice-standard/executions
Idempotency-Key: 6f1c2f0e-0c55-4b1e-9f61-6a2b1d0d4c11
X-Correlation-Id: erp-batch-17          (optional)
Content-Type: application/json

{ "data": { "invoice": { "number": "INV-2026-003901", "date": "2026-11-02" }, "customer": { … } } }
```

```json
{ "executionId": "…", "status": "Queued", "workflowKey": "invoice-standard", "workflowVersion": 1, "correlationId": "erp-batch-17" }
```

- `Idempotency-Key` is required: 1–100 visible ASCII characters, unique per workflow. The same key with the same data returns **200** with the original execution (whatever its status now) and queues nothing. The same key with different data is **409 `execution.idempotency_conflict`**. "Same data" compares canonical JSON, so property order and whitespace don't matter.
- `data`: a JSON object, **synthetic data only**, at most 1,000,000 characters and 32 levels deep (request body cap about 1.1 MB). The payload is stored with the execution and never logged.
- `X-Correlation-Id` (optional, 1–64 of `A–Z a–z 0–9 . _ : -`) is stored and returned; without it one is generated.
- `generate-pdf` steps set to `latest-published` are pinned to the template's latest published version **now**; later publishes don't change a submitted run.
- Errors: 400 `execution.idempotency_key_required` / `validation`, 404 `workflow.not_found`, 409 `workflow.not_published` / `execution.idempotency_conflict`.

The execution row, its steps, the `Submitted` event and the audit entry are saved in one transaction, then the job is queued for the Worker. If queueing fails, the submission still succeeds: the Worker's dispatcher queues every execution that is still waiting after 2 minutes (checked every minute).

### Lifecycle

`Queued` → `Running` → `Succeeded` | `Rejected` | `Failed`; an operator retry moves `Failed` → `Queued`.

| Status | Meaning | Retry? |
|---|---|---|
| `Succeeded` | The path through the workflow completed | — |
| `Rejected` | The **data** is the problem: Validate left by `invalid`, or Generate PDF left by `failed` (a value the template needs is missing), with nothing connected after it. `failureCode` `data.invalid` / `document.data_incomplete`; the step's `result.issues` lists every problem. | No: submit corrected data with a new key |
| `Failed` | A **technical** problem (storage, Chromium, timeout) persisted through 3 attempts of a step | Yes: `POST …/retry` |

Each step is tried up to **3 times** for technical errors, 5 s and then 10 s apart; while a retry is pending the execution stays `Running` and the step shows `Failed` with its `error`. A run may take at most 5 minutes. Steps that already succeeded are never repeated (a retry or a Worker restart continues at the first unfinished step), and a document is generated at most once per step.

### Execution: `GET /api/v1/executions/{id}`

```json
{ "id": "…", "status": "Rejected",
  "workflow": { "id": "…", "key": "invoice-standard", "version": 1, "versionId": "…" },
  "templateVersions": { "pdf": "…template version id…" },
  "idempotencyKey": "6f1c…", "correlationId": "…", "inputHash": "…sha-256…",
  "submittedAt": "…", "submittedBy": "dev.user", "startedAt": "…", "completedAt": "…", "attempt": 1,
  "failureCode": "data.invalid", "failureMessage": "The data failed 2 validation rule(s) in step 'Validate invoice data'.",
  "steps": [
    { "nodeId": "trigger", "type": "api-trigger", "title": "API trigger", "sequence": 1, "status": "Succeeded", "outputPort": "out", "attempt": 1, "startedAt": "…", "completedAt": "…", "durationMs": 18, "error": null, "result": null },
    { "nodeId": "validate", "type": "validate", "title": "Validate invoice data", "sequence": 2, "status": "Succeeded", "outputPort": "invalid", "attempt": 1, "durationMs": 18, "error": null,
      "result": { "issues": [ { "path": "customer.name", "rule": "required", "message": "missing or empty" },
                              { "path": "invoice.date", "rule": "type:date", "message": "expected YYYY-MM-DD" } ] } },
    { "nodeId": "pdf", "type": "generate-pdf", "sequence": 3, "status": "Skipped", "…": "…" },
    { "nodeId": "archive", "type": "archive", "sequence": 4, "status": "Skipped", "…": "…" } ],
  "documents": [] }
```

- `steps`: one per workflow node, in run order. `status`: `Pending`, `Running`, `Succeeded`, `Failed`, `Skipped` (not on the path taken). `outputPort` is the output the step left by (`null` for Archive, which ends a path). `attempt` counts tries in the current execution attempt; `error` is the last technical error.
- `result` (per type): Validate `{ issues: [{ path, rule, message }] }`; Generate PDF `{ documentId, fileName, sizeBytes, sha256, warnings }` or, on `failed`, `{ issues: [{ code, path, elementId, message }] }`; Archive `{ documentId, folderPath, retentionUntil, warnings }`. Messages describe the expectation; they never repeat submitted values.
- `attempt` (execution): 1, plus one per operator retry.
- `documents`:

```json
{ "id": "…", "nodeId": "pdf", "templateVersionId": "…", "fileName": "INV-2026-003901.pdf", "contentType": "application/pdf",
  "sizeBytes": 21284, "sha256": "…", "status": "Archived", "createdAt": "…",
  "archive": { "archivedAt": "…", "folderPath": "invoices/2026/11", "retention": "7y", "retentionUntil": "2033-11-10T07:03:43+00:00",
               "metadata": { "InvoiceNumber": "INV-2026-003901", "Customer": "Kestrel Logistics Ltd" } } }
```

- `fileName` comes from the step's pattern: only letters, digits, space and `. _ ( ) -` are kept, and it always ends in `.pdf`.
- `folderPath` is the archive node's folder pattern, resolved and sanitized (no `..`, no drive letters). It is **archive metadata**, not a file-system path: storage keys are generated by Verso (`archive/yyyy/MM/{documentId}.pdf`).
- `retentionUntil` is `archivedAt` + 7 or 10 years, or `null` for `permanent`. Nothing is deleted at expiry in the MVP.
- The archived bytes are re-read and their SHA-256 compared with `sha256` before the archive record is written.

### Retry: `POST /api/v1/executions/{id}/retry`

No body. Only for `Failed` executions: the status becomes `Queued`, `attempt` increases, the failed step gets a fresh budget of 3 attempts, and the run continues from there. Returns 202 with the execution. Errors: 404 `execution.not_found`, 409 `execution.not_retryable`.

## Folder input

The `folder-input` trigger reads files from folders **on the Worker's side**. The Worker scans each published workflow whose latest version starts with this trigger, starts one run per file, and moves the file to the processed or failed folder. The API never touches folders: it lists what the Worker reported, queues access tests for the Worker, and shows the scan status. Design: [ADR 0001](adr/0001-folder-input.md). Server setup: [runbook B7](runbook.md#b7-folder-input-approved-locations).

| Method | Path | Success | Purpose |
|---|---|---|---|
| GET | `/api/v1/folder-locations` | 200 `FolderLocation[]` | Approved locations: `{ key, name, input, output, enabled, problem, updatedAt }`. Never a path. |
| POST | `/api/v1/folder-access-tests` | 202 `FolderAccessTest`, `Location` | "Test access": `{ targets: [{ role, path }] }`, 1–3 targets, `role` one of `source`, `processed`, `failed`, `path` a typed absolute path (or, for approved locations, `location` and `subfolder` instead). 400 `validation` for an invalid role or path (`targets[i].path`), an unknown location or an invalid subfolder. A typed path is recorded (see below). Audited (`FolderAccessTestRequested`). |
| GET | `/api/v1/folder-access-tests/{id}` | 200 `FolderAccessTest` | `status`: `Queued`, `Running`, `Completed` or `Failed` (couldn't run; see `error`). When completed, `results[]` has `{ role, location, subfolder, ok, checks: [{ name, ok, message }] }` plus `identity` and `machine`: the account and server the Worker checked as. Deleted after 24 h. |
| GET | `/api/v1/workflows/{id}/folder-input` | 200 `FolderInputStatus` | `watch`: the last scan, `{ nodeId, lastScanAt, lastStatus, lastError, lastFilesFound }` (`lastStatus`: `Ok`, `Unavailable`, `AccessDenied`, `Misconfigured`, `Error`; null before the first scan). `files`: the 25 most recent files, `{ id, fileName, relativePath, sizeBytes, status, outcome, errorCode, errorMessage, executionId, deliveredName, deliveryError, claimedAt, completedAt }`. |

### Typed folder paths

Workflow authors type the three folders into the step, so no administrator step is needed to use a new folder. The Worker has to know the paths (the workflow definition and the ingestion records only carry keys derived from them), so a path is **recorded when a workflow is published or a path is access-tested**, with its purpose (source: input; processed and failed: output), and audited as `FolderPathRegistered` (entity type `FolderPath`, with the path and who typed it). Recorded paths are never deleted, so unfinished files can always be resolved, also after a restart or a newer workflow version. The Worker validates every path as its own service identity. When `FolderInput:AllowedRoots` is set on the server, typed paths (and administrator-managed locations) must lie inside one of those folders; locations in the Worker's own configuration are not limited by it. Error messages and scan errors name the typed path.

### Folder locations administration

Locations can be set in two ways: in the Worker's configuration (`FolderInput:Locations`), or by an **Admin** in the web app (screen *Folder locations*), which stores the path in the database for the Worker to read. Either way the Worker validates the path as its own service identity, and designers only ever see key, name and purpose. Whoever may add a location decides what the Worker's account reads and writes, so these endpoints are **Admin only** and audited (`FolderLocationSaved`, `FolderLocationRemoved`, entity type `FolderLocation`, with the path). A server can limit administrator-managed paths to certain parents with `FolderInput:AllowedRoots` (a list of absolute folders); locations in the Worker's own configuration are not limited by it.

| Method | Path | Success | Purpose |
|---|---|---|---|
| GET | `/api/v1/admin/folder-locations` | 200 `AdminFolderLocation[]` | `{ key, name, path, input, output, managed, enabled, problem, updatedAt, updatedBy }`. `path` is null for locations set in the Worker's configuration (read-only here). `problem` is what the Worker reported, or "Waiting for the Worker…" until it has picked up a new or changed location (about 15 seconds). |
| PUT | `/api/v1/admin/folder-locations/{key}` | 200 `AdminFolderLocation` | Create or change `{ name, path, input, output }`. `key`: 2–64 lower-case letters, digits and `-`, starting with a letter; it never changes. `path`: absolute (`D:\…`, `\\server\share\…` or `/…`), at most 400 characters, no `.` or `..` parts, no wildcards, no control characters; no passwords. At least one of `input` and `output`. 400 `validation` names the field; 409 `folder_location.key_taken` for a key set in the Worker's configuration. |
| DELETE | `/api/v1/admin/folder-locations/{key}` | 204 | Remove a managed location. The Worker disables it ("Removed by an administrator."); workflows that use it stop scanning and fail validation. 409 `folder_location.in_use` while files of unfinished ingestions refer to it. Adding the key again brings it back. |

### How a file is handled


1. **Handoff.** Senders write under a temporary name and rename it when complete. Names starting with `.` or `~`, or ending with `.tmp`, `.temp`, `.part`, `.partial`, `.filepart`, `.crdownload`, `.lock` or `.download`, are ignored. A file must also be unchanged for `FolderInput:SettleTime` (10 s), and on Windows not open by its writer.
2. **Claim.** One Worker claims the file (a database row with a unique claim) and moves it into `<folder>/.verso-inflight/`. `status` is `Claimed`.
3. **Run.** The file must be a JSON object of at most 1 MB. It is submitted like `POST …/executions`. The idempotency key is derived from the data, so the same data never runs twice. The run is recorded as submitted by `verso.folder-input`, and its correlation id is the file's `id`. `status` is `Submitted`.
4. **Move.** When the run finishes (`status` `Delivering`, then `Completed`), the file goes to the processed folder if the run `Succeeded`, and to the failed folder otherwise, together with `<name>.error.json` (`code`, `message`, `executionId`). An existing file with the same name is never overwritten: the new one gets a suffix.

| `errorCode` | Meaning |
|---|---|
| `file.invalid_json` | Not a JSON object (XML and CSV aren't mapped yet) |
| `file.too_large` | Over `FolderInput:MaxFileBytes` (1 MB) |
| `file.duplicate` | The same data was already picked up; not run again |
| `file.missing` | The file was removed by someone else before Verso finished it |
| the run's `failureCode` (e.g. `data.invalid`) | The run was Rejected or Failed |
| a submission error (e.g. `workflow.template_unavailable`) | The run couldn't be started |

A Worker that stops mid-file is taken over after a 2-minute lease, and every step is safe to repeat. An unreachable destination shows as `deliveryError` and is retried on every pass.

## Documents (archive)

Archived documents only: a PDF that is generated but not yet archived isn't listed and can't be downloaded.

| Method | Path | Success | Purpose |
|---|---|---|---|
| GET | `/api/v1/documents` | 200 page of `ArchivedDocument` | Archive search, newest first |
| GET | `/api/v1/documents/{id}` | 200 `ArchivedDocument` | Metadata: hash, retention, source execution |
| GET | `/api/v1/documents/{id}/content` | 200 `application/pdf` | The PDF (verified, audited) |

**Search:** `q` (optional) matches the file name and the archive metadata **values** (invoice number, customer…; keys aren't searched), case-insensitively, as plain text: `%` and `_` are literal. `from` (inclusive) and `to` (exclusive) filter on the archive time; `page`, `pageSize` as usual.

```json
{ "id": "…", "executionId": "…", "nodeId": "pdf", "templateVersionId": "…", "fileName": "INV-2026-003901.pdf", "contentType": "application/pdf",
  "sizeBytes": 21284, "sha256": "d0b3…", "status": "Archived", "createdAt": "…", "archivedAt": "…",
  "folderPath": "invoices/2026/11", "retention": "7y", "retentionUntil": "2033-11-10T10:03:12+00:00",
  "metadata": { "InvoiceNumber": "INV-2026-003901", "Customer": "Kestrel Logistics Ltd" } }
```

**Content:** the API reads the archived file and checks its SHA-256 against the recorded one **before** sending anything. Headers: `Content-Disposition: attachment; filename=…`, `Cache-Control: no-store`, `X-Content-Type-Options: nosniff`. A missing file is 409 `document.content_missing`, a mismatch 409 `document.integrity_failed` (nothing is served and nothing is audited). A served download is audited as `DocumentDownloaded` (document, execution, file name, SHA-256). The API only reads the store; the Worker writes. Both must use the same `Documents:RootPath`.

## Audit

Auditor and Admin only.

### `GET /api/v1/audit?entityType=&entityId=&action=&actorId=&from=&to=&newestFirst=&page=1&pageSize=50`

Append-only log, oldest first unless `newestFirst=true` (`sequence` is the authoritative order). All filters are optional and exact: `entityType` (`Template`, `Workflow`, `Execution`, `Document`), `entityId`, `action`, `actorId`; `from` (inclusive) and `to` (exclusive) as ISO 8601 instants. 400 `validation` when `to` isn't after `from`.

`GET /api/v1/audit/actions` lists the action names that occur (for filters).

```json
{ "items": [
    { "id": "…", "sequence": 1, "occurredAt": "…", "actorId": "dev.template-author", "actorName": "Tara Templates",
      "action": "TemplatePublished", "entityType": "Template", "entityId": "…",
      "details": { "versionId": "…", "versionNumber": 1, "contentHash": "2fbc01dd…" },
      "traceId": "074fa088f220678c1d90f769aa9d54cc" } ],
  "page": 1, "pageSize": 50, "total": 1 }
```

| `action` | `details` |
|---|---|
| `TemplateCreated` | `{ key }` |
| `TemplateDraftSaved` | `{ versionId }` |
| `TemplateDraftCreated` | `{ versionId, fromVersion }` |
| `TemplatePublished` | `{ versionId, versionNumber, contentHash }` |
| `WorkflowCreated` | `{ key }` |
| `WorkflowDraftSaved` | `{ versionId }` |
| `WorkflowDraftCreated` | `{ versionId, fromVersion }` |
| `WorkflowPublished` | `{ versionId, versionNumber, contentHash }` |
| `ExecutionSubmitted` | `{ workflowKey, workflowVersion, inputHash, correlationId }` (never the payload) |
| `ExecutionRetryRequested` | `{ attempt }` |
| `ExecutionCompleted` | `{ status, failureCode, attempt }`; actor `verso.worker`, `traceId` = the execution's correlation id |
| `ExecutionInputViewed` | `{ inputHash }` (entity `Execution`; never the data) |
| `DocumentDownloaded` | `{ executionId, fileName, sha256 }` (entity `Document`) |

Rejected requests (validation, 401, 403, 409, 412, 428, 429) are not audited; they change nothing. `traceId` matches the `TraceId` in the API's structured logs.
