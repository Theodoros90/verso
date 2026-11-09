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
dotnet run --project src/Verso.Worker     # separate terminal: runs background jobs (PDF previews), starts Chromium, logs a heartbeat
```

PDF printing settings are in the `Pdf` section of `src/Verso.Worker/appsettings.json`: `RenderTimeout` (default 30 s), `RecycleAfterRenders` (100) and `MaxConcurrentRenders` (2). `Worker:JobWorkers` sets the number of job threads (4).

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
1. Open Workflow designer, choose **New workflow**, and enter a key (for example `invoice-standard`) and a name. It starts with an API trigger.
2. Add steps from the palette by clicking (they go to the right of the selected step) or by dragging them onto the canvas.
3. Connect a step's output (the dots on the right of its card) to the next step's input (the dot on the left) by dragging. Select a step or connection and press Delete to remove it.
4. Select a step to edit its settings. Generate PDF needs a template that has a **published** version.
5. The Issues list and the step cards update as you edit. **Publish** is enabled when there are no errors; warnings (such as an unconnected "Invalid" output) don't block it.

After an API change, regenerate the OpenAPI snapshot (see the commands in CLAUDE.md), then run `npm run gen:api` to regenerate `src/lib/api/generated.ts`. A test fails while that file is out of date.

## 5. Tests

```powershell
dotnet test Verso.slnx                 # needs LocalDB (or set VERSO_TEST_SQL_SERVER) and Chromium (step 2)
cd src/verso-web; npm run typecheck; npm test; npm run build
```

`tests/Verso.Pdf.Tests` prints with real Chromium (the M5 acceptance tests: PDF text, pagination, zero network requests, speed, crash recovery). Two of its tests force-kill the processes they started (their own Chromium, found by a unique switch on its command line, and their own Playwright driver, found by its path under the test's output folder); nothing else is touched.

Backend integration tests create uniquely named `Verso_Test_*` databases and drop them afterwards; they never touch `Verso_Dev`. To run them against another SQL Server instance: `$env:VERSO_TEST_SQL_SERVER = "localhost"`.

`ContractTests.OpenApiDocument_MatchesCheckedInCopy` fails when the API changes without updating `docs/openapi/v1.json`. After an intended API change: `$env:VERSO_UPDATE_OPENAPI = "1"; dotnet test --filter OpenApiDocument; Remove-Item Env:VERSO_UPDATE_OPENAPI`, then review the diff and update `docs/api.md`.

The API only starts in the `Development` or `Testing` environment: there is no sign-in yet. `dotnet run` uses Development through the launch profile.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Top bar shows **API unreachable** | Verso.Api isn't running on port 5122, or `VERSO_API_URL` points elsewhere. |
| **API degraded**, "1 pending migration(s)" | Run the `database update` command in step 1. |
| **API unhealthy**, "Database is unreachable or does not exist" | LocalDB not started or database not created. `sqllocaldb start MSSQLLocalDB`, then step 1. |
| `npm ci` fails with `notsup … node` | Node is older than 22.22. Update Node 22 LTS. |
| API fails at startup: "Connection string 'Verso' is not configured" | Running outside Development without `ConnectionStrings__Verso` set. |
| PDF preview stays on "Rendering PDF in the Worker…" | The Worker isn't running, or it can't reach the database. Previews wait in the queue until a Worker picks them up. |
| PDF preview fails / Worker logs "Chromium for Playwright is not installed" | Install Chromium (step 2). On servers, check `PLAYWRIGHT_BROWSERS_PATH` and that the service account can read it. |
| Worker logs "Could not register recurring jobs" | The `hangfire` schema is missing: run the `database update` command in step 1. |
| `dotnet build` fails: file in `bin\Debug` "being used by another process" | A running API or Worker locks its build output. Stop it, or build and test with `-c Release`. |
