# Verso — MVP architecture and delivery plan

Status: **Approved 2026-11-09.** Version, hosting and identity questions answered the same day (§10). Working rules for implementation live in `CLAUDE.md` at the repo root.

Date: 2026-11-09

---

## 1. What the design reference tells us

`design-reference/Verso prototype v5 s.html` is a single-file, no-backend prototype. What carries over as **visual and product direction**:

| Area | Carry over | Do not carry over |
|---|---|---|
| Visual language | Color tokens (`--ink`, `--cyan`, `--magenta`, status colors, category colors for workflow nodes), light/dark themes, Schibsted Grotesk / Source Serif 4 / IBM Plex Mono, Lucide icons, dense 14px base | Google Fonts / jsDelivr CDN loading (bank environment: fonts and icons get bundled locally) |
| Navigation | Left sidebar: Overview, Template designer, Workflow designer, Document preview, Jobs, Archive, Audit | Assistant (AI) and Integrations screens: deferred |
| Template designer | A4 sheet canvas, element palette (text, data field, repeating table, image, QR, barcode, divider, box, signature, conditional block), properties panel, element tree, "show sample data" toggle, `{{binding}}` chips, "unmapped" warnings | Absolute-positioned-everything layout as the storage model (see D4), string-built HTML |
| Workflow designer | Node categories (Input, Processing, Logic, Document, AI, Output), node card design, typed output ports (`valid`/`invalid`, `success`/`failed`), per-node config panels, "incomplete/configured" node status, test run with step timeline | Client-side simulation as the source of truth, the 30-node catalog |
| Data conventions | `{{path}}` and `{{path\|filter}}` tokens with a small filter set (`date`, `money`, `year`, `month`, `upper`…), Validate step = required fields + typed checks (Date, Number, Email, EU VAT, IBAN), archive folder pattern + metadata key/values + retention | JS-eval transforms, JSONata (deferred) |
| Sample data | Synthetic issuer/customer/invoice shape (`invoice`, `customer`, `lines[]`, `totals`, `payment`), plus a deliberately broken payload for validation testing | — |

## 2. Architecture overview

```
 Browser (React SPA)
     │ HTTPS: REST + SignalR
     ▼
┌──────────────────────┐          ┌──────────────────────────┐
│  Verso.Api (ASP.NET) │          │  Verso.Worker (.NET host)│
│  - REST controllers  │          │  - Hangfire server        │
│  - SignalR hub       │          │  - Execution engine       │
│  - Event relay       │          │  - Playwright + Chromium  │
│  - Hangfire client   │          │  - Storage writer         │
└─────────┬────────────┘          └───────────┬──────────────┘
          │  EF Core                          │  EF Core
          ▼                                   ▼
     ┌────────────────────── SQL Server ───────────────────────┐
     │ schemas: templates · workflows · executions · documents │
     │          audit · HangFire                               │
     └─────────────────────────────────────────────────────────┘
                                   │
                      IDocumentStore (configurable)
                      MVP: local/network file system
```

Principles:

