# Verso API

Endpoints that exist today. Planned contracts are in [plan §5](plan/2026-11-09-verso-mvp-plan.md#5-api-contracts).

- The machine-readable contract is [openapi/v1.json](openapi/v1.json), also served live at `/openapi/v1.json`. A test fails when the API and that file drift apart. After an intended change, regenerate it with `$env:VERSO_UPDATE_OPENAPI = "1"; dotnet test --filter OpenApiDocument`, review the diff, and update this page in the same change.
- **No authentication yet.** Every request acts as the development user (`DevUser:Id` / `DevUser:Name`, default `dev.user`). The API refuses to start outside the Development and Testing environments until Entra ID sign-in exists.

## Conventions

| Topic | Rule |
|---|---|
| Base path | `/api/v1` (health: `/api/health`) |
| Format | JSON, camelCase. Timestamps are ISO 8601 with offset (UTC). Ids are GUIDs. |
| Errors | RFC 9457 `application/problem+json` with a stable `code` and a `traceId`. Validation errors (400) add `errors: { "<json path>": ["message"] }`. |
| Concurrency | Reads of a template version return an `ETag` header. Changing the draft (`PUT …/draft`, `POST …/draft/publish`) requires `If-Match: <that ETag>`. Missing or `*` → 428; stale → 412. |
| Paging | `?page=1&pageSize=25` (max 100) → `{ items, page, pageSize, total }` |

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

- `html` is a self-contained document (inline CSS, embedded images, a CSP that blocks scripts and network access) showing one continuous A4 sheet. Unresolved values appear as dashed `{{path}}` placeholders. Display it in a sandboxed `<iframe srcdoc>`. Real page breaks are visible in the PDF (M5).
- `issues`: codes and meanings are in [layout-model.md](layout-model.md#issue-codes). An empty list means every binding resolved.
- Errors: 400 `validation` (bad `data`), 404 `template.not_found`, 409 `template.no_draft`, 409 `template.layout_invalid` (only for drafts saved before layout validation existed; re-save to fix).

### Summaries

`TemplateSummary`: `{ id, key, name, documentType, latestPublishedVersion, hasDraft, updatedAt }`

`TemplateVersionSummary`: `{ versionId, versionNumber, status, contentHash, updatedAt, updatedBy, publishedAt, publishedBy }`

## Audit

### `GET /api/v1/audit?entityType=Template&entityId={id}&page=1&pageSize=50`

Append-only log, oldest first (`sequence` is the authoritative order). Both filters are optional.

```json
{ "items": [
    { "id": "…", "sequence": 1, "occurredAt": "…", "actorId": "dev.user", "actorName": "Development User",
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

Rejected requests (validation, 409, 412, 428) are not audited; they change nothing. `traceId` matches the `TraceId` in the API's structured logs.
