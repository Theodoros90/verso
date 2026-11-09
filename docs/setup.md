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

## 2. Backend

```powershell
dotnet build Verso.slnx
dotnet run --project src/Verso.Api        # http://localhost:5122
dotnet run --project src/Verso.Worker     # separate terminal; logs a DB check and a heartbeat every minute
```

Check the API directly:

```powershell
Invoke-RestMethod http://localhost:5122/api/health | ConvertTo-Json -Depth 5
```

Expected: `"status": "Healthy"` with a `database` check showing `pendingMigrations: 0`. In Development, the OpenAPI document is at `http://localhost:5122/openapi/v1.json`.

Logs are JSON lines on the console (one object per event, with timestamp, level, category, message, structured state and scopes such as `TraceId`). Each HTTP request logs method, path, status and duration only, never headers or bodies.

## 3. Frontend

```powershell
cd src/verso-web
npm ci
npm run dev        # http://localhost:5180
```

The dev server forwards `/api/*` to `http://localhost:5122` (override with `$env:VERSO_API_URL`). Open http://localhost:5180: the top bar shows **API healthy**, and Overview → System status lists the database check.

**Template designer (M4):**
1. Open Template designer, choose **New template**, and enter a key and a name.
2. In the designer, use **Data model → Import** to load a synthetic sample file (for example `tests/fixtures/invoice/sample.json`). The data model is derived from it.
3. Add components or data fields by clicking them, or by dragging them onto the page. Edit the selected element in the properties panel.
4. **Save draft** (Ctrl+S), then **Preview** to see the server-rendered HTML of the saved draft.
5. **Publish**. It stays disabled while the status bar shows errors or unmapped fields.

After an API change, regenerate the OpenAPI snapshot (see the commands in CLAUDE.md), then run `npm run gen:api` to regenerate `src/lib/api/generated.ts`. A test fails while that file is out of date.

## 4. Tests

```powershell
dotnet test Verso.slnx                 # needs LocalDB (or set VERSO_TEST_SQL_SERVER)
cd src/verso-web; npm run typecheck; npm test; npm run build
```

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