- **Modular monolith.** One codebase, layered projects with module folders (§3); two deployable processes (API, Worker) that share the same code.
- **The API never runs Chromium.** All PDF rendering happens in the Worker. The API can render HTML previews (pure C#, no browser).
- **Workflow and template designers are separate** screens, modules and data models. A workflow references a template *version*; it never embeds template content.
- **Everything executed is pinned and immutable**: an execution records the exact workflow version and template version it ran with.
- **No real outbound integrations.** Email/print/ERP/SFTP nodes are not in the MVP catalog. Archive writes to the configured local store only.
- **AI is deferred.** No AI nodes or Assistant screen. The node catalog is extensible so they can be added later without schema changes.

## 3. Repository structure

Revised 2026-11-09 at M1: the user chose a lighter layered structure instead of one project per module. Modules (Templates, Workflows, Executions, Documents, Audit) are **folders inside the layered projects**, not separate projects. Further projects (e.g. `Verso.Rendering`) are added only when a milestone needs the separation.

```
verso/
├─ Verso.slnx
├─ Directory.Build.props          # net10.0, nullable, warnings-as-errors, analyzers
├─ Directory.Packages.props       # central, pinned NuGet versions (+ security pins)
├─ dotnet-tools.json              # repo-local dotnet-ef
├─ design-reference/              # prototype, read-only reference
├─ docs/
│  ├─ plan/                       # this document
│  ├─ adr/                        # architecture decisions (as they arise)
│  ├─ setup.md                    # local setup and verification
│  └─ api.md                      # implemented endpoints
├─ src/
│  ├─ Verso.Api/                  # composition root: HTTP endpoints, (later) auth, SignalR, OpenAPI
│  ├─ Verso.Worker/               # composition root: (later) Hangfire server, engine, Playwright
│  ├─ Verso.Application/          # use cases, per-module folders (Templates/, Workflows/, …)
│  ├─ Verso.Domain/               # entities and invariants, per-module folders
│  ├─ Verso.Infrastructure/       # EF Core DbContext, configurations, migrations, storage, health checks
│  └─ verso-web/                  # React + Vite SPA
│     └─ src/
│        ├─ app/                  # router, layout shell, navigation, theme
│        ├─ components/ui/        # shadcn/ui components (owned source)
│        ├─ features/             # health/, overview/, later templates/, workflows/, executions/, archive/
│        ├─ lib/api/              # API client (generated OpenAPI types once real resources exist)
│        └─ styles/index.css      # design tokens ported from the prototype
├─ tests/
│  └─ Verso.Api.Tests/            # API + real SQL Server (LocalDB) integration tests
│     (later: Verso.Domain.Tests, Verso.Rendering.Tests, fixtures/ with synthetic invoices)
└─ tools/synthetic-data/          # (later) synthetic invoice generator — no real data, ever
```

Dependency rules:

- `Domain` → nothing. `Application` → `Domain`. `Infrastructure` → `Application`. `Api` and `Worker` → `Application` + `Infrastructure`.
- Module folders don't reach into each other's internals; cross-module calls go through an interface in `Application`.
- One `VersoDbContext`, but each module's tables live in its own SQL schema. No cross-schema foreign keys; references across modules are by id.
- Rendering code has no dependency on EF, ASP.NET, or Playwright. Only the Worker references the Playwright adapter.

## 4. Main entities

All ids are GUIDs (sequential for clustered-index friendliness). All timestamps are UTC `datetimeoffset`. User identity comes from the IdP; we store a subject id and display name, not a user table.

### Templates module (`templates` schema)

**Template**
- `Id`, `Key` (unique, e.g. `INV-STD`), `Name`, `Description`, `DocumentType` (`Invoice` for MVP)
- `CreatedAt/By`, `RowVersion`

**TemplateVersion**
- `Id`, `TemplateId`, `VersionNumber` (int, assigned on publish; draft has none)
- `Status`: `Draft` | `Published` | `Retired`
- `Layout` (JSON, versioned schema `layoutSchemaVersion`) — pages, regions, elements, bindings
- `DataSchema` (JSON Schema describing the data the template expects)
- `SampleData` (JSON, synthetic, used by the designer and previews)
- `PageSettings` (size A4, margins, orientation)
- `ContentHash` (SHA-256 of canonicalized layout + schema; set on publish)
- `PublishedAt/By`, `RowVersion`
- Invariants: at most one `Draft` per template; `Published` versions are never modified.

### Workflows module (`workflows` schema)

**Workflow**
- `Id`, `Key` (unique, used in the submit URL, e.g. `invoice-standard`), `Name`, `Description`, `CreatedAt/By`, `RowVersion`

**WorkflowVersion**
- `Id`, `WorkflowId`, `VersionNumber`, `Status` (`Draft` | `Published` | `Retired`)
- `Definition` (JSON): `{ nodes: [{ id, type, title, position, config }], edges: [{ id, source, sourcePort, target, targetPort }] }` — same shape as the prototype, and directly mappable to React Flow
- `ContentHash`, `PublishedAt/By`, `RowVersion`
- Invariants: a published definition passed server-side graph validation (§5.4); a `generate-pdf` node references a **specific published template version**, or `latest-published`, which is resolved to a specific version at submission time.

**Node catalog** is code, not data: each node type declares its category, ports, config JSON Schema, and a handler. Exposed read-only via the API so the designer renders config panels from it.

MVP node types: `api-trigger`, `validate`, `generate-pdf`, `archive`. Added after the MVP: `manual-trigger` (simulated), `folder-input` (real, ADR 0001) and `parse` (real: JSON/XML/CSV text in a field of the submitted data becomes the run's data; see docs/api.md). `map-fields` (real: copies and transforms fields, see docs/api.md) and `transform` (real: calculates and reshapes values with an ordered list of fixed rules; the prototype's JSONata expression box is replaced because Verso runs no author-written code; see docs/api.md) and `merge-data` (real: joins the data with a lookup table typed into the step on a key; the prototype's second input port is not supported, see docs/api.md) and `if-else` (real: sends the run down a yes or no path by comparing one field with a typed value, fixed operators and no expressions; see docs/api.md). Planned next: `switch`.

### Executions module (`executions` schema)

**Execution**
- `Id`, `WorkflowVersionId`, `ResolvedTemplateVersionIds` (JSON map nodeId → templateVersionId)
- `Status`: `Queued` | `Running` | `Succeeded` | `Rejected` | `Failed` (see §6)
- `IdempotencyKey` (unique per workflow), `CorrelationId`
- `InputPayload` (JSON, size-limited), `InputHash`
- `SubmittedAt/By`, `StartedAt`, `CompletedAt`, `Attempt`
- `FailureCode`, `FailureMessage` (sanitized, no payload content)

**ExecutionStep**
- `Id`, `ExecutionId`, `NodeId`, `NodeType`, `Sequence`
- `Status`: `Pending` | `Running` | `Succeeded` | `Failed` | `Skipped`
- `OutputPort` (which port it exited by: `valid`, `invalid`, `success`, `failed`)
- `Attempt`, `StartedAt`, `CompletedAt`, `DurationMs`
- `Result` (JSON summary, e.g. validation issues list or document id — no full payloads)

**ExecutionEvent** (append-only; drives live progress and the history timeline)
- `Id` (bigint identity — monotonic watermark), `ExecutionId`, `StepId?`, `Type`, `OccurredAt`, `Data` (JSON)

### Documents module (`documents` schema)

**Document**
- `Id`, `ExecutionId`, `StepId`, `TemplateVersionId`
- `FileName` (from the node's filename pattern, sanitized), `ContentType`, `SizeBytes`, `Sha256`
- `StorageProvider`, `StorageKey`
- `Status`: `Generated` | `Archived`
- `CreatedAt`

**ArchiveRecord** (write-once)
- `Id`, `DocumentId` (unique), `ArchivedAt`, `RetentionUntil`, `FolderPath` (resolved pattern), `Metadata` (JSON key/values from the node config, e.g. `InvoiceNumber`, `Customer`, `Total`)
- DB-enforced: no UPDATE/DELETE permission for the application login on this table.

### Audit module (`audit` schema)

**AuditEntry** (append-only)
- `Id`, `OccurredAt`, `ActorId`, `ActorName`, `Action` (`TemplateDraftSaved`, `TemplatePublished`, `WorkflowPublished`, `ExecutionSubmitted`, `DocumentDownloaded`, …), `EntityType`, `EntityId`, `Details` (JSON), `CorrelationId`

## 5. API contracts

Conventions:
- Base path `/api/v1`. JSON, camelCase. OpenAPI document generated from the API and checked in; frontend types are generated from it.
- Errors: RFC 9457 `application/problem+json`, with `errors` for field-level validation and a stable `code`.
- Optimistic concurrency: `ETag` on GET, `If-Match` required on PUT/publish; `412` on mismatch.
- Pagination: `?page=&pageSize=` (max 100), response `{ items, page, pageSize, total }`.

### 5.1 Templates

| Method | Path | Purpose |
|---|---|---|
| GET | `/templates` | List templates with latest published version + draft indicator |
| POST | `/templates` | Create template (creates empty draft) |
| GET | `/templates/{id}` | Template metadata |
| GET | `/templates/{id}/versions` | Version history |
| GET | `/templates/{id}/versions/{versionId}` | One version (layout, schema, sample) |
| GET | `/templates/{id}/draft` | Current draft |
| PUT | `/templates/{id}/draft` | Save draft (`If-Match`) |
| POST | `/templates/{id}/draft/preview/html` | Render draft + sample (or supplied) data to HTML (API-side, no Chromium) |
| POST | `/templates/{id}/draft/preview/pdf` | Queue a PDF preview job → `202 { previewId }`; result via SignalR or `GET /previews/{previewId}` |
| POST | `/templates/{id}/draft/publish` | Validate + freeze draft as next version (`If-Match`) |
| POST | `/templates/{id}/draft` | Start a new draft from the latest published version (added in M2) |

Draft save body (abridged):

```json
{
  "name": "Commercial invoice",
  "pageSettings": { "size": "A4", "orientation": "portrait", "marginsMm": [18, 16, 18, 16] },
  "layout": {
    "layoutSchemaVersion": 1,
    "regions": {
      "header": { "heightMm": 70, "elements": [ { "id": "e1", "type": "field", "x": 0, "y": 30, "w": 220, "h": 20,
                    "props": { "binding": "invoice.number", "label": "Invoice no.", "format": "text" } } ] },
      "body":   { "flow": [ { "id": "t1", "type": "table", "props": { "binding": "lines", "columns": ["description","qty","unitPrice","amount"] } } ] },
      "footer": { "heightMm": 40, "elements": [] }
    }
  },
  "dataSchema": { "type": "object", "required": ["invoice","customer","lines","totals"], "properties": { } },
  "sampleData": { "invoice": { "number": "INV-2026-003901" } }
}
```

### 5.2 Workflows

| Method | Path | Purpose |
|---|---|---|
| GET | `/node-types` | Node catalog: type, category, ports, config JSON Schema, defaults |
| GET | `/workflows` | List |
| POST | `/workflows` | Create (empty draft containing an `api-trigger` node) |
| GET | `/workflows/{id}` · `/workflows/{id}/versions` · `/workflows/{id}/draft` | Read |
| PUT | `/workflows/{id}/draft` | Save draft definition (`If-Match`) |
| POST | `/workflows/{id}/draft/validate` | Graph + config validation → list of issues per node |
| POST | `/workflows/{id}/draft/publish` | Validate + freeze (`If-Match`) |

Validation response:

```json
{ "valid": false,
  "issues": [
    { "nodeId": "n3", "severity": "error", "code": "config.required", "message": "Template is required" },
    { "nodeId": "n2", "port": "invalid", "severity": "warning", "code": "port.unconnected", "message": "Invalid records will end the run as Rejected" }
  ] }
```

### 5.3 Executions

| Method | Path | Purpose |
|---|---|---|
| POST | `/workflows/{key}/executions` | Submit synthetic invoice JSON to the latest **published** version |
| GET | `/executions` | History: filter by workflow, status, date range, correlation id |
| GET | `/executions/{id}` | Detail incl. steps, resolved versions, documents |
| GET | `/executions/{id}/events?after={eventId}` | Event timeline (catch-up for live view) |
| GET | `/executions/{id}/input` | Submitted payload (restricted role) |
| POST | `/executions/{id}/retry` | Retry a `Failed` execution from the failed step (not allowed for `Rejected`) |

Submit:

```http
POST /api/v1/workflows/invoice-standard/executions
Idempotency-Key: 6f1c2f0e-0c55-4b1e-9f61-6a2b1d0d4c11
Content-Type: application/json

{ "data": { "invoice": { "number": "INV-2026-003901", "date": "2026-11-02", "...": "..." } } }
```

```http
202 Accepted
Location: /api/v1/executions/0193a6c2-...

{ "executionId": "0193a6c2-...", "status": "Queued", "workflowVersion": 3, "correlationId": "..." }
```

Same `Idempotency-Key` + same payload hash → `200` with the original execution. Same key + different payload → `409`. No key → `400`.

Execution detail (abridged):

```json
{ "id": "0193a6c2-...", "status": "Rejected", "workflow": { "key": "invoice-standard", "version": 3 },
  "submittedAt": "2026-11-09T10:42:03Z", "completedAt": "2026-11-09T10:42:03.410Z",
  "steps": [
    { "nodeId": "n1", "type": "api-trigger", "status": "Succeeded", "outputPort": "out", "durationMs": 2 },
    { "nodeId": "n2", "type": "validate", "status": "Succeeded", "outputPort": "invalid", "durationMs": 31,
      "result": { "issues": [ { "path": "invoice.billingAddress", "rule": "required", "message": "missing or empty" },
                              { "path": "invoice.date", "rule": "type:date", "message": "expected YYYY-MM-DD" } ] } },
    { "nodeId": "n3", "type": "generate-pdf", "status": "Skipped" },
    { "nodeId": "n4", "type": "archive", "status": "Skipped" }
  ],
  "documents": [] }
```

### 5.4 Documents / archive

| Method | Path | Purpose |
|---|---|---|
| GET | `/documents` | Archive search: document type, customer, invoice number (metadata), date range |
| GET | `/documents/{id}` | Metadata incl. hash, retention, source execution |
| GET | `/documents/{id}/content` | Stream PDF (audited) |

### 5.5 Real-time (SignalR)

Hub `/hubs/executions` (authenticated, same roles as `GET /executions`).

- Client → server: `SubscribeExecution(executionId)`, `SubscribeWorkflow(workflowId)`, `Unsubscribe…`
- Server → client: `ExecutionUpdated { executionId, status, eventId }`, `StepUpdated { executionId, nodeId, status, outputPort, eventId }`, `PreviewReady { previewId }`
- Messages are **notifications, not the source of truth**. On (re)connect the client catches up with `GET /executions/{id}/events?after=`.

## 6. Execution lifecycle

### 6.1 States

```
Execution:  Queued ──► Running ──► Succeeded
                          │   └──► Rejected   (business: data failed validation; not retryable)
                          └──────► Failed     (technical: render/storage/timeout; retryable)
                Failed ──(retry)──► Queued

Step:       Pending ──► Running ──► Succeeded(port) | Failed
            Pending ──► Skipped   (branch not taken / run ended)
```

The **Rejected vs Failed** split matters for a bank: a data problem goes back to the sender; a technical problem goes to operations.

### 6.2 Flow

1. **Submit (API).** Authorize → enforce size/depth limits → check idempotency → resolve latest published workflow version and `latest-published` template refs → in **one transaction**: insert `Execution(Queued)` with its steps, `ExecutionEvent(Submitted)`, `AuditEntry` → after commit, enqueue the Hangfire job. Return `202`. *Revised at M7 (transactional outbox):* Hangfire.SqlServer can't join EF's transaction without promoting it to MSDTC, so the row is the durable record and a Worker dispatcher re-queues any execution still waiting after a grace period. Still no "row without job" (eventually), and a job without a row does nothing.
2. **Pick up (Worker).** Hangfire job `RunExecution(executionId)`. Load execution + pinned versions. Mark `Running`. Hangfire's own automatic retry is off for this job; the engine owns retries.
3. **Walk the graph.** From the trigger node, run each node's handler; persist `ExecutionStep` + `ExecutionEvent` before and after; follow the edge from the returned port. Unconnected optional port → end of path.
4. **Validate.** JSON Schema of the workflow trigger + Validate node rules (required, typed checks). Exit `valid` or `invalid`. `invalid` with no outgoing edge → execution `Rejected` with issue list.
5. **Generate PDF.** Map data → `Verso.Rendering` produces HTML (fonts and images inlined) → `Verso.Rendering.Pdf` prints with Chromium → bytes written to `IDocumentStore` staging area → `Document(Generated)` with SHA-256.
6. **Archive.** Resolve folder pattern and metadata tokens → commit to archive location in `IDocumentStore` → re-verify hash → `ArchiveRecord` + `Document(Archived)`.
7. **Complete.** Terminal status + event + audit entry.
8. **Progress relay (API).** A hosted service in the API reads new `ExecutionEvent` rows by id watermark (~500 ms) and pushes them to SignalR groups. This avoids a direct Worker→API channel and needs no extra infrastructure (no Redis). Latency ≈ 1 s, which is fine for this use.

### 6.3 Failure handling

- **Step retry policy** (per node type, MVP default: 3 attempts, exponential backoff) for technical errors only. Implemented by rescheduling the Hangfire job; the engine resumes at the first non-succeeded step.
- **Idempotent steps.** Each handler checks for its existing result before doing work (e.g. a `Document` already exists for this step → reuse). A worker crash mid-step is therefore safe to re-run.
- **Timeouts.** Render: 30 s per document (assumption). Whole execution: 5 min (assumption).
- **Exhausted retries** → `Failed` with sanitized `FailureCode`. Operator can `retry`.
- **Chromium health.** Browser process is pooled and recycled after N renders or on crash.

## 7. Security boundaries

| Boundary | Control |
|---|---|
| Browser → API | OIDC bearer tokens from the bank's IdP (to be confirmed, §10). HTTPS only. CORS limited to the SPA origin. Anti-forgery not needed with bearer tokens (no cookies) — revisit if the bank requires cookie sessions/BFF. |
| Authorization | Role-based policies on every endpoint and the hub. Proposed roles: `TemplateAuthor`, `WorkflowAuthor`, `Operator` (submit, retry, view runs), `Viewer` (read-only history/archive), `Auditor` (audit trail, payload access), `Admin`. Endpoint × role matrix tested automatically. |
| Dev authentication | A development-only auth handler with seeded synthetic users, compiled in but refused at startup unless `ASPNETCORE_ENVIRONMENT=Development`. |
| Submitted data | Payload size cap (1 MB, assumption), JSON depth cap, schema validation before processing. Payloads are never written to logs. Payload read endpoint restricted to `Auditor`/`Admin`. Synthetic data only in all environments for the MVP. |
| API ↔ Worker | No network channel between them. They share the database only. The Worker exposes no inbound HTTP (except an optional localhost health probe). |
| Database | Separate SQL logins for API and Worker, least privilege per schema. Migrations run by a separate deploy-time identity, not by the app at startup in non-dev environments. Archive and audit tables: INSERT/SELECT only. |
| Chromium rendering | Runs only in the Worker. Templates are structured layout JSON rendered by our code — **no raw author HTML/JS in the MVP**. All bound values HTML-encoded. Page JavaScript disabled. All network requests from the page aborted (fonts/images inlined as data URIs). Per-render timeout. Browser runs as the low-privilege worker service account. |
| Document storage | Accessed through `IDocumentStore`. Storage keys are generated ids, never user-supplied paths (folder patterns are sanitized metadata, not filesystem paths). API has read access; only the Worker writes. SHA-256 recorded and verified on read. True WORM/immutability is a property of the production storage target (§10). |
| Secrets | Connection strings and keys from environment / bank secret store; nothing in the repo. |
| Supply chain | Central package version pinning (NuGet and npm lockfile), no runtime CDN usage, fonts/icons self-hosted, dependency list for bank approval, vulnerability scanning in CI. |
| Audit | Publishing, submission, retry, document download, and role-sensitive reads are written to the append-only audit log with actor and correlation id. |

## 8. Test strategy

| Layer | What | Tooling (subject to approval) |
|---|---|---|
| Unit — backend | Binding/filter engine, validation rules (incl. IBAN/VAT/date types), layout → HTML renderer, graph validator, execution state machine, idempotency rules | xUnit, FluentAssertions-equivalent or Shouldly (license check required) |
| Golden rendering | Renderer HTML snapshots for each element type and the invoice template; PDF assertions by text extraction (page count, invoice number, totals, line count) — not pixel diffing in MVP | Verify-style snapshot library, PdfPig |
| Integration — backend | Real API host (`WebApplicationFactory`) + real SQL Server + in-process Hangfire server + real Chromium: submit → poll → assert archived document and hash | Testcontainers for SQL Server **or** a shared dev SQL instance / LocalDB if Docker isn't allowed (§10) |
| Architecture | Module boundary rules from §3 | NetArchTest or ArchUnitNET |
| Security | Endpoint × role matrix; payload limits; template referencing an external URL produces no network request; dev auth refuses to start outside Development; no payload content in log output | xUnit |
| Contract | OpenAPI document snapshot; CI fails if generated frontend types are stale | openapi-typescript |
| Frontend unit/component | Designer logic (element ops, binding lookup, graph validity, React Flow ⇄ definition mapping), key components | Vitest, React Testing Library |
| End-to-end | The 7 MVP steps as browser scenarios: edit + publish template, build + publish workflow, submit valid payload → Succeeded + downloadable PDF, submit broken payload → Rejected with issues, history shows both | Playwright (Node test runner) — or Playwright for .NET to keep one toolchain; decision in §10 |
| Fixtures | Synthetic generator + checked-in fixtures: valid invoice, 1-line, 60-line (multi-page), missing fields, wrong types, Greek text, huge payload | `tools/synthetic-data` |

Rule: every milestone ships with its tests; CI is green before a milestone is called done.

## 9. Milestones

Each milestone is a vertical slice that ends in something runnable. Sizes are relative.

### M1 — Application foundation (S) — ✅ accepted 2026-11-09
Scope as agreed 2026-11-09 (replaces the earlier "walking skeleton" scope):
- Layered solution (§3), central package management.
- API with `GET /api/health` (database connectivity + pending migrations), structured JSON logging, no committed secrets.
- EF Core on local SQL Server (LocalDB), initial migration creating the module schemas.
- Worker process skeleton (startup DB check + heartbeat).
- SPA shell: Verso navigation, light/dark theme from ported tokens, self-hosted fonts, live API status.
- Setup instructions (`docs/setup.md`).

**Acceptance:** backend builds; frontend builds; frontend calls the health endpoint; migration applies to the development database; tests pass.

Moved out of M1: Hangfire (→ M5/M7, when the Worker first runs jobs), dev auth / Entra (→ before M2's write endpoints need roles; to schedule), CI pipeline (CI system still unknown, §10), architecture tests (project references enforce the layers for now).

### M2 — Template storage (S) — ✅ accepted 2026-11-09
- Template + TemplateVersion entities, draft save with ETag, publish, version history, audit entries.

**Acceptance:** create template; save draft twice; stale `If-Match` → 412; publish → v1 immutable (PUT to published → 409); second draft → v2; history lists both; audit shows each action; all via API tests.

Decisions made while implementing M2 (2026-11-09):
- Added `POST /templates/{id}/draft` (start a draft from the latest published version); "PUT to published → 409" is `PUT …/draft` with no draft → `409 template.no_draft`.
- Added `GET /audit?entityType=&entityId=` (read side of the audit log; the audit UI is still M9).
- No sign-in yet: changes are attributed to a configured development user, and the API refuses to start outside Development/Testing until Entra ID exists (fail closed).
- `layout` is stored as JSON and only checked for `layoutSchemaVersion: 1`; the element model and its validation come with M3.
- Template name/description can't be edited after creation yet (not needed for the MVP flow).
- OpenAPI document checked in at `docs/openapi/v1.json` with a drift test; generated frontend types arrived with M4 (`npm run gen:api`).
- Published-version immutability is enforced by the domain, a check constraint and filtered unique indexes; denying UPDATE/DELETE to the app login is part of deployment hardening (M9).

### M3 — Rendering engine (M) — ✅ accepted 2026-11-09
- Layout model v1 (§D4), element types for MVP: text, data field, repeating table, divider, box, image (embedded), signature line. QR/barcode/conditional block: deferred to post-MVP unless you want them now.
- Binding `{{path|filter}}`, HTML encoding, number/currency/date formatting (locale: §10).
- `POST …/preview/html`.

**Acceptance:** golden HTML for each element; invoice sample renders all fields; missing binding renders a visible placeholder in preview and is reported as an issue; HTML-injection fixture is escaped; renderer has no I/O dependencies.

Decisions made while implementing M3 (2026-11-09), full model in [docs/layout-model.md](../layout-model.md):
- **D4 implemented** as fixed-height header/footer bands (absolutely positioned elements, mm) plus a flowing body.
- **How pagination works (verified with Chromium):** CSS-only approaches failed. Repeated `thead` spacers are dropped by Chromium when taller than ¼ page, and `position:fixed` elements can't be placed into page margins. The renderer therefore outputs, besides the screen preview, a **print body plus header/footer templates** for Chromium's `Page.printToPDF` (`displayHeaderFooter`), which repeats them in each page's top/bottom margin at any height. A 60-line invoice printed through the DevTools protocol showed the full header, footer and table headings on all 3 pages. M5 uses this; it also enables "Page X of Y" later.
- New project `Verso.Rendering` (no dependencies), referenced by Application; purity enforced by a metadata test.
- Formatting fixed to English/EUR (`€1,234.50`, `02 Nov 2026`), independent of server culture; locale stays open in §10.
- Saving a draft now validates the full layout structure (M2 only checked `layoutSchemaVersion`).
- Deferred from M3: web-font embedding (M5), page numbers, QR/barcode/conditional blocks, body columns.

### M4 — Template designer UI (L) — ✅ accepted 2026-11-09
- Template list; designer with canvas, palette, properties panel, element tree, sample-data toggle, binding picker from the data schema, unmapped warnings, undo, save/publish, HTML preview pane.

**Acceptance:** an author can build the Commercial Invoice from an empty template without editing JSON; saving and reloading round-trips exactly; publish disabled while there are errors; keyboard-accessible element selection; matches prototype visual direction.

Decisions made while implementing M4 (2026-11-09):
- Canvas editing follows D4. Header/footer elements are dragged and resized freely inside their band (snap 1 mm, Alt for 0.1 mm; arrow keys move 1 mm, Shift+arrow 5 mm). Body blocks are a vertical stack, reordered by drag or Alt+↑/↓. The canvas is an editing view; the HTML preview pane shows the server renderer's output (D2).
- The preview endpoint renders the **saved** draft, so the preview pane shows the last save and offers "Save and refresh". No new endpoint was added.
- Data model: the author imports a synthetic sample JSON file and the data model (JSON Schema) is derived from it, so nobody types JSON. An "Edit JSON" dialog exists for advanced changes. Bindings are chosen from the data model (list or drag-and-drop).
- "Unmapped" means a binding the data model doesn't contain, or a data field with no binding yet. Unmapped bindings are **errors** and block publishing, as do the structural rules that mirror LayoutParser. The browser checks these on every change, and the server still validates every save. Clipped or off-page elements are warnings.
- Undo/redo covers every edit. Typing in one input is one step; a drag is one step.
- The element tree (regions → elements) is the keyboard path for selecting elements; canvas elements are focusable too.
- A template with no draft opens its latest published version read-only, with "Start new draft".
- The palette shows only element types the M3 renderer supports. QR code, barcode and conditional block stay deferred.
- Generated frontend types: `src/verso-web/src/lib/api/generated.ts` is generated from `docs/openapi/v1.json` by a dependency-free script (`npm run gen:api`), and a drift test fails when it is out of date.
- Template name and description stay read-only in the designer (no edit endpoint, as in M2).

### M5 — PDF generation in the Worker (M) — ✅ accepted 2026-11-09
- `Verso.Rendering.Pdf` with pooled Chromium, network blocking, JS disabled, timeout, recycling.
- PDF preview job (`…/preview/pdf`) and `PreviewReady` notification.
- Offline Chromium installation procedure documented.

**Acceptance:** invoice fixture → PDF whose extracted text contains invoice number, customer, all line descriptions and totals; 60-line fixture paginates with repeated table header; external-URL fixture makes zero network requests; render of a 1-page invoice < 3 s p95 on the reference machine (assumption); killing Chromium mid-render → retried successfully.

Decisions made while implementing M5 (2026-11-09):
- **D5 decided:** standard PDF for the MVP. PDF/A stays a later decision, once the bank's archive requirements are known; it would need a post-processing library.
- New project `Verso.Rendering.Pdf` (Playwright 1.64.1, Chromium 156 / build 1248, headless shell), referenced only by the Worker. The contract `IPdfPrinter` lives in pure `Verso.Rendering`.
  - One shared browser, a fresh isolated context per document, JavaScript off, offline, every request aborted and counted.
  - 30 s render timeout; recycled after 100 documents; at most 2 at once.
  - If Chromium or Playwright's own driver process dies, it is replaced and the document is retried once. The Worker starts Chromium at startup.
- Fonts (deferred from M3): static 400/600 weights of the three document fonts (SIL OFL), embedded in `Verso.Rendering.Pdf` and inlined at print time. Variable fonts were rejected because Chromium embeds them as Type 3 outlines.
  - Every face is preloaded in the body, and the rules use `font-display:swap`: a font used only in the header template was otherwise still loading when Chromium drew the header, and the text went missing.
- PDF preview flow: the API stores a `PdfPreview` row holding a snapshot of the draft's page settings, layout and data, then queues a Hangfire job. The Worker renders it (preview mode) and stores the PDF bytes in the row. The API's relay sends `PreviewReady` over SignalR (`/hubs/executions`).
  - The preview is saved first and queued second. If queueing fails, the row is marked failed. The same-transaction enqueue in §6 is still planned for executions (M7).
  - Retries: 3 attempts for technical failures; then `Failed` with the error.
  - Previews are deleted after 24 h by an hourly recurring job. They're stored in the database, because the document store (M7) is for archived documents.
  - Endpoints: `POST …/draft/preview/pdf` (202), `GET /previews/{id}`, `GET /previews/{id}/content`.
- Hangfire 1.8.25 (SQL Server storage, schema `hangfire`). Its schema is created by our EF migration (`AddPdfPreviews`) from Hangfire's own install script, never at startup. Newtonsoft.Json is pinned to 13.0.4, because Hangfire otherwise allows a vulnerable 11.0.1.
- The relay waits until the database exists and is fully migrated before polling, so it never touches a database mid-migration.
- Designer: the preview pane has HTML and PDF tabs. The PDF renders when the tab opens, after each save, and on refresh. The designer subscribes over SignalR (`@microsoft/signalr` 10.0.11) and also polls every 3 s.
- Test-only dependency: PdfPig 0.1.16 (Apache-2.0) for PDF text extraction in `tests/Verso.Pdf.Tests`.
- Measured on the development machine:
  - Render (start → stored) about 0.9 s; the first after start about 1.5 s.
  - Request → PDF on screen 2–3 s, most of it Hangfire's 1 s queue polling.
  - The printer-level p95 test (20 renders) passes under 3 s.

### M6 — Workflow definitions and designer (L) — ✅ accepted 2026-11-09
- Node catalog endpoint; MVP nodes `api-trigger`, `validate`, `generate-pdf`, `archive`.
- Server-side graph validation (single trigger, reachable nodes, no cycles, required ports connected, config valid against node schema, referenced template version exists and is published).
- React Flow designer: palette by category, node cards with ports and status, config side panel generated from node schema, validation issues on nodes, save/publish.

**Acceptance:** build the "Invoice standard" workflow (trigger → validate → generate PDF → archive) in the UI; disconnected/invalid graphs show per-node issues and cannot be published; reload round-trips node positions and config; draft referencing a template draft (not published) is rejected on publish.

Decisions made while implementing M6 (2026-11-09):
- Workflows module (schema `workflows`): `Workflow` and `WorkflowVersion`, with the template lifecycle (one draft, published versions frozen, gap-free numbers, ETag/`If-Match`, audited). `Retired` is not implemented yet. The definition is stored exactly as saved, and its content hash is set on publish.
- Node catalog in code, served by `GET /node-types`. Settings follow the prototype:
  - `validate`: required fields plus type checks (text, number, date, email, EU VAT, IBAN).
  - `generate-pdf`: template, version (`latest-published` or a specific published version), filename pattern.
  - `archive`: folder pattern, retention (7y/10y/permanent), metadata pairs.
  - `invalid` and `failed` are optional outputs; leaving them unconnected is a warning.
- Settings schemas use a JSON Schema subset, checked by an in-house validator (`ConfigSchemaValidator`), so no JSON Schema library was added.
- Saving checks structure only, so unfinished graphs can be saved. Graph and settings problems are issues from `POST …/draft/validate`. That endpoint also accepts an unsaved definition (an extension of §5.2), which the designer uses to show issues live. Publishing re-validates; it returns 409 `workflow.invalid` with the issue list.
- One edge per output port (an output leads to one next step); any number of edges may enter an input.
- Designer: @xyflow/react 12.12.0 (12.12.1 was under two weeks old).
  - Palette by category; only one trigger allowed.
  - Node cards with category colour, status and labelled ports.
  - Settings panel generated from the node schema: template picker with published versions, field-path suggestions from the referenced template's data model.
  - Undo/redo, save/publish, read-only published view with "Start new draft".
- The undo history module moved from the template designer to `src/lib/history.ts` (shared).

### M7 — Execution engine (L) — ✅ accepted 2026-11-10
- Submit endpoint with idempotency, transactional enqueue, engine, step handlers, events, local file-system `IDocumentStore`, archive records, Rejected/Failed semantics, retry.

**Acceptance:** valid fixture → `Succeeded`, one archived document with matching SHA-256 at the resolved folder path; broken fixture → `Rejected` with each failed rule listed, no document; duplicate submit with same key → same execution; same key with different body → 409; stop the Worker mid-run and restart → execution completes without duplicate documents; storage failure → `Failed`, retry → `Succeeded`.

Decisions made while implementing M7 (2026-11-10):
- **Transactional outbox instead of same-transaction enqueue** (§6.2 step 1, chosen by the user). Tested on LocalDB: Hangfire.SqlServer 1.8 (netstandard2.0 build) opens its own connections and transactions, so enlisting it in EF's `TransactionScope` promotes to MSDTC and fails. Now:
  - Submit commits the execution, steps, `Submitted` event and audit entry in one transaction, then queues the job. A queueing failure is logged and the submission still returns 202.
  - A recurring Worker job (`execution-dispatch`, every minute) re-queues executions that are `Queued`/`Running`, not locked, and last dispatched more than 2 minutes ago.
  - Each run takes a lock on the execution (`LockedUntil`, 6 min, renewed at every step, protected by `rowversion`), so duplicate jobs never run an execution twice. On a clean Worker stop the lock is released and the interrupted step isn't counted as an attempt. After a crash, the lock expires and the dispatcher re-queues the run.
- Engine (Worker, `ExecutionEngine`): one Hangfire job per execution, Hangfire's retry off. It walks the pinned version from the trigger and saves each step before and after it runs. Succeeded steps are never re-run, so a retry or restart resumes at the first unfinished step.
  - Technical errors: 3 attempts per step, 5 s then 10 s apart, by rescheduling the job. While a retry is pending the execution stays `Running`. After the third attempt it is `Failed`, and the steps that didn't run stay `Pending`.
  - Run timeout 5 min. Steps that didn't run are `Skipped` on `Succeeded`/`Rejected`.
- Rejected vs Failed: Validate leaving by an unconnected `invalid` → `Rejected` (`data.invalid`, issues in the step result). Generate PDF leaving by an unconnected `failed` → `Rejected` (`document.data_incomplete`). Generate PDF takes `failed` when rendering in final mode reports errors (a bound value is missing), because that is a data problem; technical print/storage errors are retried and end as `Failed`. The designer's warning text for the unconnected `failed` port changed to match.
- Validate rules: required = present, not null, not blank, not an empty list. Type checks run only on present fields:
  - date: `YYYY-MM-DD`, a real calendar date.
  - number: a JSON number.
  - email: a simple shape check.
  - EU VAT: per-country formats, including EL and XI. No VIES lookup.
  - IBAN: ISO 13616 mod-97, plus country lengths for SEPA countries.
  - Issue messages never echo values. The sample fixture's IBAN now has valid check digits (`CY82 0000…`); the two rendering goldens changed by that line only.
- Documents module (schema `documents`): `Document` (unique per execution and node, so the database refuses duplicates) and write-once `ArchiveRecord`.
  - `IDocumentStore` with a local file-system adapter (`Documents:RootPath`, default `%LOCALAPPDATA%\Verso\documents`). Writes are atomic: temp file, then move.
  - Keys are generated: `staging/{execution}/{node}.pdf` (overwritten on re-run, deleted once archived) and `archive/yyyy/MM/{documentId}.pdf` (dated by document creation, so a re-run writes the same key).
  - Archive re-reads the staged bytes, checks the SHA-256, writes the archive copy, reads it back and checks again.
  - `folderPath` is the resolved, sanitized folder pattern stored as archive metadata, not a file-system path (§7). "At the resolved folder path" in the acceptance means the archive record's `folderPath`.
- Retention (open since §10): the node's 7y/10y/permanent is recorded as `retentionUntil` (null = permanent). Nothing is deleted at expiry in the MVP.
- Endpoints in M7: submit, `GET /executions/{id}` and `POST /executions/{id}/retry`. The list, the events catch-up, the payload view, document endpoints and the SignalR relay of execution events stay in M8 (events are already written). The submit body is capped at about 1.1 MB, data at 1,000,000 characters and 32 levels of nesting. An optional `X-Correlation-Id` is stored.
- Audit: `ExecutionSubmitted`, `ExecutionRetryRequested`, `ExecutionCompleted` (actor `verso.worker`). Payloads are never audited or logged.
- DB-level write-once for `ArchiveRecords` (deny UPDATE/DELETE to the app login) stays with deployment hardening in M9, as for published versions.
- Test infrastructure: integration tests now create and migrate their databases before starting API hosts. A host's background relay could connect between EF's `CREATE DATABASE` and its `ALTER DATABASE` and block it; this happened when several databases were created in parallel.

### M8 — Execution history and live progress (M) — ✅ accepted 2026-11-10
- Executions list (filters), execution detail with step timeline (prototype's run view), live updates via SignalR relay, input payload view (restricted), document download, archive search screen.

**Acceptance:** submitting from a second browser tab shows step progress live within ~2 s; reconnecting mid-run catches up with no missing steps; filters by status/date/workflow work; download returns the archived bytes (hash verified); Viewer can't see payloads.

Decisions made while implementing M8 (2026-11-10):
- **Payload restriction before roles exist (chosen by the user).** `ICurrentUser` gains `Roles`, and `GET /executions/{id}/input` requires Auditor or Admin (403 `forbidden`, checked before the lookup). Until Entra ID, the development user's roles come from `DevUser:Roles` (default `Operator,Auditor`). M9 replaces the source of roles with Entra app roles and adds the full endpoint × role matrix. Each successful payload read is audited (`ExecutionInputViewed`, input hash only).
- **Live progress relay** (`ExecutionEventRelay`, API hosted service): polls `ExecutionEvent` every 500 ms from the latest event at startup, and sends `ExecutionUpdated` (to the execution, workflow and an all-executions group) and `StepUpdated` (execution group). The all-executions group (`SubscribeExecutions`) is an addition to the plan's two group kinds, for the history list.
  - Identity values can commit out of order across concurrent runs, so each poll re-reads the last 100 ids and skips those already sent. A test makes an event visible late and checks it is delivered once.
  - Group memberships are lost on reconnect: clients subscribe again and catch up with `GET /executions/{id}/events?after=`. Clients merge events by id and keep a 3 s fallback poll while a run is unfinished.
  - A connection in two groups can receive a notification twice; clients treat them as idempotent hints.
- **Document download:** the API reads the file through `IDocumentStore`, checks its SHA-256 against the recorded one before sending, and audits the download (`DocumentDownloaded`). A mismatch or missing file is 409 and nothing is served. Both processes use the same `Documents:RootPath`; the API never writes. Only archived documents are listed or served.
- **Archive search** (`GET /documents`): free text `q` matches the file name and the metadata values, as plain text (`%`/`_` are literal), plus an archive-date range. A new `ArchiveRecords.SearchText` column holds them, set when archiving. This avoids SQL Server JSON functions in the Application layer and works with the author-defined metadata keys. The migration `AddArchiveSearch` backfills existing rows. The plan's separate "document type" and "customer / invoice number" filters are covered by `q`, because the MVP has one document type and those are metadata values.
- Executions list filters: workflow key, status, submission date range (`from` inclusive, `to` exclusive; the UI sends the start of the first day and the start of the day after the last), correlation id; paged, newest first.
- Screens: Executions (list with live updates), run detail (steps timeline with problems found by Validate, documents with download, details, activity from the events, submitted data on request, retry for Failed runs), and Archive. The Overview, Document preview and Audit trail screens are unchanged (M9 builds the audit trail).
- Measured with the real API, Worker, Chromium and SignalR (Node client using `@microsoft/signalr`): `Submitted` after about 0.3 s, `Started` after 0.8 s and `Succeeded` after 1.8 s, and every stored event was also announced.

### M9 — MVP hardening (M) — ✅ accepted 2026-11-10
- Role matrix enforcement + tests, audit trail screen (read-only), limits, structured logging with correlation ids, E2E suite for the 7 MVP steps, runbook (setup, Chromium offline install, backup notes).

**Acceptance:** all E2E scenarios pass in CI; security tests pass; no payload content in logs during the full E2E run; a new developer sets up from the runbook alone.

Decisions made while implementing M9 (2026-11-10). The user chose the first four:
- **Sign-in: both modes, chosen by configuration.**
  - **Entra** (`Auth:Mode`, the default): JWT bearer in the API, MSAL (auth code + PKCE, redirect, sessionStorage cache) in the SPA. Roles come from Entra app roles `Verso.<Role>`, or from groups or other values mapped in `Auth:RoleMap`. Startup fails without a tenant and API client id (fail closed).
  - **Development:** synthetic users, one per role plus one without access, tokens `dev:<user id>`. Startup refuses it outside the Development and Testing environments. The old "refuse to start outside Development" guard is gone.
  - With no tenant available, Entra is tested with locally signed tokens: audience, expiry, wrong key, role and group mapping, and hub-only query tokens. That testing found and fixed two bugs in the mapping. A real tenant hasn't been exercised yet.
  - Packages, approved 2026-11-10: `Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.14 and `@azure/msal-browser` 5.26.0. `@azure/msal-react` was approved but isn't needed.
- **Role matrix** (§7 roles): reads for every Verso role; template authoring for TemplateAuthor and Admin; workflow authoring for WorkflowAuthor and Admin; submit and retry for Operator and Admin; payloads and the audit trail for Auditor and Admin.
  - Every endpoint names a policy, and the fallback policy requires a Verso role, so the API is secure by default. A signed-in user without a role reaches only `/me`.
  - `RoleMatrixTests` calls all endpoints as all users plus anonymous, from an independently written table, and fails when an endpoint isn't in the table.
  - The SPA hides what the user can't do, using `GET /api/v1/me` permissions: designers become view-only, New buttons are hidden, Retry and payload views appear only for the right roles, and the Audit nav item only for Auditor/Admin.
- **E2E: Playwright for .NET** (`tests/Verso.E2E.Tests`). It runs the real API and Worker processes, the production SPA build served by `vite preview` with the dev proxy, Chromium build 1248 and its own database. The 7 MVP steps run in the browser, followed by Viewer restrictions, Kestrel body limits, 401 without a token, and a scan of both services' log output for a payload marker.
- **CI: `tools/ci.ps1`**, until the bank names its CI system. It runs prerequisites, the Release build, frontend install, typecheck, tests and build, the backend test projects one at a time, the E2E suite, and NuGet/npm vulnerability checks. A pipeline file only needs to call it.
- **Maker-checker (D7): after the MVP.**
- **Authenticated content:** downloads and the PDF preview are fetched with the token and handed over as `blob:` URLs, because links and iframes can't send a bearer token. The SignalR client sends the token through `accessTokenFactory`.
- **Limits:** request bodies are capped at 2 MB (Kestrel and IIS; submissions 1.1 MB as before), and submissions are limited per user per minute (120, fixed window) with 429, `Retry-After` and a problem body. Framework 401/403/413/429 responses get stable `code`s.
- **Logging:** a correlation id for every request (the caller's `X-Correlation-Id` if well-formed, else generated), echoed and added to the log scope. A submission without one stores the request's id on the execution, and the Worker logs each run under `ExecutionId` + `CorrelationId` (start, step failures, finish). Tests capture all log output at Trace level and check that no payload value appears on any path (valid, rejected, technical failure, malformed, too deep, idempotency conflict, payload view).
- **Audit screen:** `GET /audit` gains `action`, `actorId`, `from`, `to` and `newestFirst`, plus `GET /audit/actions`. The read-only screen links entries to their template, workflow or run.
- **Database hardening:** `tools/db/least-privilege.sql` creates the roles `verso_api` and `verso_worker`. Audit and archive records are insert/select only, the apps can't delete templates, workflows, executions or documents, and neither role can change the schema. `LeastPrivilegeTests` runs the whole flow under the roles and checks 15 refused changes. Published-version immutability is still enforced by the domain and check constraints, not by permissions.
- **Worker as a Windows service** (`Microsoft.Extensions.Hosting.WindowsServices` 10.0.14, approved 2026-11-10). Install steps are in the runbook.
- **Runbook** (`docs/runbook.md`): A, a developer from a clean machine to `CI passed`; B, servers (database and roles, Entra registrations, IIS with the SPA and a reverse proxy, offline Chromium, the Worker service, a smoke test); C, operations (logs and correlation, failure handling, backup and restore order, retention, upgrades).

**MVP done = M1–M9 accepted.** ✅ Reached 2026-11-10.

Not in MVP: email/print/SFTP/ERP/API delivery (folder input followed after the MVP, see below), AI nodes and Assistant, map-fields/branching nodes, DOCX/HTML email output, QR/barcode, maker-checker publishing, multi-tenant issuers, production storage adapters, dashboard/overview metrics.

## Post-MVP features

### Folder input (2026-11-11)

Requested by the user as a separate feature. It is the first real (not simulated) input besides the API trigger. Full decision record: [ADR 0001](../adr/0001-folder-input.md).
- New node `folder-input`, beside the simulated `file-upload` (unchanged). The Worker scans approved folders, starts one run per JSON file, and moves the file to the processed or failed folder (`<name>.error.json` explains failures).
- Approved locations are configured on the Worker (`FolderInput:Locations`), which reports key, name and purpose only. Designers pick keys and relative subfolders; paths are validated at design time and resolved, contained and checked for links on the Worker. There are no stored credentials: folders are reached as the Worker's service identity, on Windows (local or UNC) and Linux (OS mounts).
- Reliable ingestion: a handoff convention (temporary names, settle time, exclusive open) and a database claim (filtered unique index) plus an atomic move into `.verso-inflight`. State moves Claimed → Submitted → Delivering → Completed, with 2-minute leases for restart recovery. A content-derived idempotency key means no duplicate runs.
- New schema `ingestion` (migration `AddFolderInput`), endpoints `GET /folder-locations`, `POST|GET /folder-access-tests`, `GET /workflows/{id}/folder-input`. Roles: reads for every role; Test access for WorkflowAuthor/Admin. `tools/db/least-privilege.sql` extended.
- Designer: location pickers, an "Include subfolders" switch (the settings schema gained `boolean`), Test access with per-check results and the identity used, and folder activity (last scan, errors, recent files linking to their runs).
- Typed paths (2026-11-11, later the same day, at the requester's request): the step takes `sourcePath`, `processedPath` and `failedPath` typed as absolute paths instead of choosing approved locations. Paths are recorded in `ingestion.TypedFolderPaths` (migration `AddTypedFolderPaths`) and audited when a workflow is published or a path access-tested; the Worker validates them as its service identity and applies `FolderInput:AllowedRoots` if set. Workflows saved with locations keep working. See ADR addendum 2.
- Admin screen (*Folder locations*, added the same day at the requester's request): Admins add, change, test and remove locations by typing the path (stored in `ingestion.ManagedFolderLocations`, migration `AddManagedFolderLocations`), instead of editing the Worker configuration. New policy `Administration` (Admin only) and permission `administer` in `/me`. Typing a path in the workflow designer stays refused. See the ADR addendum.
- Not included: XML/CSV mapping, alerting on scan failures, a screen listing all ingested files across workflows.
- Fixed alongside (found by these tests): audit `TraceId` was `varchar(32)`, but it also stores execution correlation ids, which the API accepts with up to 64 characters. A run submitted with a longer `X-Correlation-Id` failed at its final save and never finished. The column is now `varchar(64)` (migration `WidenAuditTraceId`, widening only), with a regression test.

## 10. Assumptions and unresolved decisions

### Answered 2026-11-09 (Q1–Q3)

The user chose these answers. They are working assumptions until the bank formally confirms its approved environment.

| Topic | Decision |
|---|---|
| Runtime | .NET 10 LTS, ASP.NET Core 10, EF Core 10 |
| Database | SQL Server 2022 |
| Frontend tooling | Node 22 LTS (build only), React 19, TypeScript 5.x, Vite, @xyflow/react 12, Tailwind CSS 4, shadcn/ui; exact patch versions pinned at M1 |
| Packages | Public nuget.org / npmjs feeds, pinned via central package management and lockfiles; designed so an internal mirror can be swapped in through `nuget.config` / `.npmrc` |
| Hangfire | Open-source edition (LGPL), used unmodified |
| Hosting | Windows Server: API under IIS (in-process), Worker as a Windows service. Chromium installed from an offline Playwright browser bundle placed on the server (`PLAYWRIGHT_BROWSERS_PATH`); the Worker never downloads at runtime |
| Identity | Microsoft Entra ID via OIDC (SPA: auth code + PKCE with MSAL; API: JWT bearer). Verso roles mapped from Entra/AD security groups through configuration. Development-only fake sign-in with synthetic users |

Consequences for the plan:
- M1 includes an Entra app-registration checklist (SPA + API registrations, exposed API scope, group claims or app roles) — the registrations themselves are created by the bank's identity team.
- Group overage (users in more than ~200 groups) must be handled: prefer **Entra app roles assigned to groups** over raw group claims. To confirm with the identity team.
- Runbook gains: IIS hosting bundle install, Worker service install, offline Chromium bundle procedure and the Windows fonts/locale prerequisites for rendering.
- Testcontainers (Docker) remains open (D8); on Windows-only dev machines the fallback is LocalDB / SQL Server Developer edition.

### Originally blocking before M1 (now answered, kept for the record)

**Q1. Approved versions.** I have no information about the bank's approved environment, so I have not selected versions. Candidates I would propose, each pending your confirmation:
- **.NET 10 (LTS)** + ASP.NET Core 10 + EF Core 10. Note: .NET 8 LTS support ends on **10 Nov 2026** (tomorrow), so starting on .NET 8 isn't advisable even if it's the currently approved runtime.
- **SQL Server**: which edition and version is approved (2019 / 2022 / 2025)? This determines the minimum compatibility level and JSON features available.
- **Hangfire** 1.8.x (open-source LGPL edition; check whether LGPL is acceptable, and whether Hangfire Pro is needed — it is not for the MVP).
- **Playwright for .NET** 1.x latest at M1, with its pinned Chromium revision.
- **Node.js** LTS (22 or 24) for build tooling only; **React 19**, **TypeScript 5.x**, **Vite**, **@xyflow/react 12** (React Flow), **Tailwind CSS 4**, **shadcn/ui** (copied source, so the real dependencies are Radix UI primitives).
- Is there an internal NuGet/npm mirror (Artifactory/Nexus) we must resolve packages from?

**Q2. Hosting target.** Windows Server + IIS / Windows services, or Linux containers (Kubernetes/OpenShift)? Affects how Chromium is installed (Playwright downloads browsers from the internet by default; an offline/mirrored install will almost certainly be needed) and how the Worker runs.

**Q3. Identity provider.** Entra ID, on-prem ADFS, or another OIDC provider? Or Windows Integrated Authentication? And are roles sourced from AD groups?

### Needs your answer before the milestone named (non-blocking for M1)

- **D4 — Template layout model (before M3).** The prototype positions every element absolutely on a fixed A4 sheet. That can't handle invoices whose line count varies (a 60-line invoice must flow onto page 2). **My recommendation:** fixed **header** and **footer** regions where elements are absolutely positioned (as in the prototype), plus a flowing **body** region where blocks stack and the repeating table breaks across pages with repeated headers. The designer still looks like the prototype. Alternative: pure flow layout (simpler engine, less designer freedom).
- **D5 — PDF/A (before M5).** The prototype shows "PDF/A-3". Chromium cannot produce PDF/A. If archive compliance requires PDF/A, we need a post-processing step (likely a licensed library) — that's a new dependency for approval. MVP proposal: standard PDF, with PDF/A as a follow-up. **Decided 2026-11-09: standard PDF for the MVP.**
- **D6 — Production storage (before production, not MVP).** Which target: network file share, SQL Server (FILESTREAM / varbinary), S3-compatible object storage with object lock, or the bank's existing DMS/ECM? Is WORM required by regulation? MVP ships with the local file-system adapter only.
- **D7 — Maker-checker (before M9 or post-MVP).** Should publishing a template or workflow require a second person's approval (four-eyes)? Common in banks; it changes the publish flow. Proposed: post-MVP.
- **D8 — Test infrastructure (before M2).** Is Docker available on developer machines and CI (for Testcontainers SQL Server)? If not, integration tests use LocalDB or a shared dev SQL instance.
- **Languages and locale (before M3).** Greek-language documents? If yes, fonts must cover Greek — **verified at M1: Schibsted Grotesk has no Greek glyphs** (Latin and Latin Extended only); Source Serif 4 does include Greek. Greek documents would need a different sans font. Also: number/date formats (`1.234,56 €` vs `€1,234.56`), currency (EUR only?).
- **Retention (before M7).** Retention period per document type (prototype shows 7 years for invoices, 10 for statements) and what happens at expiry (MVP: record only, no deletion). **Applied at M7:** chosen per archive node (7y/10y/permanent), recorded as `retentionUntil`; no deletion at expiry. The bank's actual periods are still to be confirmed.
- **Issuer model.** The prototype shows a bank (Atlas Mutual) producing invoices for a corporate issuer (Halcyon Energy). Is Verso used by the bank for its own documents, or on behalf of corporate clients (multi-tenant)? MVP assumes **single tenant, single issuer**.
- **E2E tooling.** Playwright Test (Node) for browser E2E is the most productive option but adds a second Playwright distribution to approve; Playwright for .NET can do it with one toolchain at some convenience cost.
- **CI system.** Azure DevOps, GitHub, GitLab, Jenkins? Determines pipeline files.

### Assumptions I'm making (tell me if any is wrong)

1. All data in all environments during MVP is synthetic; no real customer data.
2. Single organization, single issuer, single language for the first template (English), EUR currency.
3. Expected volume for MVP is low (interactive submissions, tens per minute at most); batch volumes like the prototype's 18,000-statement jobs are a later concern, but the design (Hangfire queues, stateless worker) can scale out.
4. One worker instance is enough for MVP; the design allows several.
5. Template authors are trusted staff, but templates are still treated as untrusted input to the renderer (no raw HTML/JS).
6. Fonts are SIL OFL licensed (Schibsted Grotesk, Source Serif 4, IBM Plex Mono) and may be embedded in PDFs and self-hosted.
7. Browsers: current Edge/Chrome on bank desktops; no IE/legacy support.
8. Hangfire dashboard is exposed only in development (or to `Admin` behind auth), never publicly.

### Decisions I made (my calls — open to override)

- **D1** ~~Per-module DbContext~~ Revised at M1: one `VersoDbContext`, one SQL schema per module, in one database (fits the lighter layered structure the user chose).
- **D2** Server-side C# renderer is the single source of truth for document output; the designer canvas is an editing view and uses the server HTML preview for fidelity. Rejected: running a Node/React renderer in the Worker (second runtime in a bank server environment).
- **D3** Progress delivery via DB event table + API relay instead of Worker→API calls or a Redis backplane.
- Rejected vs Failed as distinct terminal states.
- Hangfire job per execution with engine-owned step retries, rather than one Hangfire job per step.
- Executions always run against **published** versions; drafts can only be previewed.
- `{{path|filter}}` binding syntax kept from the prototype; no expression language/scripting in MVP.
