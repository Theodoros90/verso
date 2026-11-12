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
| 409 | `workflow.inactive` | Submission to a workflow that is deactivated |
| 409 | `execution.not_retryable` | Retry of an execution that isn't `Failed` (Rejected runs need corrected data, not a retry) |
| 404 | `folder_access_test.not_found` | Unknown access test id (or deleted after 24 hours) |

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
| GET | `/api/v1/workflows` | 200 page of `WorkflowSummary` | List of the active workflows, ordered by key. `?inactive=true` lists the deactivated ones instead |
| POST | `/api/v1/workflows` | 201 `Workflow`, `Location` | Create `{ key, name, description? }` |
| GET | `/api/v1/workflows/{id}` | 200 `Workflow` | Metadata |
| GET | `/api/v1/workflows/{id}/versions` | 200 `WorkflowVersionSummary[]` | History: draft first, then published newest first |
| GET | `/api/v1/workflows/{id}/versions/{versionId}` | 200 `WorkflowVersion` + `ETag` | One version |
| GET | `/api/v1/workflows/{id}/draft` | 200 `WorkflowVersion` + `ETag` | The draft |
| POST | `/api/v1/workflows/{id}/draft` | 201 `WorkflowVersion` + `ETag` | Start a draft from the latest published version |
| PUT | `/api/v1/workflows/{id}/draft` | 200 `WorkflowVersion` + new `ETag` | Save `{ definition }` (`If-Match`) |
| POST | `/api/v1/workflows/{id}/draft/validate` | 200 `WorkflowValidation` | Validate `{ definition }` (unsaved edits) or, without a body, the saved draft |
| POST | `/api/v1/workflows/{id}/draft/publish` | 200 `WorkflowVersion` (published) | Validate and freeze (`If-Match`) |
| POST | `/api/v1/workflows/{id}/deactivate` | 200 `Workflow` | Stop accepting new runs and the folder trigger's scanning, and move it to the deactivated list. Nothing is deleted; the key stays reserved; still editable |
| POST | `/api/v1/workflows/{id}/activate` | 200 `Workflow` | Accept runs again and return to the main list |

