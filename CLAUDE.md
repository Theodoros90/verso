# Verso

Bank-focused document-generation and workflow platform. MVP: maintain an invoice template, maintain a workflow, submit synthetic invoice JSON, validate it, generate a PDF, archive it, and show execution history.

- Full plan, entities, API contracts, lifecycle, security, tests, milestones: [docs/plan/2026-11-09-verso-mvp-plan.md](docs/plan/2026-11-09-verso-mvp-plan.md)
- Visual direction: [design-reference/Verso prototype v6.html](<design-reference/Verso prototype v6.html>). It's a prototype: keep its look, don't reuse its code as production code.
- Architecture decisions: `docs/adr/`
- Local setup and verification: [docs/setup.md](docs/setup.md). Implemented endpoints: [docs/api.md](docs/api.md)

## Current milestone

**M1: Application foundation** — accepted 2026-11-09.
**M2: Template storage** — accepted 2026-11-09.
**M3: Rendering engine** — accepted 2026-11-09. Layout model: docs/layout-model.md.
**M4: Template designer UI** — accepted 2026-11-09.
**M5: PDF generation in the Worker** — accepted 2026-11-09.
**M6: Workflow definitions and designer** — accepted 2026-11-09.
**M7: Execution engine** — accepted 2026-11-10.
**M8: Execution history and live progress** — accepted 2026-11-10.
**M9: MVP hardening** — accepted 2026-11-10 (full `tools/ci.ps1` run passed). **MVP complete (M1–M9).**
Post-MVP: **Generate DOCX step** (2026-11-12, ADR 0003) implemented; awaiting acceptance. **Approval step** (2026-11-12, ADR 0002) implemented; awaiting acceptance. **Folder input** (2026-11-11, ADR 0001) implemented; awaiting acceptance. No next milestone agreed yet; post-MVP items are listed under "Not in MVP" in plan §9 and the open decisions in plan §10.
Update this line when a milestone is accepted and the next one is agreed.

## Commands

```powershell
powershell -ExecutionPolicy Bypass -File tools/ci.ps1   # everything CI runs (build, frontend, all test projects incl. browser E2E, vulnerable packages); -NoInstall keeps node_modules
dotnet tool restore
dotnet build Verso.slnx
dotnet test Verso.slnx -m:1                 # one project at a time; LocalDB (VERSO_TEST_SQL_SERVER to override), Chromium, Node (E2E builds the SPA)
dotnet tool run dotnet-ef migrations add <Name> --project src/Verso.Infrastructure --startup-project src/Verso.Api --output-dir Persistence/Migrations
dotnet tool run dotnet-ef database update --project src/Verso.Infrastructure --startup-project src/Verso.Api
$env:VERSO_UPDATE_OPENAPI = "1"; dotnet test --filter OpenApiDocument   # after an intended API change; review the diff of docs/openapi/v1.json
$env:VERSO_UPDATE_GOLDEN = "1"; dotnet test --filter Golden            # after an intended rendering change; review the diff of tests/Verso.UnitTests/Rendering/Golden
cd src/verso-web; npm ci; npm run typecheck; npm test; npm run build
cd src/verso-web; npm run gen:api            # after the OpenAPI snapshot changes; regenerates src/lib/api/generated.ts (a test checks it)
powershell -ExecutionPolicy Bypass -File src/Verso.Worker/bin/Debug/net10.0/playwright.ps1 install --only-shell chromium   # once per dev machine (after a build); servers use the offline bundle (docs/setup.md)
```

## Stack (approved 2026-11-09, pending formal bank confirmation)

- Backend: .NET 10 LTS, ASP.NET Core 10 Web API, EF Core 10, SQL Server 2022
- Jobs: Hangfire (open-source, LGPL) with SQL Server storage
- Real-time: SignalR (hub hosted in the API)
- PDF: Playwright for .NET + Chromium, in the Worker only, installed from an offline bundle
- DOCX: DocumentFormat.OpenXml (Open XML SDK, MIT) in `Verso.Rendering.Docx`, Worker only (approved 2026-11-12, ADR 0003)
- Frontend: React 19 + TypeScript 5.x + Vite, @xyflow/react (React Flow), Tailwind CSS 4 + shadcn/ui (radix-ui, lucide-react, class-variance-authority, clsx, tailwind-merge, tw-animate-css), react-router, @fontsource fonts (self-hosted); Node 22 LTS ≥ 22.22, used for builds only
- Frontend tests: Vitest, React Testing Library (+ user-event, jest-dom), jsdom
- Backend tests: xUnit, Microsoft.AspNetCore.Mvc.Testing, LocalDB for integration tests; PdfPig (PDF text extraction, test projects only; approved 2026-11-09)
- Browser E2E: Playwright for .NET (same package and Chromium build as the Worker), `tests/Verso.E2E.Tests` (decided 2026-11-10)
- SignalR browser client: @microsoft/signalr (approved 2026-11-09)
- Identity: Microsoft Entra ID (OIDC; MSAL in the SPA via @azure/msal-browser, JWT bearer in the API via Microsoft.AspNetCore.Authentication.JwtBearer; both approved 2026-11-10); roles mapped from Entra app roles / security groups; a Development-only sign-in with synthetic users (`Auth:Mode`)
- Hosting: Windows Server; API under IIS, Worker as a Windows service (Microsoft.Extensions.Hosting.WindowsServices, approved 2026-11-10); runbook in docs/runbook.md
- CI: `tools/ci.ps1` until the bank names its CI system (decided 2026-11-10)
- Packages: nuget.org / npmjs, versions pinned (`Directory.Packages.props`, lockfiles)

