# Verso runbook

How to set up, deploy, run and recover Verso. Part A gets a developer from a clean Windows machine to a passing CI run; part B installs it on servers; part C is day-to-day operation. All data is synthetic in the MVP; nothing here connects to bank systems.

Reference: [setup.md](setup.md) has more detail per step, [api.md](api.md) the API contract, and the [plan](plan/2026-11-09-verso-mvp-plan.md) the design.

---

## Part A. Developer machine

### A1. Install

| Tool | Version | Check |
|---|---|---|
| .NET SDK | 10.0.x | `dotnet --list-sdks` |
| Node.js | 22 LTS, **22.22 or later** | `node --version` |
| SQL Server LocalDB | ships with Visual Studio or SQL Server Express (or any local SQL Server) | `sqllocaldb info MSSQLLocalDB` |
| Git | any | `git --version` |

### A2. Get the code and build

```powershell
git clone <repository url> verso; cd verso
dotnet tool restore
dotnet build Verso.slnx
powershell -ExecutionPolicy Bypass -File src/Verso.Worker/bin/Debug/net10.0/playwright.ps1 install --only-shell chromium   # Chromium for PDFs and E2E, once
cd src/verso-web; npm ci; cd ../..
```

### A3. Database

```powershell
dotnet tool run dotnet-ef database update --project src/Verso.Infrastructure --startup-project src/Verso.Api
```

This creates `Verso_Dev` on `(localdb)\MSSQLLocalDB` (connection string in `src/Verso.Api/appsettings.Development.json`, Windows authentication, no secret). Migrations run only by this command, never at startup.

### A4. Run

Three terminals:

```powershell
dotnet run --project src/Verso.Api        # http://localhost:5122
dotnet run --project src/Verso.Worker     # background jobs, Chromium
cd src/verso-web; npm run dev             # http://localhost:5180
```

Open http://localhost:5180. **Development sign-in** lists synthetic users, one per role (plus one without access): pick **Alex Admin** to do everything, or a role to see what it allows. This sign-in exists only when the API runs in the Development environment; anywhere else the API refuses to start with it.

### A5. The MVP in five minutes

1. Template designer → **New template** → Data model → **Import** `tests/fixtures/invoice/sample.json` → click fields to add them → **Save draft** → **Publish**.
2. Workflow designer → **New workflow** (key `invoice-standard`) → add Validate data, Generate PDF, Archive from the palette → connect each step's output to the next step's input → in Generate PDF choose the template → **Save draft** → **Publish**.
3. Submit synthetic data (an Operator or Admin token; in Development the token is `dev:<user id>`):

   ```powershell
   $body = @{ data = (Get-Content tests/fixtures/invoice/sample.json -Raw | ConvertFrom-Json) } | ConvertTo-Json -Depth 20
   Invoke-RestMethod -Method Post http://localhost:5122/api/v1/workflows/invoice-standard/executions `
     -Headers @{ Authorization = "Bearer dev:dev.operator"; "Idempotency-Key" = [guid]::NewGuid().ToString() } `
     -ContentType application/json -Body $body
   ```

4. **Executions** shows the run live; open it to download the PDF. Submit `tests/fixtures/invoice/broken.json` to see a Rejected run with its failed rules. **Archive** searches documents; **Audit trail** shows who did what.

### A6. Verify everything (what CI runs)

```powershell
powershell -ExecutionPolicy Bypass -File tools/ci.ps1            # add -NoInstall to keep node_modules (e.g. while `npm run dev` runs)
```

About 5 minutes. It builds, runs the frontend checks, the backend unit, API, PDF and browser E2E tests (the 7 MVP steps against real API and Worker processes), and checks packages for known vulnerabilities. It ends with `CI passed.` Test databases are named `Verso_Test_*`, and each is dropped when its tests finish.

---

## Part B. Servers (Windows Server)

Verso is two processes sharing one SQL Server database: **Verso.Api** (IIS) and **Verso.Worker** (Windows service), plus the SPA as static files. They never call each other.

### B1. Prerequisites