- `key`: 2–48 characters, lower-case letters, digits and single hyphens, starting with a letter (`invoice-standard`). Unique; never changes; used in the submission URL `POST /api/v1/workflows/{key}/executions` ([Executions](#executions)).
- Errors: 404 `workflow.not_found` / `workflow.version_not_found`, 409 `workflow.key_taken` / `workflow.no_draft` / `workflow.draft_exists` / `workflow.nothing_published` / `workflow.invalid`, 412 / 428 as for templates. Submitting to a deactivated workflow is 409 `workflow.inactive` ([Executions](#executions)).
- `WorkflowSummary` and `Workflow` carry `isActive`. Both state endpoints need the WorkflowAuthoring role, are audited (`WorkflowDeactivated`, `WorkflowActivated`) and are idempotent (repeating one changes nothing and writes no second entry). Runs already started carry on. See [ADR 0008](adr/0008-deactivate-workflows.md).

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
| `parse` ("Parse JSON / XML / CSV") | process | `in` → `success`, `failed` (optional; taken when the content can't be parsed, and the run then ends **Rejected** with `data.unparseable` unless the port is connected) | `source` (required, a data path, default `content`: the field of the submitted data that holds the file's content as text), `format` (`auto`, `json`, `xml`, `csv`; auto looks at the first character: `{`/`[` JSON, `<` XML, otherwise CSV), `encoding` (`utf-8`: the field is the text itself; `windows-1253` or `iso-8859-1`: the field is the file's bytes as base64), `recordPath` (optional dotted names down to the record, e.g. `invoices.invoice`), `numbers` (boolean, default true: plain decimals such as `4797.28` in XML/CSV become numbers; `007` or `12,5` stay text). **Real.** On success the run's data **is replaced** by the parsed record, so later steps (Validate, Generate PDF, file name and archive patterns) read the parsed fields; the submitted payload stays as submitted in the history. A run handles one record: a list is accepted only with exactly one entry (a CSV therefore needs a single data row), otherwise `parse.not_single`. XML: the root element's name is kept as the top-level property (use `recordPath` to step into it); child elements become properties, repeated names become an array (a single occurrence is an object, not a list), attributes become `@name`, text beside attributes `#text`, namespaces are dropped, DTDs are refused. CSV: the first row names the columns, the delimiter (`,` `;` tab `\|`) is detected, quoted fields may hold delimiters and line breaks. Limits: depth 32, 10 000 CSV rows. The step's result holds the detected `format`, or on failure `issues: [{ code, message }]` (`parse.no_content`, `parse.encoding`, `parse.invalid`, `parse.record_not_found`, `parse.not_single`, `parse.not_record`, `parse.too_deep`, `parse.too_large`); messages give a line, row or setting and never echo content. |
| `map-fields` ("Map fields") | process | `in` → `success`, `failed` (optional; taken when a value cannot be transformed, and the run then ends **Rejected** with `data.unmappable` unless the port is connected) | `rows`: a list (max 100) of `{ from, to, transform }`: `from` and `to` are dotted data paths, `transform` is `as-is`, `trim`, `uppercase`, `number` (a JSON number, or text holding a plain decimal such as `4797.28` or `12,5`; thousands separators are not guessed), `round2` (a number rounded to 2 decimals, midpoint away from zero) or `date` (reads `YYYY-MM-DD`, `dd/MM/yyyy`, `dd.MM.yyyy`, `dd-MM-yyyy`, `yyyy/MM/dd`, `yyyyMMdd` or an ISO date-time, and writes `YYYY-MM-DD`). **Real.** Sources are read from the data the step received (row order does not matter); the results are merged over that data: destination objects merge, other values are replaced, and fields no row mentions stay for later steps. A source that is not in the data is skipped. The step needs at least one row to publish; template fields without a row give a warning when the workflow is validated, not an error: the fields are those of the data model of the first template a Generate PDF step uses (its latest published version: every scalar field, and each list as one field), and nothing is checked while that template has no published version. The designer shows the same list from the template's latest version, draft included On failure the result holds `issues: [{ code: "map.transform", message, field: "rows[n]" }]` (or `map.not_record` when the data is not an object); messages name the row and transform and never echo values. |
| `transform` ("Transform data") | process | `in` → `success`, `failed` (optional; taken when a rule can't be applied, and the run then ends **Rejected** with `data.untransformable` unless the port is connected) | `rules`: a list (max 100) of `{ target, operation, a, b, text }`. `target` is a dotted data path the result is written to; `a` and `b` are operands, each a dotted data path or a plain number (`19`, `-0.5`); `text` is a pattern. `operation`: `copy` (A), `text` (builds a string from `text`, which may hold `{{field}}` and `{{field\|filter}}` tokens like file name patterns; a field with no value fails the rule), `add`, `subtract`, `multiply`, `divide` (A and B; a number may be JSON or text holding a plain decimal; decimal arithmetic), `round` (A to B decimals, 0 to 8, default 2, midpoint away from zero), `trim`, `uppercase`, `lowercase` (A; non-text values pass through unchanged), `date` (A, same formats as Map fields' `date`, written as `YYYY-MM-DD`), `default` (A, or `text` when A is missing, null or blank; a numeric `text` becomes a number) and `remove` (deletes the target; a missing target is fine). **Real**, and deliberately not the prototype's JSONata / JavaScript expression box: Verso never runs author-written code, so the prototype's free-form expression is replaced by this fixed set of rules. Rules run top to bottom and each sees what the rules above it wrote (so a total can be added up and then rounded); results are merged over the data like Map fields (destination objects merge, other values are replaced, fields no rule mentions stay). Unlike Map fields, a missing operand fails the rule (only `default` and `remove` accept a missing field). At least one rule is needed to publish, and each rule must have the settings its operation reads. On failure the result holds `issues: [{ code, message, field: "rules[n]" }]` with codes `transform.missing`, `transform.not_number`, `transform.not_date`, `transform.divide_by_zero`, `transform.decimals`, `transform.overflow`, `transform.too_long` (or `transform.not_record` when the data is not an object); messages name the rule and field path and never echo values. |
| `merge-data` ("Merge data") | process | `in` → `success`, `failed` (optional; taken when a record has no match under "stop" or the data can't be merged, and the run then ends **Rejected** with `data.unmergeable` unless the port is connected) | `key` (dotted path of the key in the record), `lookupKey` (dotted path of the key in each lookup record), `lookup` (a JSON list of 1 to 2000 objects typed into the step, max 200 000 characters; each key once; reference data only), `source` (optional dotted path of a list in the data: each entry is matched on its own and `key` is read from the entry; empty = the data itself is the one record), `into` (optional dotted path the matched row is written under, relative to the record; empty = its top level), `overwrite` (default off: values the record already has are kept and the lookup only fills gaps; on: lookup values replace them; objects merge either way), `strategy` (`keep` = leave unmatched records as they are, `drop` = remove unmatched list entries, `stop` = fail). Keys are compared as text: strings trimmed and case-sensitive, numbers as written. A single record with no match fails under `drop` and `stop`. **Real.** The prototype's second input port is not supported: the engine runs one path with one record, so the lookup is a table inside the step. The step result holds counts only (`matched`, `unmatched`, `dropped`); failure issues name the position, never a value. Rebuilt, not stored, when a run resumes. |
| `if-else` ("If / else") | logic | `in` → `yes`, `no` (both optional; a path with nothing connected ends the run there, as **Succeeded**, and the designer warns) | `conditions`: a list (1 to 10) of `{ join, field, op, compare, value }`. `field` is a dotted data path (the designer suggests the fields the data has at that step: the trigger's sample payload plus what earlier Map fields, Transform and Merge data steps add or remove, not the template's whole data model). `op`: `equals`, `not-equals`, `contains`, `not-contains`, `starts-with`, `ends-with`, `greater-than`, `greater-or-equal`, `less-than`, `less-or-equal`, `is-empty`, `is-not-empty`. `compare`: `value` (default; `value` is text typed into the step) or `field` (`value` is a dotted data path whose value in the data is compared with, suggested like `field`); `value` is required except for the two `is-…empty` operators. `join` (`and` default, `or`) joins a row to the one above and is ignored on the first row; AND is applied before OR (`A or B and C` is `A or (B and C)`); there are no groups. `yesLabel`, `noLabel` (optional names shown on the card and on the connection, max 40 characters). **Real.** The run continues by `yes` when the whole expression holds and by `no` otherwise. The data is not changed. Text is trimmed and compared without regard to case; the number operators compare numbers (a JSON number, or text holding a plain decimal such as `4797.28`) and are false when either side isn't a number, so a typed value must be a number to publish; a missing, null or blank field, an empty list and an empty object are *empty*; a missing field equals nothing and is "not equal" to everything; when the field to compare with is missing, every comparison is false except `not-equals` and `not-contains`. No expressions or scripts. Never fails. The step result is `{ matched: true \| false }`, no values. Not rebuilt when a run resumes: the path taken is stored. The prototype's per-branch preview and the Switch step (more than two paths) are not part of this step. |
| `switch` ("Switch") | logic | `in` → **one output per branch** (its `id`), then `other` when that is on. The catalog lists no outputs for it: they come from the step's settings (the designer and the graph validation both read them from there). All optional; an output with nothing connected ends the run there, as **Succeeded**, and the designer warns | `field` (dotted data path, suggested like If / else), `op`: `equals`, `not-equals`, `contains`, `not-contains`, `starts-with`, `ends-with`, `greater-than`, `greater-or-equal`, `less-than`, `less-or-equal` (the two emptiness operators are not offered); `branches`: a list (max 10) of `{ id, label, compare, value }`: `id` is the output's id (`^[a-z][a-z0-9-]{0,29}$`, unique, never `other`; the designer creates it and keeps it when the branch is renamed, so its connection stays), `label` its name (required, max 40), `compare` `value` (`value` is typed text) or `field` (`value` is a dotted data path whose value in the data is compared with); `other` (boolean). **Real.** The field is compared with each branch's value in order and the run leaves by the first branch that matches; none matching leaves by `other` when it is on, otherwise nowhere (the run ends at this step as Succeeded). Comparison rules are those of If / else (text trimmed and case-insensitive, numbers for the size operators, a missing field equals nothing). At least one branch, or `other`, is needed to publish; every branch needs a name and a value. Removing a branch (or turning `other` off) in the designer also removes the connection from that output, and the server refuses a connection from an output that doesn't exist (`edge.unknown_port`). Never fails. The step result is `{ branch: "<id>" \| null }`, never the record's value. Not rebuilt when a run resumes: the path taken is stored. |
| `for-each` ("For each") | logic | `in` → `each` (required), `empty` (optional; an empty list ends the run there as **Succeeded**, and the designer warns) | `field` (required, dotted data path of a list, such as `lines`), `mode` (`each` default, `split`), `item` (name, `^[A-Za-z_][A-Za-z0-9_]{0,39}$`, default `item`), `eachLabel`, `emptyLabel` (optional names of the two paths, max 40; defaults "For each item" / "No items"). **Real.** The steps after `each` (the *loop*: everything reachable from that output) run once per item of the list. Mode `each` keeps the whole record and adds the item to it under the item name (`item.description`; a field of the record with the same name is replaced while the item runs); mode `split` makes each item its own record for the loop (an object item is the record; any other item becomes `{ "<item>": value }`). A single value that isn't a list counts as a list of one; a missing, null or blank field is an empty list; at most 200 items (more fails the step with `foreach.too_many_items`, not retryable). The data after the loop is the data before it. An item ends where its path ends (an output with nothing connected, or Archive), or where one step of the loop connects **back into the For each's input** (the designer draws it dashed and labelled "repeat"). That is the only cycle a workflow may have: the connecting step can't be a For each, If / else or Switch (`loop.back_edge_source`), a For each has one such connection (`loop.multiple_back_edges`), a For each can't be inside another's loop (`loop.nested`), and a step of the loop can't also be reached from outside it (`loop.shared_step`). There is no output for "all items done": when the last item finishes the run ends as **Succeeded**. Items run one after another; the first item that is **Rejected** (for example a document that can't be completed) or **Failed** ends the whole run, and an operator retry resumes in that item, keeping items already finished. Each item has its own step rows (`iteration` 1…n, see Executions) and its own documents. The step result is `{ count }`. |
| `approval` ("Approval") | logic | `in` → `approved` (required), `rejected` ("Declined"; optional: with nothing connected a declined approval ends the run as **Rejected**, `approval.declined`) | `approver` (`operator` default, `workflow-author`, `template-author`, `auditor`: anyone holding that Verso role may decide, and Admin always), `escalateAfter` (`24h`, `48h` default, `5d`: after this the request shows as overdue; nothing is sent or reassigned). **Real.** The run stops at the step: the step is `Waiting`, the execution `AwaitingApproval`, and `ApprovalRequested` is recorded. A decision (`POST /api/v1/executions/{id}/approvals/{nodeId}`) leaves by `approved` or `rejected` and queues the run to continue. The step result holds `approver`, `escalateAfter`, `requestedAt`, `dueAt`, and after a decision `decision`, `decidedBy`, `decidedByName`, `decidedAt` and the `comment`. See ADR 0002. |
| `validate` | process | `in` → `valid`, `invalid` (optional) | `required`: data paths; `types`: `[{ field, type }]` with `type` one of `text`, `number`, `date`, `email`, `eu-vat`, `iban`. At least one rule. |
| `generate-pdf` | doc | `in` → `success`, `failed` (optional; taken when the data can't make a complete document) | `templateId`; `templateVersion`: `latest-published` (resolved when a run is submitted) or a published version number such as `"2"`; `fileName` pattern such as `{{invoice.number}}.pdf` |
| `generate-docx` | doc | `in` → `success`, `failed` (optional; same as `generate-pdf`: the run ends **Rejected** with `document.data_incomplete` unless connected) | `templateId`, `templateVersion` and `fileName` as for `generate-pdf` (default `{{invoice.number}}.docx`); `protect`: `allow` (default), `trackedChanges` or `readOnly` (Word's editing restriction; no password, so advisory). **Real** (ADR 0003): the Worker writes a Word file from the template layout without Chromium; the document's `contentType` is `application/vnd.openxmlformats-officedocument.wordprocessingml.document` and Archive and the download endpoint treat it like a PDF. |
| `generate-email` | doc | `in` → `success`, `failed` (optional; the run ends **Rejected** with `document.data_incomplete` unless connected) | No template. `layout`: `invoice` (default), `statement` or `reminder` (heading label and accent colour); `subject` (one line), `preheader`, `body` (blank line = new paragraph), `buttonLabel` and `buttonUrl` (both or neither; the link must resolve to http, https or mailto), `footer`, `fileName` (default `{{invoice.number}}.html`), `darkMode`. Every text may hold `{{path|filter}}` fields. **Real** (ADR 0004): the Worker writes one self-contained `.html` file (`contentType` `text/html`, `templateVersionId` null); Archive treats it like a PDF and the download endpoint serves it as an attachment. Nothing is sent. |
| `combine-pdfs` | doc | `in` → `success`, `failed` (optional; fewer than two PDFs ends the run **Rejected** with `combine.not_enough_documents` unless connected) | No template. `order`: `generated` (default, in the order the PDFs were made) or `reverse`; `bookmarks` (default on: one per combined file, named after it); `fileName` (default `{{invoice.number}}-pack.pdf`). **Real** (ADR 0005): the Worker joins the PDFs the run generated earlier (same execution and, in a For each loop, the same item; Word and email files and this step's own output are ignored), each checked against its stored SHA-256, into one new PDF (`templateVersionId` null) that Archive stores. Result: `{ documentId, fileName, sizeBytes, sha256, documents, pages, warnings }`. |
| `send-email` | output | `in` → `sent`, `failed` (optional; a missing or invalid recipient or an empty subject ends the run **Rejected** with `email.data_incomplete` unless connected) | `to` (addresses separated by commas or semicolons, or fields such as `{{customer.email}}`; at most 10), `subject`, `body` (plain text), `attach`: `none` or `document` (default). **Real** (ADR 0006): the Worker sends through the SMTP relay in its `Email` configuration (docs/runbook.md B8); with `attach: document` the run's current document is attached (up to 10 MB, SHA-256 checked), or used as the HTML body when it is a Generate HTML email file. Without relay configuration the run **Fails** with `email.not_configured`; an unreachable relay is retried; a refused message or a recipient outside `Email:AllowedRecipientDomains` is not. Result: `{ recipients, attachment, htmlBody, messageId, warnings }` (no addresses or text). Delivery is at-least-once. |
| `print-queue` | output | `in` → `queued` | `queue` (key of a print queue in the Worker's `Print:Queues`, such as `cyprus-print-nightly`; lower-case letters, digits, hyphens), `envelope`: `dl-window` (default) or `c4`, `duplex` (default on). **Real** (ADR 0007): the Worker writes the run's current PDF and a `{jobId}.json` job file (queue, envelope, duplex, file name, size, SHA-256) into that queue's spool folder, which the print system picks up (docs/runbook.md B9); the folder path is configured by the administrator, never typed in a workflow. An unknown queue key **Fails** the run with `print.queue_unknown`; an unwritable folder is retried; a non-PDF document fails with `print.not_pdf`. Result: `{ jobId, queue, fileName, sizeBytes, envelope, duplex }`. `queued` means the files are in the folder, not that anything was printed. |
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
| `graph.cycle` | error | Loops (including a node connected to itself); the one loop a For each allows is not a cycle |
| `loop.back_edge_source` / `loop.multiple_back_edges` / `loop.nested` / `loop.shared_step` | error | A For each's loop breaks its rules (see the `for-each` step) |
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
| POST | `/api/v1/executions/{id}/approvals/{nodeId}` | 202 `Execution`, `Location` | Approve or decline the waiting Approval step `nodeId`. Body `{ "decision": "approved" | "declined", "comment"?: string (max 500), "iteration"?: int (the For each item; default 0) }`. `403` unless the user holds the step's approver role or is Admin; `409 approval.not_waiting` when it is not (or no longer) waiting; `404 approval.not_found` for an unknown step. Audited as `ExecutionApprovalDecided` (without the comment) |

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
- `generate-pdf` and `generate-docx` steps (an HTML email step has no template) set to `latest-published` are pinned to the template's latest published version **now**; later publishes don't change a submitted run.
- Errors: 400 `execution.idempotency_key_required` / `validation`, 404 `workflow.not_found`, 409 `workflow.not_published` / `workflow.inactive` / `execution.idempotency_conflict`.

The execution row, its steps, the `Submitted` event and the audit entry are saved in one transaction, then the job is queued for the Worker. If queueing fails, the submission still succeeds: the Worker's dispatcher queues every execution that is still waiting after 2 minutes (checked every minute).

### Lifecycle

`Queued` → `Running` → `Succeeded` | `Rejected` | `Failed`; an operator retry moves `Failed` → `Queued`. An Approval step moves `Running` → `AwaitingApproval` (no Worker holds the run) and a decision moves it back to `Queued` (ADR 0002).

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
    { "nodeId": "trigger", "type": "api-trigger", "title": "API trigger", "sequence": 1, "status": "Succeeded", "outputPort": "out", "attempt": 1, "startedAt": "…", "completedAt": "…", "durationMs": 18, "error": null, "result": null, "iteration": 0 },
    { "nodeId": "validate", "type": "validate", "title": "Validate invoice data", "sequence": 2, "status": "Succeeded", "outputPort": "invalid", "attempt": 1, "durationMs": 18, "error": null,
      "result": { "issues": [ { "path": "customer.name", "rule": "required", "message": "missing or empty" },
                              { "path": "invoice.date", "rule": "type:date", "message": "expected YYYY-MM-DD" } ] } },
    { "nodeId": "pdf", "type": "generate-pdf", "sequence": 3, "status": "Skipped", "…": "…" },
    { "nodeId": "archive", "type": "archive", "sequence": 4, "status": "Skipped", "…": "…" } ],
  "documents": [] }
```

- `steps`: one per workflow node, in run order; a step inside a For each loop has one row per item, appended as the items run. `iteration` is 0 for a step outside a loop and the item number (1, 2, …) inside one; the rows of item 1 exist from submission, later items add theirs. Step events carry `iteration` in their data when it is above 0. `status`: `Pending`, `Running`, `Waiting` (an Approval step asking for a decision), `Succeeded`, `Failed`, `Skipped` (not on the path taken). `outputPort` is the output the step left by (`null` for Archive, which ends a path). `attempt` counts tries in the current execution attempt; `error` is the last technical error.
- `result` (per type): Validate `{ issues: [{ path, rule, message }] }`; Generate PDF / Generate DOCX / Generate HTML email `{ documentId, fileName, sizeBytes, sha256, warnings }` or, on `failed`, `{ issues: [{ code, path, elementId, message }] }`; Archive `{ documentId, folderPath, retentionUntil, warnings }`. Messages describe the expectation; they never repeat submitted values.
- `attempt` (execution): 1, plus one per operator retry.
- `documents`:

```json
{ "id": "…", "nodeId": "pdf", "templateVersionId": "…", "fileName": "INV-2026-003901.pdf", "contentType": "application/pdf",
  "sizeBytes": 21284, "sha256": "…", "status": "Archived", "createdAt": "…", "iteration": 0,
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

Workflow authors type the three folders into the step, so no administrator step is needed to use a new folder. The Worker has to know the paths (the workflow definition and the ingestion records only carry keys derived from them), so a path is **recorded when a workflow is published or a path is access-tested**, with its purpose (source: input; processed and failed: output), and audited as `FolderPathRegistered` (entity type `FolderPath`, with the path and who typed it). Recorded paths are never deleted, so unfinished files can always be resolved, also after a restart or a newer workflow version. The Worker validates every path as its own service identity. When `FolderInput:AllowedRoots` is set on the server, typed paths must lie inside one of those folders; locations in the Worker's own configuration are not limited by it. Error messages and scan errors name the typed path.

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
| `ExecutionApprovalRequested` | `{ nodeId, step, approver, iteration }` (by the Worker) |
| `ExecutionApprovalDecided` | `{ nodeId, decision, approver, iteration, hasComment }` |
| `ExecutionCompleted` | `{ status, failureCode, attempt }`; actor `verso.worker`, `traceId` = the execution's correlation id |
| `ExecutionInputViewed` | `{ inputHash }` (entity `Execution`; never the data) |
| `DocumentDownloaded` | `{ executionId, fileName, sha256 }` (entity `Document`) |

Rejected requests (validation, 401, 403, 409, 412, 428, 429) are not audited; they change nothing. `traceId` matches the `TraceId` in the API's structured logs.
