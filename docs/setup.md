# Local development setup

Everything runs on one Windows machine. All data is synthetic; nothing here connects to bank systems.

## Prerequisites

| Tool | Version | Check |
|---|---|---|
| .NET SDK | 10.0.x | `dotnet --list-sdks` |
| Node.js | 22 LTS, **22.22 or later** (react-router 8 requires it) | `node --version` |
| SQL Server | LocalDB (ships with Visual Studio / SQL Server Express), or any local SQL Server instance | `sqllocaldb info MSSQLLocalDB` |

The approved production target is SQL Server 2022. LocalDB 2019 works for development today because nothing yet depends on 2022-only features; install SQL Server 2022 Developer edition locally when that changes.

The EF Core CLI is a repo-local tool (pinned in `dotnet-tools.json`), so there's no global install:

```powershell
dotnet tool restore
```

## 1. Database

The development connection string is in `src/Verso.Api/appsettings.Development.json` and `src/Verso.Worker/appsettings.Development.json`:

```
Server=(localdb)\MSSQLLocalDB;Database=Verso_Dev;Integrated Security=True;TrustServerCertificate=True
```

It uses Windows authentication, so it contains no secret. To use another server, don't edit the committed file; override it instead:

```powershell
# per user, stored outside the repo
dotnet user-secrets set "ConnectionStrings:Verso" "Server=.;Database=Verso_Dev;Integrated Security=True;TrustServerCertificate=True" --project src/Verso.Api
# or per shell session
$env:ConnectionStrings__Verso = "Server=...;Database=Verso_Dev;..."
```

Create the database and apply migrations:

```powershell
dotnet tool run dotnet-ef database update --project src/Verso.Infrastructure --startup-project src/Verso.Api
```

Expected: `Done.` The database `Verso_Dev` now has the schemas `templates`, `workflows`, `executions`, `documents`, `audit`, and `__EFMigrationsHistory` lists `…_InitialCreate`.

Migrations are applied by this command only. The API and Worker never migrate the database on startup.

The same migrations also create the `hangfire` schema (background job storage). Hangfire never creates or changes it at startup.

## 2. Chromium (PDF printing, Worker only)

The Worker prints PDFs with Chromium through Playwright for .NET 1.64.1, which is pinned to **Chromium 156.0.8078.4 (Playwright build 1248)**. Only the headless shell is needed. The Worker never downloads a browser at runtime: if Chromium is missing, PDF jobs fail and the Worker logs how to fix it.