- **SQL Server 2022**, one database (for example `Verso`), with backups (C3).
- **API host:** IIS with the **ASP.NET Core 10 Hosting Bundle**. To serve the SPA from the same site, also URL Rewrite and Application Request Routing, with WebSockets enabled (the live updates use them).
- **Worker host:** the .NET 10 runtime, and Chromium from the offline bundle (B5). The API and Worker may share a server.
- **Document folder** (`Documents:RootPath`): a local or network folder. The Worker's account writes to it, the API's account only reads it, and nobody else has access.
- Two service accounts (for example gMSAs): one for the API app pool, one for the Worker service.

### B2. Database

1. Create the database, then apply the migrations as the deploy identity (a DBA account, not the applications). Either run the command below from a build machine with network access, or generate an idempotent script with `dotnet tool run dotnet-ef migrations script --idempotent --project src/Verso.Infrastructure --startup-project src/Verso.Api -o verso.sql` and hand it to the DBA:

   ```powershell
   $env:ConnectionStrings__Verso = "Server=<sql>;Database=Verso;Integrated Security=True;TrustServerCertificate=False"
   dotnet tool run dotnet-ef database update --project src/Verso.Infrastructure --startup-project src/Verso.Api
   ```

2. Run [`tools/db/least-privilege.sql`](../tools/db/least-privilege.sql) (idempotent; again after every upgrade), then map the accounts:

   ```sql
   CREATE USER [CORP\svc-verso-api]    FOR LOGIN [CORP\svc-verso-api];    ALTER ROLE verso_api    ADD MEMBER [CORP\svc-verso-api];
   CREATE USER [CORP\svc-verso-worker] FOR LOGIN [CORP\svc-verso-worker]; ALTER ROLE verso_worker ADD MEMBER [CORP\svc-verso-worker];
   ```

   The roles grant only what each process needs. Audit entries and archive records are insert/select only, nothing in templates, workflows, executions or documents can be deleted, and neither role can change the schema. A test runs the whole flow under these roles and checks the refusals.

### B3. Entra ID (by the bank's identity team)

1. **API app registration:** expose an API with the scope `access_as_user` (the Application ID URI defaults to `api://<api client id>`). Define the app roles `Verso.Viewer`, `Verso.Operator`, `Verso.TemplateAuthor`, `Verso.WorkflowAuthor`, `Verso.Auditor` and `Verso.Admin`.
2. **SPA app registration:** platform "Single-page application", redirect URI = the SPA's URL (for example `https://verso.bank.example/`). Add an API permission to the API's `access_as_user` and grant admin consent.
3. Under Enterprise applications → Verso API → Users and groups, **assign security groups to the app roles**. App roles avoid the group-overage problem; raw group claims also work through `Auth:RoleMap` (B4).