Ask before adding a dependency or changing a version not listed here.

## Architecture rules

- Modular monolith, not microservices. Two processes: `Verso.Api` and `Verso.Worker`. They share code and the database, and nothing else (no direct network calls between them).
- Layered projects: `Verso.Domain` → nothing; `Verso.Rendering` → nothing; `Verso.Rendering.Pdf` → Rendering + Playwright; `Verso.Rendering.Docx` → Rendering + Open XML SDK; `Verso.Application` → Domain + Rendering; `Verso.Infrastructure` → Application (+ Hangfire); `Verso.Api` and `Verso.Worker` → Application + Infrastructure; only `Verso.Worker` also references `Verso.Rendering.Pdf` and `Verso.Rendering.Docx`. Frontend in `src/verso-web`. Keep it lightweight: no abstraction or new project without a concrete need.
- Modules (Templates, Workflows, Executions, Documents, Audit, Ingestion) are folders inside the layered projects. Module folders don't reach into each other's internals. One `VersoDbContext`; each module's tables go in its own SQL schema (`Schemas` class); no cross-schema foreign keys.
- Template designer and workflow designer are separate modules and screens. Workflows reference template **versions**; they never embed template content.
- Rendering lives in `Verso.Rendering` (layout JSON + data → HTML; no dependencies, enforced by RendererPurityTests). Only the Worker uses Playwright/Chromium. The API never runs Chromium. PDFs are printed from the renderer's `Print` parts (body + header/footer templates), not from the preview HTML.
- Migrations are applied with `dotnet ef database update`, never automatically at app startup.
- Published template and workflow versions are immutable. Executions run against published versions only and record the exact versions they used.
- Execution states: `Queued → Running → Succeeded | Rejected | Failed`, plus `AwaitingApproval` while an Approval step waits for a person (ADR 0002). Rejected = data failed validation (not retryable). Failed = technical error (retryable).
- Progress: the Worker writes `ExecutionEvent` rows, and an API hosted service relays them to SignalR. SignalR messages are notifications; the REST API is the source of truth.
- Document storage goes through `IDocumentStore` (configurable). The MVP adapter is the local file system.
- AI features are deferred. No email, print, ERP, SFTP or banking integrations. Folder input (approved 2026-11-11, docs/adr/0001-folder-input.md) is the one real file integration: the Worker reads folders whose paths workflow authors type into the step (recorded and audited when a workflow is published, optionally limited by `FolderInput:AllowedRoots`) or that are named locations, always as its own service identity, and never stores credentials.

## Working rules

**Scope**
- Implement only the current agreed milestone. Don't start the next one or add unrequested features.
- Read existing code before changing it.
- Don't replace or rewrite working features unless the milestone requires it.
- Stop and explain architectural blockers instead of working around them. Ask before deviating from the plan or the rules above.

**Contracts and docs**
- Keep API contracts and documentation in sync: when an endpoint changes, update the OpenAPI snapshot, the generated frontend types, and the plan/ADR in the same change.

**Tests and verification**
- Add tests for new behavior.
- After changes, run the relevant builds, type checks and tests (e.g. `dotnet build`, `dotnet test`, `npm run typecheck`, `npm run test`, `npm run build`).
- Report the commands you ran and their actual results, including failures.
- Never claim tests passed if they weren't run. If something couldn't be run, say so and why.
- Don't hide failures by weakening, skipping or deleting tests or assertions. Fix the cause, or report it.

**Safety**
- Never commit secrets, connection strings with credentials, tokens, or real customer data. Use synthetic data only (`tests/fixtures`, `tools/synthetic-data`).
- Don't apply destructive database changes (dropping or truncating tables or columns, data-losing migrations, resetting databases other than throwaway local/test ones) without approval.
- Never access production systems.
- Don't execute arbitrary user-authored code: no `eval`, scripting engines or raw template HTML/JS. Templates are structured layout JSON; bound values are HTML-encoded; Chromium runs with JavaScript disabled and network requests blocked.
- External integrations stay simulated until explicitly approved (approved so far: folder input).

**Milestone close-out**
- End each milestone with: what was built, verification steps (commands + results, manual checks), how it meets the acceptance criteria, and remaining limitations / open issues.