**Developer machine** (downloads about 100 MB from Playwright's CDN once, into `%LOCALAPPDATA%\ms-playwright`):

```powershell
dotnet build src/Verso.Worker
powershell -ExecutionPolicy Bypass -File src/Verso.Worker/bin/Debug/net10.0/playwright.ps1 install --only-shell chromium
```

Note: the installer also deletes older Playwright browser builds in that folder that no other installed Playwright still references.

**Servers (offline bundle).** On a machine with internet access, build the same Worker version and download into a dedicated folder:

```powershell
$env:PLAYWRIGHT_BROWSERS_PATH = "C:\verso-browsers"
powershell -ExecutionPolicy Bypass -File src/Verso.Worker/bin/Release/net10.0/playwright.ps1 install --only-shell chromium
Compress-Archive C:\verso-browsers\* verso-chromium-1248.zip
```

Copy the zip to the server, extract it to, for example, `D:\Verso\browsers`, and set `PLAYWRIGHT_BROWSERS_PATH=D:\Verso\browsers` as a machine environment variable (or in the Worker service's environment). The folder must be readable by the Worker's service account. When the Worker starts it logs `Chromium 156.0.8078.4 started for PDF printing`. A Playwright upgrade means a new bundle: the revision is tied to the package version.

Fonts need nothing installed: the document fonts (Schibsted Grotesk, Source Serif 4, IBM Plex Mono; SIL Open Font License) are embedded in `Verso.Rendering.Pdf` and inlined into every printed document.

## 3. Backend

```powershell
dotnet build Verso.slnx
dotnet run --project src/Verso.Api        # http://localhost:5122
dotnet run --project src/Verso.Worker     # separate terminal: runs background jobs (PDF previews, executions), starts Chromium, logs a heartbeat
```

PDF printing settings are in the `Pdf` section of `src/Verso.Worker/appsettings.json`: `RenderTimeout` (default 30 s), `RecycleAfterRenders` (100) and `MaxConcurrentRenders` (2). `Worker:JobWorkers` sets the number of job threads (4).

Archived documents are written by the Worker to `Documents:RootPath`, by default `%LOCALAPPDATA%\Verso\documents` (`staging\…` while a run is in progress, `archive\yyyy\MM\{documentId}.pdf` once archived). The API reads the same folder for downloads, so **the API and the Worker must use the same `Documents:RootPath`** (the default is the same for both when they run as the same user). On a server, give the Worker's service account write access and the API's account read access only.

**Sign-in.** In Development the API uses **Development sign-in** (`Auth:Mode` = `Development` in `appsettings.Development.json`): the SPA lists synthetic users, one per role (Viewer, Operator, Template author, Workflow author, Auditor, Admin) plus one without access, and remembers the choice for the browser tab. Pick **Alex Admin** to do everything, or another user to see what a role allows. API calls from scripts send `Authorization: Bearer dev:<user id>` (for example `dev:dev.operator`). Anywhere else the API requires Entra ID (`Auth:Mode` = `Entra`) and refuses to start with Development sign-in; see [runbook.md](runbook.md) part B.

Check the API directly:

```powershell
Invoke-RestMethod http://localhost:5122/api/health | ConvertTo-Json -Depth 5
```

Expected: `"status": "Healthy"` with a `database` check showing `pendingMigrations: 0`. In Development, the OpenAPI document is at `http://localhost:5122/openapi/v1.json`.

Logs are JSON lines on the console (one object per event, with timestamp, level, category, message, structured state and scopes such as `TraceId`). Each HTTP request logs method, path, status and duration only, never headers or bodies.

## 4. Frontend

```powershell
cd src/verso-web
npm ci
npm run dev        # http://localhost:5180
```

The dev server forwards `/api/*` and the SignalR hub `/hubs/*` (WebSockets) to `http://localhost:5122` (override with `$env:VERSO_API_URL`). Open http://localhost:5180: the top bar shows **API healthy**, and Overview → System status lists the database check.

**Template designer (M4):**
1. Open Template designer, choose **New template**, and enter a key and a name.
2. In the designer, use **Data model → Import** to load a synthetic sample file (for example `tests/fixtures/invoice/sample.json`). The data model is derived from it.
3. Add components or data fields by clicking them, or by dragging them onto the page. Edit the selected element in the properties panel.
4. **Save draft** (Ctrl+S), then **Preview** to see the server-rendered HTML of the saved draft.
5. **Publish**. It stays disabled while the status bar shows errors or unmapped fields.

**PDF preview (M5):** in the preview pane, switch to **PDF**. The API queues a job, the Worker prints the saved draft with Chromium, and the PDF appears when the API's SignalR hub announces it (about 2–3 s once Chromium is warm). It needs the Worker running and Chromium installed (step 2). In development, React's StrictMode runs the effect twice, so each PDF request queues two renders; production builds queue one.

**Workflow designer (M6):**
1. Open Workflow designer, choose **New workflow**, and enter a key (for example `invoice-standard`) and a name. It starts with an API / webhook trigger (mock path, authentication method, expected schema and a synthetic sample filled in).
2. Add steps from the palette by clicking (they go to the right of the selected step) or by dragging them onto the canvas.
3. Connect a step's output (the dots on the right of its card) to the next step's input (the dot on the left) by dragging. Select a step or connection and press Delete to remove it.
4. Select a step to edit its settings. Generate PDF needs a template that has a **published** version.
5. The Issues list and the step cards update as you edit. **Publish** is enabled when there are no errors; warnings (such as an unconnected "Invalid" output) don't block it.

**Execution history and archive (M8):** open **Executions** for every run (newest first, filter by workflow, status, date or correlation id; it updates as runs progress). Open a run for its steps (a rejected run lists each failed rule), documents, activity and, on request, the submitted data (Auditor/Admin only). **Archive** searches archived documents by file name or metadata and downloads them; each download is checked against its SHA-256. The live updates use SignalR through the dev server's `/hubs` proxy and need the API and the Worker running. If your Vite dev server already runs, restart the API and Worker after `database update` and reload the page.

**Running a workflow (M7):** with the API and Worker running, a published template, and a published workflow `invoice-standard` (trigger → validate → generate PDF → archive):

```powershell
$body = @{ data = (Get-Content tests/fixtures/invoice/sample.json -Raw | ConvertFrom-Json) } | ConvertTo-Json -Depth 20
$run = Invoke-RestMethod -Method Post http://localhost:5122/api/v1/workflows/invoice-standard/executions `
  -Headers @{ Authorization = "Bearer dev:dev.operator"; "Idempotency-Key" = [guid]::NewGuid().ToString() } -ContentType application/json -Body $body
Start-Sleep 5; Invoke-RestMethod "http://localhost:5122/api/v1/executions/$($run.executionId)" -Headers @{ Authorization = "Bearer dev:dev.operator" } | ConvertTo-Json -Depth 8
```

Expected: `"status": "Succeeded"` and one document with `"status": "Archived"`; the PDF is under `%LOCALAPPDATA%\Verso\documents\archive`. Submitting `tests/fixtures/invoice/broken.json` instead ends `Rejected`, and the Validate step's `result.issues` lists each failed rule (when the workflow checks those fields). If the Worker is stopped, submissions wait as `Queued` and run when it starts again.

After an API change, regenerate the OpenAPI snapshot (see the commands in CLAUDE.md), then run `npm run gen:api` to regenerate `src/lib/api/generated.ts`. A test fails while that file is out of date.

## 5. Tests

```powershell
powershell -ExecutionPolicy Bypass -File tools/ci.ps1   # everything CI runs, about 5 minutes; -NoInstall keeps node_modules
```

Or piece by piece:

```powershell
dotnet test Verso.slnx -m:1            # needs LocalDB (or set VERSO_TEST_SQL_SERVER), Chromium (step 2) and Node (the E2E suite builds the SPA)
cd src/verso-web; npm run typecheck; npm test; npm run build
```

`-m:1` runs the test projects one after another: the PDF speed test (p95 under 3 s) can fail when the other suites compete for the CPU.

`tests/Verso.E2E.Tests` is the browser suite (Playwright for .NET, the Chromium build the Worker uses): it creates its own database, starts the real API and Worker processes and the production build of the SPA (`vite preview`), and goes through the 7 MVP steps as Alex Admin. It then checks a Viewer's restrictions, the request limits on Kestrel, and that no submitted value appears in either service's log output. Its process logs and any failure screenshots are under `%TEMP%erso-e2e`.

`tools/db/least-privilege.sql` (database roles for servers) is applied to a test database by `LeastPrivilegeTests`, which runs the whole flow under those roles.

`tests/Verso.Pdf.Tests` prints with real Chromium (the M5 acceptance tests: PDF text, pagination, zero network requests, speed, crash recovery). Two of its tests force-kill the processes they started (their own Chromium, found by a unique switch on its command line, and their own Playwright driver, found by its path under the test's output folder); nothing else is touched.

Backend integration tests create uniquely named `Verso_Test_*` databases and drop them afterwards; they never touch `Verso_Dev`. To run them against another SQL Server instance: `$env:VERSO_TEST_SQL_SERVER = "localhost"`.

`ContractTests.OpenApiDocument_MatchesCheckedInCopy` fails when the API changes without updating `docs/openapi/v1.json`. After an intended API change: `$env:VERSO_UPDATE_OPENAPI = "1"; dotnet test --filter OpenApiDocument; Remove-Item Env:VERSO_UPDATE_OPENAPI`, then review the diff and update `docs/api.md`.

`dotnet run` uses the Development environment through the launch profile, and with it Development sign-in.

## 6. Folder input (optional)

The Folder input step reads files from folders **you type in the step**, so there is nothing to configure first. Create two folders, for example `C:\VersoDev\inbound` and `C:\VersoDev\results`. The Worker reaches them as the Windows account it runs as, which on a dev machine is you.

1. In a workflow, replace the trigger with **Folder input**. Type the source folder (`C:\VersoDev\inbound`) and the folders for processed and failed files (for example `C:\VersoDev\results\processed` and `C:\VersoDev\results\failed`; they are created if missing).
2. Press **Test access**, then publish. Publishing records the typed paths so the Worker can use them.
3. Copy `tests/fixtures/invoice/sample.json` into the inbound folder (write it as `inv.json.tmp` and rename it to `inv.json`, so it is never read half-written). Within about 15 seconds the file moves to `results\processed` and the run appears under Executions. Broken data (`broken.json`) ends up in `results\failed` with an `.error.json` file.

For quicker tests, lower the settle time in the Worker's user secrets: `dotnet user-secrets set "FolderInput:SettleTime" "00:00:02" --project src/Verso.Worker`, then restart the Worker. Server setup, including `AllowedRoots` to limit where typed paths may point: [runbook B7](runbook.md#b7-folder-input).

## 7. Read-only database login for Query database steps

The Query database step ([ADR 0019](adr/0019-query-database-step.md)) runs a `SELECT` that a workflow author typed, as the login stored in the Database connection. Verso checks the text and rolls the transaction back, but those are defence in depth: **the connection's login is the real protection and MUST be read-only** (a rollback does not undo sequence values, work done through a linked server or database link, or a function with its own transaction, and SQL Server has no read-only transaction at all): give it SELECT only, on only what the workflows read, and use that login in Configuration, Connections. Examples with synthetic names (replace the password with your own; never commit it):

- **SQL Server:** `CREATE LOGIN verso_ro WITH PASSWORD = '<strong password>'; USE bankdb; CREATE USER verso_ro FOR LOGIN verso_ro; GRANT SELECT ON dbo.customers TO verso_ro;` (grant per table or schema, not `db_datareader` unless everything is meant to be readable).
- **PostgreSQL:** `CREATE ROLE verso_ro LOGIN PASSWORD '<strong password>'; GRANT CONNECT ON DATABASE bankdb TO verso_ro; GRANT USAGE ON SCHEMA public TO verso_ro; GRANT SELECT ON public.customers TO verso_ro;` On PostgreSQL 14 and older every role may create tables in `public` by default: also run `REVOKE CREATE ON SCHEMA public FROM PUBLIC;` (15 and later already do this).
- **MySQL / MariaDB:** `CREATE USER 'verso_ro'@'%' IDENTIFIED BY '<strong password>'; GRANT SELECT ON bankdb.customers TO 'verso_ro'@'%';`
- **Oracle:** `CREATE USER verso_ro IDENTIFIED BY "<strong password>"; GRANT CREATE SESSION TO verso_ro; GRANT SELECT ON app.customers TO verso_ro;` (grant SELECT on the specific tables; do not grant `SELECT ANY TABLE`). The login does not own the tables, so queries must name the schema (`SELECT name FROM app.customers`), or a synonym must exist.

In the Worker's configuration, set `DatabaseConnections:AllowedServers` (for example `"DatabaseConnections": { "AllowedServers": [ "sql.bank.example" ] }`) so the Worker connects only to listed hosts; this limit also applies to Query database steps (`db.server_not_allowed`). Only SQL Server has been verified against a live server; try the other types against a non-production server first. If a query is rejected as unsafe, the message names an `sql.*` code; see the ADR for what the guard refuses.

## 8. Login for Write to database steps

The Write to database step ([ADR 0020](adr/0020-write-database-step.md)) runs one `INSERT`, `UPDATE` or `DELETE` that a workflow author typed, as the login stored in the Database connection, and only on a connection where an administrator has switched on **Allow workflows to change data** (Configuration, Connections; off by default). Verso checks the text and caps the rows a statement may change, but those are defence in depth: **the login's own permissions and the switch are the protection.** A write-capable login must be able to change **only the tables the flows need**, and nothing else (no `db_owner` / `db_datawriter`, no `ANY TABLE` privileges, no DDL, no access to other schemas). An `UPDATE` or `DELETE` with a `WHERE` (and an `INSERT ... SELECT ... WHERE NOT EXISTS`) also reads the table, so grant `SELECT` on the same tables. Examples with synthetic names (replace the password with your own; never commit it):

- **SQL Server:** `CREATE LOGIN verso_rw WITH PASSWORD = '<strong password>'; USE bankdb; CREATE USER verso_rw FOR LOGIN verso_rw; GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.document_log TO verso_rw;`
- **PostgreSQL:** `CREATE ROLE verso_rw LOGIN PASSWORD '<strong password>'; GRANT CONNECT ON DATABASE bankdb TO verso_rw; GRANT USAGE ON SCHEMA public TO verso_rw; GRANT SELECT, INSERT, UPDATE, DELETE ON public.document_log TO verso_rw;` If the table has a `serial` / `bigserial` column (its default calls `nextval` on a sequence), also `GRANT USAGE ON SEQUENCE public.document_log_id_seq TO verso_rw;`. A `GENERATED ... AS IDENTITY` column needs no grant on its sequence (INSERT on the table is enough). On PostgreSQL 14 and older also run `REVOKE CREATE ON SCHEMA public FROM PUBLIC;`.
- **MySQL / MariaDB:** `CREATE USER 'verso_rw'@'%' IDENTIFIED BY '<strong password>'; GRANT SELECT, INSERT, UPDATE, DELETE ON bankdb.document_log TO 'verso_rw'@'%';` **The step assumes InnoDB (transactional) tables.** On MyISAM or MEMORY tables a failed multi-row statement can leave some rows changed, and with a non-strict `sql_mode` data errors become warnings (values are truncated and written); use InnoDB and a strict `sql_mode` for tables the flows write.
- **Oracle:** `CREATE USER verso_rw IDENTIFIED BY "<strong password>"; GRANT CREATE SESSION TO verso_rw; GRANT SELECT, INSERT, UPDATE, DELETE ON app.document_log TO verso_rw;` (grant on the specific tables; never `INSERT ANY TABLE`, `UPDATE ANY TABLE`, `DELETE ANY TABLE` or `SELECT ANY TABLE`). The login does not own the table, so statements must name the schema (`INSERT INTO app.document_log ...`) or a synonym must exist.

**Use a dedicated connection for each purpose** (for example one for the audit table, another for a status table) with its own narrow login, and switch "Allow writes" on **only** for those. A connection used by Query database steps for reading should keep writes off and keep its read-only login (section 7). Any author who can edit a workflow can use any connection whose switch is on, so a login that can write must never be one that can write more than the flows need. The Worker's `DatabaseConnections:AllowedServers` applies to writes too. Only SQL Server has been verified against a live server; try the other types on a non-production database first. How to handle a run that failed with an unknown outcome: [runbook C2](runbook.md#c2-failures).

## 9. Calling APIs from workflows

The Fetch from API step ([ADR 0021](adr/0021-fetch-api-step.md)) makes the **Worker** send one `GET` to an API connection an administrator created (Configuration, Connections, authentication HTTP API, [ADR 0017](adr/0017-api-connections.md)). To set it up on a server:

- **Egress.** The Worker server (not the API server) must be able to reach each API host over **https** (firewall, proxy; the system proxy is used when configured). The Worker refuses loopback, link-local (including `169.254.169.254`), unspecified and multicast addresses when it connects directly, and never follows a redirect. **With a system proxy (`HTTPS_PROXY`, or the service account's proxy settings) that address check applies to the proxy, not to the target:** the proxy resolves the target name, so the proxy / egress itself must block internal and cloud-metadata targets, or the Worker service should run without a system proxy for these calls (the host names are still fixed by the administrator and `AllowedHosts` still applies). Whether the shared handler should ignore the system proxy is an open question for the owner (ADR 0021, Consequences).
- **Set `ApiConnections:AllowedHosts` in production**, in the Worker's configuration, for example `"ApiConnections": { "AllowedHosts": [ "api.frankfurter.dev", "api.bank.example" ] }`. Include the **OAuth token host** for connections that use OAuth client credentials. With the list empty any https host is allowed.
- **Use read-only or least-privilege API keys**, one connection per purpose. Any author who can edit a workflow can use any active connection, with its key, on any path of its host.
- **Prefer a header to "API key in the address".** A key in the address travels in the URL, which the remote server and proxies may log.
- **Limits.** One call has one time limit (1 to 60 seconds, default 15, covering the token request, the call and the reading of the answer) and a largest answer (1 to 256 KB, default 64). The answer must be JSON (`application/json` or `+json`).
- Nothing in Verso has been verified against a live third-party API: try a non-production API first (the sample [fetch-api-exchange-rate.md](samples/fetch-api-exchange-rate.md) uses the public Frankfurter service). What a failure code means: [runbook C2](runbook.md#c2-failures).

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Sign-in page: "The API did not return its sign-in settings" | Verso.Api isn't running (or `VERSO_API_URL` points elsewhere). |
| API fails at startup: "Auth:Entra:TenantId or Auth:Entra:ApiClientId is not configured" | It isn't running in Development (check `ASPNETCORE_ENVIRONMENT` / the launch profile), so it expects Entra ID. |
| A page says your role doesn't allow it, or buttons are missing | Signed in as a user without that role: sign out (bottom of the sidebar) and pick another development user. |
| Top bar shows **API unreachable** | Verso.Api isn't running on port 5122, or `VERSO_API_URL` points elsewhere. |
| **API degraded**, "1 pending migration(s)" | Run the `database update` command in step 1. |
| **API unhealthy**, "Database is unreachable or does not exist" | LocalDB not started or database not created. `sqllocaldb start MSSQLLocalDB`, then step 1. |
| `npm ci` fails with `notsup … node` | Node is older than 22.22. Update Node 22 LTS. |
| API fails at startup: "Connection string 'Verso' is not configured" | Running outside Development without `ConnectionStrings__Verso` set. |
| PDF preview stays on "Rendering PDF in the Worker…" | The Worker isn't running, or it can't reach the database. Previews wait in the queue until a Worker picks them up. |
| PDF preview fails / Worker logs "Chromium for Playwright is not installed" | Install Chromium (step 2). On servers, check `PLAYWRIGHT_BROWSERS_PATH` and that the service account can read it. |
| Execution stays `Queued` | The Worker isn't running or can't reach the database. It runs as soon as a Worker picks it up; if its job was lost, the Worker's dispatcher re-queues it within about 3 minutes. |
| Execution `Failed` with `step.technical_error` | Read the failed step's `error` (e.g. storage folder not writable, Chromium missing). Fix the cause, then `POST /api/v1/executions/{id}/retry`. |
| Worker logs "Could not register recurring jobs" | The `hangfire` schema is missing: run the `database update` command in step 1. |
| `dotnet build` fails: file in `bin\Debug` "being used by another process" | A running API or Worker locks its build output. Stop it, or build and test with `-c Release`. |