What each role may do is in [api.md](api.md#roles) (the endpoint × role matrix, tested automatically).

### B4. Configure and install the API (IIS)

Publish with `dotnet publish src/Verso.Api -c Release -o <folder>`, create an IIS site and app pool (No Managed Code, identity `svc-verso-api`), and set the configuration with environment variables on the app pool or an `appsettings.Production.json` next to the dll. Never commit it with secrets.

| Setting | Value |
|---|---|
| `ConnectionStrings__Verso` | `Server=<sql>;Database=Verso;Integrated Security=True;Encrypt=True` (Windows authentication, no password) |
| `Auth__Mode` | `Entra` (the default; `Development` is refused outside Development) |
| `Auth__Entra__TenantId` / `Auth__Entra__ApiClientId` | the tenant id and the **API** registration's client id (startup fails without them) |
| `Auth__Entra__SpaClientId` / `Auth__Entra__Scope` | the **SPA** registration's client id; `api://<api client id>/access_as_user` |
| `Auth__RoleMap__<group object id>` | optional, for example `Operator`, when groups (not app roles) carry the roles |
| `Documents__RootPath` | the document folder (read-only for this account) |
| `Cors__AllowedOrigins__0` | only if the SPA is served from another origin |
| `Limits__SubmissionsPerMinute` | per-user submission limit (default 120); `Limits__MaxRequestBodyBytes` (default 2 MB) |

The API never runs Chromium, and the OpenAPI document isn't served outside Development and Testing.

**SPA:** run `npm ci; npm run build` in `src/verso-web` and copy `dist/` to the site's root. Rewrite `/api/*` and `/hubs/*` to the API (ARR reverse proxy, WebSockets on), and send every other unknown path to `index.html` (client-side routes). Alternatively, host the SPA on its own origin and set `Cors__AllowedOrigins`.

### B5. Chromium (offline bundle) and the Worker service

1. On a machine with internet access, build the same Worker version and download Chromium into a dedicated folder ([setup.md](setup.md#2-chromium-pdf-printing-worker-only)):

   ```powershell
   $env:PLAYWRIGHT_BROWSERS_PATH = "C:\verso-browsers"
   powershell -ExecutionPolicy Bypass -File src/Verso.Worker/bin/Release/net10.0/playwright.ps1 install --only-shell chromium
   Compress-Archive C:\verso-browsers\* verso-chromium-1248.zip
   ```

2. On the server, extract it to, for example, `D:\Verso\browsers`, and give the Worker's account read access.
3. Publish (`dotnet publish src/Verso.Worker -c Release -o D:\Verso\worker`), put the configuration in `D:\Verso\worker\appsettings.Production.json` (or service environment variables): `ConnectionStrings:Verso`, `Documents:RootPath` (write access), and the machine environment variable `PLAYWRIGHT_BROWSERS_PATH=D:\Verso\browsers`. Then create the service:

   ```powershell
   sc.exe create "Verso Worker" binPath= "D:\Verso\worker\Verso.Worker.exe" obj= "CORP\svc-verso-worker$" start= delayed-auto
   sc.exe failure "Verso Worker" reset= 86400 actions= restart/60000/restart/60000/restart/300000
   sc.exe start "Verso Worker"
   ```

   As a service it logs to the console sinks and to the Windows Event Log (source "Verso.Worker", warnings and errors). At start it logs `Chromium 156.0.8078.4 started for PDF printing`. Several Workers may run against one database; Hangfire and the execution locks share the work.

### B6. Smoke test after a deployment

1. `GET /api/health` returns `Healthy` with `pendingMigrations: 0`.
2. Sign in as a test user with Verso.Admin: the top bar shows **API healthy**.
3. Submit `tests/fixtures/invoice/sample.json` to a published test workflow: **Succeeded** within seconds, and the download matches its SHA-256 (the API checks it).
4. The Audit trail shows the submission and the download.

### B7. Folder input (approved locations)

The `folder-input` step ([ADR 0001](adr/0001-folder-input.md)) makes the **Worker** read files from folders and move them afterwards. Only folders approved here can be used. Workflow designers pick them by key and may add a relative subfolder, but never type a server path.

1. **Create the folders** on a local disk or a file server, for example `\fileserver\verso\ap-inbound` (where senders drop files) and `\fileserver\verso\ap-results` (processed and failed files). Never approve a drive or share root.
2. **Grant the Worker's service account** (for example the gMSA `CORP\svc-verso-worker$`) **Modify** on source folders: Verso moves files into a `.verso-inflight` subfolder, so read-only isn't enough. Grant **Modify** on output folders too: it creates subfolders and files. On a share, grant both share and NTFS permissions. The API's account needs no access. No passwords are stored anywhere in Verso: the Worker uses its own identity. On **Linux**, mount the share with the OS (for example in `/etc/fstab`, with a root-only credentials file) and approve the mount path, for example `/mnt/verso/ap-inbound`.
3. **Approve the locations** in the Worker's configuration (`appsettings.Production.json` or service environment variables). Keys are 2–64 lower-case letters, digits and `-`:

   ```json
   "FolderInput": {
     "Locations": {
       "ap-inbound": { "Name": "AP inbound (file server)", "Path": "\\fileserver\verso\ap-inbound", "Input": true },
       "ap-results": { "Name": "AP results (file server)", "Path": "\\fileserver\verso\ap-results", "Output": true }
     }
   }
   ```

   Optional: `PollInterval` (default `00:00:15`), `SettleTime` (`00:00:10`), `MaxFileBytes` (1048576). **Every Worker must have the same locations**, because a file may be handled by any Worker.
4. **Restart the Worker.** It reports the locations (key, name, purpose; never the path) at startup and every 5 minutes. A location with an invalid path is listed as unavailable with the reason, and the Worker log shows it. Locations removed from the configuration are disabled, not deleted.
5. In the workflow designer, choose the locations on the Folder input step and press **Test access**. The Worker checks each folder as its own account and shows the account it used.
6. **Tell senders the handoff rule:** write the file under a temporary name (for example `inv-1.json.tmp`) and rename it when complete. Leave `.verso-inflight` alone.

---

## Part C. Operations

### C1. Logs and correlation

Both processes write JSON log lines (timestamp, level, category, message, scopes). Every API request has a **correlation id**: the caller's `X-Correlation-Id` when well-formed, otherwise a new one, returned in the response header. A submission stores it on the execution, and every Worker log line for that run carries `CorrelationId` and `ExecutionId`, so one id follows a run through both processes. Request logs hold method, path, status and duration only. **No payload values are ever logged.** Tests check that on every submission path, and the E2E suite checks the real processes' output.

### C2. Failures

| Symptom | What to do |
|---|---|
| Run **Rejected** | The data is wrong (the run view lists each failed rule). The sender corrects it and submits again with a new Idempotency-Key. Not retryable. |
| Run **Failed** | A technical error persisted through 3 attempts (storage, Chromium, timeout); read the failed step's error. Fix the cause, then retry from the run view or `POST /api/v1/executions/{id}/retry` (Operator/Admin). |
| Runs stay **Queued** | No Worker is running or it can't reach the database. Queued runs start once a Worker is up; lost jobs are re-queued within about 3 minutes. |
| Download returns 409 `document.integrity_failed` | The archived file no longer matches its recorded SHA-256. Nothing is served. Restore the file from backup (C3) and investigate. |
| API won't start: "Auth:Entra:TenantId … not configured" | Configure Entra (B4). |
| 401 for everyone | Wrong tenant or client id, or the token's audience isn't the API registration. |
| 403 for a user | The user lacks the Verso role (B3). |
| Folder input: workflow shows **Access denied** or **Folder unavailable** | The Worker's account lacks Modify on the folder, or the share is unreachable from the Worker's server. Fix it, then press **Test access**. Files wait; nothing is lost. |
| Files pile up in `.verso-inflight` | Their runs are still Queued or Running (see C2 above), or the destination is unreachable (the workflow's recent files show "Not moved yet"). They are moved once that is fixed. |
| A file landed in the failed folder | Read `<name>.error.json` next to it. `file.duplicate`: the same data already ran. A Failed run can be retried from the run view, but its file stays in the failed folder. |

### C3. Backup and restore

- **What to back up:** the SQL database (templates, workflows, executions, audit, archive records and Hangfire jobs) and the document folder (`archive\yyyy\MM\*.pdf`). Neither is useful without the other.
- **Database:** full backups plus transaction log backups (recovery model FULL), at the bank's usual RPO.
- **Documents:** back up the folder at least as often as the database, and **after** the database backup. A document is written to the folder before its archive record is committed, so a folder backup taken after a database backup contains every document that backup references.
- **Restore:** restore the database first, then the folder to its configured path. Afterwards, every archive record's file must exist and match its SHA-256 (the API checks this on each download). Files without a record (a run that crashed before committing) are harmless.
- Archive records and audit entries are write-once in the database (B2). True WORM storage depends on the production storage target, which is still open (plan D6).

### C4. Retention

Each archive record keeps `retentionUntil` (7 or 10 years from archiving, or permanent, set per workflow). The MVP records it only: **nothing is deleted at expiry.** Disposal is a later, separate decision.

### C5. Upgrades

1. Back up (C3).
2. Stop the Worker service. Queued runs wait; running ones resume after the restart, without duplicate documents.
3. Apply migrations as the deploy identity (B2), then re-run `tools/db/least-privilege.sql`.
4. Deploy the API, the SPA and the Worker. A new Playwright version needs a new Chromium bundle (B5).
5. Start the Worker and run the smoke test (B6).
