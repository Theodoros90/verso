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

### B7. Folder input

The `folder-input` step ([ADR 0001](adr/0001-folder-input.md)) makes the **Worker** read files from folders and move them afterwards. **Workflow authors type the folder paths in the step** (source, processed, failed), as the Worker's server sees them. There is no approval step: the safety lies in what the Worker's own account can reach, in the audit trail (each new path is recorded with who typed it, `FolderPathRegistered`), and optionally in `AllowedRoots` (step 2). Named locations (step 4) are optional; they remain for older workflows.

1. **Create the folders** on a local disk or a file server, for example `\\fileserver\verso\ap-inbound` (where senders drop files) and `\\fileserver\verso\ap-results` (processed and failed files). Never use a drive or share root.
2. **Grant the Worker's service account** (for example the gMSA `CORP\svc-verso-worker$`) **Modify** on source folders: Verso moves each file out of the source folder while it works on it (into a hidden `.verso-inflight` folder inside the **processed** folder), so read-only isn't enough. Grant **Modify** on output folders too: it creates subfolders and files. On a share, grant both share and NTFS permissions. The API's account needs no access. No passwords are stored anywhere in Verso: the Worker uses its own identity. On **Linux**, mount the share with the OS (for example in `/etc/fstab`, with a root-only credentials file) and type the mount path, for example `/mnt/verso/ap-inbound`. Typed paths work for any folder that account can reach, so grant it only the folders Verso should use.
3. **Recommended: limit where typed paths may point.** Set `FolderInput:AllowedRoots` on every Worker, for example:

   ```json
   "FolderInput": {
     "AllowedRoots": [ "\\\\fileserver\\verso", "D:\\Verso" ]
   }
   ```

   Typed paths must then lie inside one of those folders; anything else is reported as a scan error ("isn't inside a folder this server allows…"). Locations in the Worker's own configuration (step 4) are not limited by it. Without `AllowedRoots`, any folder the Worker's account can reach can be typed.
4. **Optional: named locations in the Worker's configuration** (`appsettings.Production.json` or service environment variables). Keys are 2–64 lower-case letters, digits and `-`. Older workflows that chose an approved location keep using them, and they can be selected by `location` in access tests:

   ```json
   "FolderInput": {
     "Locations": {
       "ap-inbound": { "Name": "AP inbound (file server)", "Path": "\\\\fileserver\\verso\\ap-inbound", "Input": true },
       "ap-results": { "Name": "AP results (file server)", "Path": "\\\\fileserver\\verso\\ap-results", "Output": true }
     }
   }
   ```

   Optional: `PollInterval` (default `00:00:15`), `SettleTime` (`00:00:10`), `MaxFileBytes` (1048576). **Every Worker must have the same configuration**, because a file may be handled by any Worker. Restart the Worker after changing it.
5. **In the workflow designer**, type the three paths on the Folder input step and press **Test access**. The Worker checks each folder as its own account and shows the account it used. Publishing records the typed paths so the Worker can resolve them (also after restarts and newer workflow versions); each new path is audited.
6. **Tell senders the handoff rule:** write the file under a temporary name (for example `inv-1.json.tmp`) and rename it when complete. Leave `.verso-inflight` alone.

### B8. Send email

The `send-email` step ([ADR 0006](adr/0006-send-email.md)) makes the **Worker** send mail through the bank's SMTP relay. Only the Worker needs this; the API never sends mail. Without it, a workflow can still be designed and published, but a run reaching a Send email step fails with `email.not_configured`.

1. **Ask the mail team for a relay** that accepts the Worker server (by IP or by an authenticated account), supports TLS on the port (usually 587), and allows the `From` address.
2. **Configure the Worker** (`appsettings.Production.json` or service environment variables):

   ```json
   "Email": {
     "Host": "smtp.bank.example",
     "Port": 587,
     "EnableSsl": true,
     "UserName": "svc-verso-mail",
     "From": "no-reply@bank.example",
     "FromName": "Verso",
     "AllowedRecipientDomains": [ "bank.example" ]
   }
   ```

   Set the password only as the environment variable `Email__Password` on the service (never in a file in the repository). Leave `UserName` empty for a relay that trusts the server's IP.
3. **Recommended: set `AllowedRecipientDomains`.** When it is not empty, a message with any recipient outside those domains is refused (`email.recipient_not_allowed`). Start with internal domains and test with a mail catcher outside production.
4. **Every Worker needs the same settings**, because any Worker may run a step. Restart the Worker after changing them.
5. **Check:** run a workflow with a Send email step to an internal address and look for the message; the step result in the execution history shows the message id (not the address).
6. **Limits:** delivery is at-least-once (a duplicate is possible after a Worker stop or relay timeout); bounces are not tracked; attachments up to 10 MB.
7. **More than one relay** ([ADR 0006 addendum 1](adr/0006-send-email.md)). The settings above are the relay named `default`, used by every Send email step that names none, so existing workflows keep working. To add others, give each a key (lower-case letters, digits and hyphens) under `Email:Relays`, with the same fields as above:

   ```json
   "Email": {
     "Host": "smtp.bank.example", "From": "no-reply@bank.example",
     "Relays": {
       "statements": { "Host": "smtp-statements.bank.example", "UserName": "svc-verso-stmt", "From": "statements@bank.example", "AllowedRecipientDomains": [ "customers.example" ] },
       "internal":   { "Host": "smtp.bank.example", "From": "verso-internal@bank.example", "AllowedRecipientDomains": [ "bank.example" ] }
     }
   }
   ```

   - Each relay has its own `AllowedRecipientDomains`, sender and credentials. Passwords only as environment variables: `Email__Relays__statements__Password`.
   - Workflow authors type the key in the step's **Email relay** setting (empty means `default`). A key that is not configured ends the run **Failed** with `email.relay_unknown`; a relay without `Host` or `From` fails with `email.not_configured`. Neither is retried.
   - **Keys must match on every server and Worker**, and across test and production, because the workflow stores the key, not the host. Restart the Worker after adding a relay.
   - The step result in the execution history shows the relay key.

8. **Relays managed in the app, and the master key** ([ADR 0015](adr/0015-email-relays-in-the-app.md)). An administrator can add relays and type their passwords in **Configuration, Connections, Email relays**; the API stores them in `mail.EmailRelays` and the password encrypted. This needs a **master key** that is **not** in the database:
   1. Make a key once: `[Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))` (PowerShell).
   2. Set it as the environment variable `Secrets__MasterKey` (or in the bank's secret store) for the **API and every Worker**: the same value everywhere. Restart them.
   3. Keep a sealed copy. **If the key is lost, the stored passwords can't be read and must be typed again.** Changing it has the same effect. Never put it in the repository, a ticket or a chat.
   - Without a master key the API refuses to save a password ("Secrets:MasterKey"), but a relay without a password still works. A Worker with a missing or different key fails the step with `email.password_unreadable` (not retried).
   - Apply the migration first: `dotnet tool run dotnet-ef database update --project src/Verso.Infrastructure --startup-project src/Verso.Api` (creates `mail.EmailRelays`); on a locked-down database also run `tools/db/least-privilege.sql` again (the API can write the `mail` schema, the Worker only read it; nothing can delete a relay).
   - **Test connection:** in Configure, type a synthetic address and press Test connection: the Worker sends one short message through the saved settings and the dialog shows `Succeeded` or the error code. It needs the Worker running (otherwise "The Worker has not run the test yet"). Workflow authors choose the connection in the Send email step from a dropdown.
   - A relay in the app wins over one with the same key under `Email:Relays`; configuration-only setups keep working unchanged. Deactivating a relay in the app makes steps that name it fail with `email.relay_unknown`.
   - Gmail needs an *app password* (2-step verification on); Microsoft 365 often requires OAuth, which Verso does not support. Use synthetic recipients while testing.

### B8b. Database connections (SQL Server, PostgreSQL, MySQL / MariaDB, Oracle; test only)

An administrator can add a database connection in **Configuration, Connections, New connection, authentication Database login** (choose the **Database type**: SQL Server, PostgreSQL, MySQL / MariaDB or Oracle; see [ADR 0016 addendum 1](adr/0016-database-connections.md)) ([ADR 0016](adr/0016-database-connections.md)). It is stored in `connections.DatabaseConnections` with the password encrypted. It needs the **same master key as email relays** (`Secrets__MasterKey` on the API and every Worker, see B8 item 8). **Test connection** only connects and runs `SELECT 1`; the **Query database** step ([ADR 0019](adr/0019-query-database-step.md)) also uses it to run a `SELECT` a workflow author typed (see C2 for failures and the resume behaviour).

1. **Use a read-only login** (SELECT only on what workflows read; the login is the real protection for Query database steps, see [setup.md section 7](setup.md)). **SQL Server behind an Availability Group listener:** Verso connects with `ApplicationIntent=ReadOnly`, so with read-only routing a query may be answered by a readable secondary that lags behind the primary (stale data); point the connection at the primary or a listener without read-only routing if that matters. For first tests, use a non-production server. The connection is real: the Worker server must be able to reach the host and port (firewall).
2. **Apply the migration** (`dotnet tool run dotnet-ef database update --project src/Verso.Infrastructure --startup-project src/Verso.Api`, creates the `connections` schema) and, on a locked-down database, run `tools/db/least-privilege.sql` again.
3. **Recommended in production:** set `DatabaseConnections:AllowedServers` in the Worker's configuration, for example `"DatabaseConnections": { "AllowedServers": [ "sql.bank.example" ] }`. The Worker then connects only to those hosts (the name before an instance or port); anything else fails with `db.server_not_allowed`.
4. **Encryption:** leave "Encrypt the connection" on. If the test fails with `db.certificate_untrusted`, install the server's certificate authority on the Worker server; "Trust the server certificate" is for non-production servers only.
5. **Check:** Configure, Test connection. The dialog shows the server version, or one of Verso's error codes (`db.login_failed`, `db.unreachable`, `db.timeout`, `db.cannot_open_database`, `db.password_expired`, `db.password_unreadable`). The Worker must be running.
6. A connection can be switched off but not deleted. Rotate the password by typing a new one in Configure.
7. **Drivers.** PostgreSQL (Npgsql), MySQL / MariaDB (MySqlConnector) and Oracle (Oracle.ManagedDataAccess.Core) ship in the **Worker** only (project `Verso.Databases`); the API loads none. The Oracle driver is under the Oracle Free Use Terms (not open source): get the bank's licence review before deploying it.
8. **Ports and defaults:** leave Port empty for the default (SQL Server 1433, PostgreSQL 5432, MySQL / MariaDB 3306, Oracle 1521). Only SQL Server accepts `\INSTANCE` or `,port` in the server name. **Oracle** needs the **service name** (for example `ORCLPDB1`) and checks the server certificate against the Worker's certificate store or wallet when "Encrypt" is on (TCPS); it has no trust-anything switch.
9. **Try each type** with Test connection against a non-production server before relying on it: these probes are tested without a live server.
10. **Writes (Write to database step, [ADR 0020](adr/0020-write-database-step.md)).** A connection runs INSERT / UPDATE / DELETE only when an administrator turns on **Allow workflows to change data** on it (off by default; the change is audited as `DatabaseConnectionWritesChanged`). Turn it on only for a connection whose login can write just the tables the flows need ([setup.md section 8](setup.md)), one connection per purpose. Applying the `DatabaseConnectionAllowWrites` migration adds the column with writes off for every existing connection. See C2 for failed writes.

### B8c. API connections (HTTP; Test connection and the Fetch from API step)

An administrator can add a web API in **Configuration, Connections, New connection, authentication HTTP API** ([ADR 0017](adr/0017-api-connections.md)): base address, a test request path, and how the API is secured (none, API key in a header or in the address, bearer token, basic login, or OAuth client credentials). It is stored in `connections.ApiConnections` with the secret encrypted, under the **same master key** as email relays and database connections (`Secrets__MasterKey` on the API and every Worker, see B8 item 8). **Test connection** sends **one GET**, and the **Fetch from API** step ([ADR 0021](adr/0021-fetch-api-step.md)) uses it to read JSON with GET requests from workflows (see C2 for failures and [setup.md section 9](setup.md)). Anyone who can edit a workflow may use any active connection: give each its own read-only or least-privilege key.

1. **Egress.** The Worker server must be able to reach the host over **https** (firewall, proxy; the system proxy is used when configured). Only https addresses are accepted, redirects are not followed, and the Worker does not connect directly to loopback, link-local (including `169.254.169.254`), unspecified or multicast addresses. **With a system proxy that check applies to the proxy, not to the target** (the proxy resolves the target name): the proxy / egress must itself block internal and cloud-metadata targets, or the Worker service should run without a system proxy for these calls. Whether the shared handler should ignore the system proxy is an open owner question (ADR 0021, Consequences).
2. **Apply the migration** (`dotnet tool run dotnet-ef database update --project src/Verso.Infrastructure --startup-project src/Verso.Api`, adds the API tables to the `connections` schema) and, on a locked-down database, run `tools/db/least-privilege.sql` again.
3. **Recommended in production:** set `ApiConnections:AllowedHosts` in the Worker's configuration, for example `"ApiConnections": { "AllowedHosts": [ "api.frankfurter.dev", "api.bank.example" ] }`. The Worker then calls only those hosts (for OAuth, both the API host and the token host); anything else fails with `api.host_not_allowed` before any request.
4. **Certificates:** if the test fails with `api.certificate_untrusted`, install the API's certificate authority on the Worker server. There is no "trust anything" switch for API connections.
5. **Check:** Configure, Test connection. The dialog shows the HTTP status, the time, the content type and the size, or one of Verso's error codes (`api.unauthorized`, `api.not_found`, `api.unreachable`, `api.timeout`, `api.token_failed`, ...). The response body is never kept or shown. The Worker must be running.
6. **Keys in the address** ("API key in the address") travel in the URL, which the remote server and proxies may log: use a header where the API allows it. A connection can be switched off but not deleted; rotate a secret by typing a new one in Configure.

### B8d. SMS connections (Brevo; Test connection and the Send SMS step)

An administrator can add an SMS connection in **Configuration, Connections, New connection, authentication SMS (Brevo)** ([ADR 0018](adr/0018-sms-connections.md)): sender, type (transactional or marketing), Unicode, organisation prefix, base address and the Brevo API key. It is stored in `connections.SmsConnections` with the key encrypted, under the **same master key** as the other connections (`Secrets__MasterKey` on the API and every Worker, see B8 item 8). **Test connection** sends **one real SMS** to a number you type, which uses one credit. The **Send SMS** workflow step ([ADR 0022](adr/0022-send-sms-step.md)) sends real SMS to numbers from a run's data, only through a **transactional** connection on which an administrator has switched **Allow workflows to send SMS** on, and only to numbers starting with one of the connection's **allowed country prefixes** (empty = no country). Setup: [setup.md section 10](setup.md); failures: C2.

1. **Egress.** The Worker server must reach `api.brevo.com` over **https** (firewall, proxy). Only https addresses are accepted, redirects are not followed, and the Worker never connects to loopback, link-local, unspecified or multicast addresses.
2. **Apply the migrations** (`dotnet tool run dotnet-ef database update --project src/Verso.Infrastructure --startup-project src/Verso.Api`: adds the SMS tables to the `connections` schema, and `SmsConnectionSending` adds the sending switch and the allowed prefixes; existing connections stay off) and, on a locked-down database, run `tools/db/least-privilege.sql` again.
3. **Recommended in production:** set `SmsConnections:AllowedHosts` in the Worker's configuration, for example `"SmsConnections": { "AllowedHosts": [ "api.brevo.com" ] }`. Anything else fails with `sms.host_not_allowed` before any request.
4. **Sender and country rules.** Some countries restrict or pre-register alphanumeric senders, and marketing SMS needs the recipient's consent and opt-out wording. Check Brevo's rules for the destination before relying on a sender.
5. **Check:** Configure, Test connection, type your own number in international format (for example `+357 99 000000`). The dialog shows the HTTP status, the time, the SMS parts, the credits left and Brevo's message id, or one of Verso's error codes (`sms.unauthorized`, `sms.invalid_recipient`, `sms.insufficient_credits`, ...). The number and the text are never kept. The Worker must be running. A connection can be tested once every 30 seconds.
6. **The API key.** Never put it in the repository, a ticket or a chat. If it was exposed, rotate it in Brevo and type the new one in Configure. A connection can be switched off but not deleted.
7. **Sending from workflows.** Switch **Allow workflows to send SMS** on only after Test connection worked, and type the allowed country prefixes (for example `357`). To stop all workflow SMS at once through a connection, switch sending off (steps then fail with `sms.sending_not_allowed`) or deactivate the connection (`sms.connection_unavailable`): either way nothing is sent and the key is not read.

### B9. Print queue

The `print-queue` step ([ADR 0007](adr/0007-print-queue.md)) makes the **Worker** write a PDF and a small job file into a spool folder that the print system (or the print house's import job) picks up. Workflow authors type only a queue **key**; the folder behind it is yours to set. Without a matching queue, a run reaching the step fails with `print.queue_unknown`.

1. **Create one folder per queue** on a local disk or file server, for example `\\fileserver\verso\print\cyprus-print-nightly`. Never use a drive or share root.
2. **Grant the Worker's service account** (for example the gMSA `CORP\svc-verso-worker$`) **Modify** on it (it creates and renames files there). No passwords are stored in Verso.
3. **Configure the Worker** (`appsettings.Production.json` or service environment variables). Keys are 2 to 64 lower-case letters, digits and `-`; tell workflow authors the keys:

   ```json
   "Print": {
     "Queues": {
       "cyprus-print-nightly": { "Name": "Cyprus Print Services, nightly", "Path": "\\\\fileserver\\verso\\print\\cyprus-print-nightly" },
       "limassol-inhouse":     { "Name": "In-house printer, Limassol",     "Path": "D:\\Verso\\print\\limassol" }
     }
   }
   ```

   Every Worker needs the same queues, because any Worker may run a step. Restart the Worker after changing them.
4. **The print system's side.** For each job it finds `{jobId}.json` (written last, so the PDF is already complete). The JSON names the PDF (`file`) and says how to print it (`envelope`: `dl-window` or `c4`, `duplex`, `fileName`, `sha256`). It should delete both files after taking the job. Ignore files ending in `.tmp`.
5. **Check:** run a workflow with a Print queue step and look in the folder for the two files; the step result in the execution history shows the job id.
6. **Limits:** `queued` means the files are in the folder, nothing comes back from the print system; PDFs only; one copy.

---

## Part C. Operations

### C1. Logs and correlation

Both processes write JSON log lines (timestamp, level, category, message, scopes). Every API request has a **correlation id**: the caller's `X-Correlation-Id` when well-formed, otherwise a new one, returned in the response header. A submission stores it on the execution, and every Worker log line for that run carries `CorrelationId` and `ExecutionId`, so one id follows a run through both processes. Request logs hold method, path, status and duration only. **No payload values are ever logged.** Tests check that on every submission path, and the E2E suite checks the real processes' output.

### C2. Failures

| Symptom | What to do |
|---|---|
| Run **Rejected** | The data is wrong (the run view lists each failed rule). The sender corrects it and submits again with a new Idempotency-Key. Not retryable. |
| Run **Failed** | A technical error persisted through 3 attempts (storage, Chromium, timeout); read the failed step's error. Fix the cause, then retry from the run view or `POST /api/v1/executions/{id}/retry` (Operator/Admin). |
| Run **Failed** with a `db.*` or `sql.*` code at a Query database step | A non-retryable code is not retried automatically: an author problem (`sql.*`, `db.bad_column`, `db.result_too_large`, a permission or column error) or a connection problem (`db.login_failed`, `db.server_not_allowed`, `db.password_unreadable`). Fix the step or connection, publish, then retry the run. Retryable codes (`db.timeout`, `db.unreachable`) are retried like other technical errors. Missing or non-scalar parameter data ends the run **Rejected** (`data.query_invalid`). |
| A run resumed after an approval or a Worker restart shows different database data | By design: rows are not stored, so the Query database step **runs its SELECT again** on resume (an object may become an array, or a result may vanish). If that re-query fails the run ends **Failed**; retry it from the run view. |
| Run **Failed** at a Write to database step with code `write.outcome_unknown`, or any failed Write to database step whose message says "The outcome is unknown" | The statement may or may not have changed data (the Worker stopped or crashed while it ran, or a timeout, a lost connection or a failed commit came after it was sent). **Check the target table first**, by the business key the statement writes (for example the customer number in the audit row), and look at what the run's data says it should have written. If the change is there, do **not** retry: fix the run's later steps by hand or leave it. If it is not there, or the SQL is idempotent (`INSERT ... WHERE NOT EXISTS`, setting a value), retry. **Retry (the run view's Retry button or `POST /api/v1/executions/{id}/retry`, Operator or Admin) runs the write again**, by your decision: Verso never repeats a write by itself, and never retries it automatically. A Worker that is lost or stopped while a write is in progress leaves the step Running; the next time the run is picked up it is failed with `write.outcome_unknown` without sending the statement again. This is also reported after a stop that came before the statement was sent (Verso cannot tell). A Succeeded write is not run again when a run resumes after an approval or a restart; inside a For each the run resumes only over an unchanged list (otherwise `loop.items_changed`, below). |
| Run **Failed** at a Write to database step with `db.writes_not_allowed` | The connection's "Allow workflows to change data" is off. Nothing was sent and the password was not read. Ask an administrator to switch it on in Configuration, Connections (only for a connection whose login can write just the tables the flow needs, [setup.md section 8](setup.md)), then retry. |
| Run **Failed** with `db.too_many_rows_affected` | The statement would have changed more rows than the step's "Most rows to change". It was rolled back, nothing changed. Fix the statement's WHERE (or raise the limit if intended), publish, retry. Rows changed by cascades or by triggers with `SET NOCOUNT ON` are not counted. |
| Run **Failed** with `db.rows_affected_unknown` | The server did not report a row count for the statement, so the cap cannot be checked and the statement was rolled back. This needs the session itself to report no count, typically `SET NOCOUNT ON` made the default for every session on the server (the `user options` server setting); a trigger's own `SET NOCOUNT ON` only hides the trigger's counts, not the statement's. Ask the database administrator to change that default, then retry. |
| Run **Failed** at a For each step with `loop.items_changed` | The loop contains a Write to database step, and the list it repeats over was not the same when the run resumed (after an automatic retry of a later step, an approval or a Worker restart) as when it started: typically a Query database step before the loop read again and found fewer rows (the writes already marked some as done), other rows, or the same rows in another order (a SELECT without ORDER BY). Matching the items by position again could skip an item or write one twice, so nothing in the loop ran. Writes already made for earlier items stay in the database. **Check the target table** for what was written, then **submit the data again** if the remaining items still need processing: a new run writes every item again, so prefer idempotent SQL. A Retry of this run compares the list again and fails the same way unless the data is back as it was. |
| Run **Failed** with an `api.*` code at a Fetch from API step | The Worker called the API through the connection and the call failed; the code says why (rows below). Verso's messages never carry the address, a value or the API's text. **Retried automatically** (three attempts): `api.timeout`, `api.unreachable`, `api.server_error` (HTTP 5xx), `api.rate_limited` (429). **Not retried:** everything else; fix the cause, then retry the run from the run view or `POST /api/v1/executions/{id}/retry`. |
| `api.unauthorized` | The API refused the credentials (HTTP 401 or 403). Check the connection's key or login in Configuration (type a new secret, press Test connection), that the key may read that path, and that it has not expired. Then retry. |
| `api.rate_limited` | The API answered HTTP 429 and the retries ran out. Wait, lower how often the flow runs (or ask the API owner for a higher limit), then retry. |
| `api.server_error` | The API answered HTTP 5xx three times. Check the API's status, then retry. |
| `api.timeout` / `api.unreachable` | The answer did not arrive within the step's time limit (the limit covers the token request, the call and the reading of the answer), or the Worker could not connect. Check the Worker server's egress to the host (firewall, proxy, DNS) and raise the step's time limit if the API is slow; then retry. |
| `api.not_json` / `api.response_too_large` | The answer is not JSON (a missing content type and `text/json` are refused too; XML and text are not supported) or is larger than the step's "Largest answer" (1 to 256 KB). Check the path and the API's answer; raise the limit or ask the API for less; publish the workflow again, then retry. |
| `api.host_not_allowed` | The connection's host (or its OAuth token host) is not in `ApiConnections:AllowedHosts` on the Worker. Add it ([setup.md section 9](setup.md)), restart the Worker, retry. Nothing was sent and the secret was not read. |
| `api.bad_request` | The path, a query value or a stored address is not valid (for example a stored address the save rules would refuse, or a query parameter named like the connection's own API key). Fix the step or the connection, publish, retry. |
| `api.token_failed` | The OAuth token endpoint refused or did not answer. An HTTP error or an unusable answer from the token endpoint is not retried (an unreachable or stalled token endpoint gives the retryable `api.unreachable` / `api.timeout`): check the client id, secret and token address in the connection, press Test connection, then retry. |
| `api.client_error`, `api.connection_unavailable`, `api.secret_unreadable`, `api.certificate_untrusted`, `api.blocked_address`, `api.redirect_not_followed` | Other 4xx answer (check the path and values); the connection is unknown or switched off; the secret cannot be decrypted (the master key `Secrets__MasterKey` differs: type the secret again); the API's certificate authority is not trusted by the Worker server; the host resolves to a blocked address; the API redirected (redirects are never followed: use the final address as the connection's base address). Fix, then retry. |
| Run **Rejected** with `data.fetch_invalid` (Fetch from API) | The data could not make a valid request: a value for the path or a query line is missing or null, is a list or a group of fields, or the path piece is empty, `.` or `..` or has an incomplete Unicode character, or the **resolved, percent-encoded path would be longer than 500 characters** (each non-ASCII character takes 6 to 12 characters once encoded; shorten the value or the template). **Nothing was sent.** The step's issues name the data paths, never the values. Fix the input and submit it again with a new Idempotency-Key (not retryable). |
| Run **Failed** with `fetch.restore` | A run resumed after an approval, a retry or a Worker restart fetches the answer again (it is never stored); the second fetch failed, found nothing, or could not be merged. The API's answer may have changed meanwhile. The step stays Succeeded; fix the cause (the message names the failure), then retry the run: it resumes at that step. |
| Run **Failed** with `fetch.answer_unsupported` / `fetch.answer_too_deep` | The API's answer holds an incomplete Unicode character, or is nested too deeply to be added to the run's data. The same answer fails again: ask the API for less (a narrower path or query), or a flatter answer. |
| Run **Failed** with `fetch.data_not_object`, `fetch.bad_path` or `fetch.bad_query` | The run's data is not an object (a result can only be merged into an object), or the step's path or query setting is invalid (a workflow definition changed outside Verso). Fix the data or the step, publish, retry. |
| Run **Failed** at a Send SMS step with `sms.outcome_unknown`, or any failed Send SMS step whose message says "The outcome is unknown" | The SMS **may or may not have been sent**: the Worker stopped or crashed during the send (`sms.outcome_unknown`; also reported after a stop that came just before the request, Verso cannot tell), or after the request left there was a timeout (`sms.timeout`, or `execution.timeout` "while the message was being sent"), a lost connection (`sms.connection_lost`), an HTTP 5xx (`sms.server_error`), an answer Verso doesn't understand (`sms.unexpected_answer`), a 400 that says the request is a duplicate (`sms.duplicate_request`), or an unexpected failure (`sms.unreachable` with this wording). **Before anything else, look the message up in Brevo's log** (Transactional, SMS, logs) by its **tag** `verso-<first 12 hex characters of the execution id, without dashes>-<iteration>` (iteration `0` outside a For each, `1`, `2`, ... for the items inside one; two Send SMS steps of the same run and item share the tag, compare the time). If it was sent, **do not retry**: a retry sends it again (the customer gets two messages); finish the run's remaining work by hand or leave it. If it is not in the log, retry (the run view's Retry button or `POST /api/v1/executions/{id}/retry`, Operator or Admin): the step sends once more, by your decision. Verso never resends by itself and never retries a Send SMS step automatically; a Succeeded send is not repeated when a run resumes. |
| Run **Failed** at a Send SMS step with a known `sms.*` code (nothing was sent) | `sms.sending_not_allowed`: the connection's "Allow workflows to send SMS" is off; an administrator switches it on (Configuration, Connections), then retry. `sms.marketing_not_allowed`: the connection is of type marketing; use a transactional connection, publish, retry. `sms.connection_unavailable`: the connection is unknown or switched off. `sms.host_not_allowed`: its host is not in `SmsConnections:AllowedHosts` on the Worker. `sms.bad_request`: its stored base address is not a valid https address. `sms.secret_unreadable`: the API key can't be decrypted (the master key differs; type the key again). `sms.text_needs_unicode`: the text has a character outside the GSM 7-bit set and the connection has Unicode off; switch Unicode on, or change the template, then retry. `sms.tag_invalid`: an internal check (report it). `sms.unauthorized` (401 / 403: check the key in Brevo), `sms.insufficient_credits` (top up the Brevo account), `sms.rate_limited` (429: wait), `sms.invalid_recipient` (Brevo itself refused the number: check the customer's number in the data), `sms.client_error` (another 4xx; Brevo may also refuse the tag this way, see ADR 0022), `sms.redirect_not_followed`, `sms.unreachable` with "could not be reached" (DNS, firewall or proxy: the connection was never opened), `sms.certificate_untrusted`, `sms.blocked_address`: fix the cause, then retry. **None of these is retried automatically**, and nothing was sent. |
| Run **Rejected** with `data.sms_invalid` (Send SMS) | The data could not make a valid message, nothing was sent for this step: a value for the number or the text is missing or null (`sms.value_missing`), is a list, a group of fields or a number too large to write (`sms.value_invalid`), the text holds a control or invisible character or is empty or longer than 480 characters once filled in (`sms.text_invalid`), the number is not an international number of 8 to 15 digits (`sms.recipient_invalid`), or its country is not among the connection's allowed prefixes (`sms.country_not_allowed`, also when the list is empty). The issues name the data path or the rule, never the number or the text. Fix the data (or ask an administrator to allow the country) and submit again with a new Idempotency-Key (not retryable). **Inside a For each the whole run is rejected**: messages already sent for earlier items stay sent, so a new submission of the same list **sends them again**; remove the items already handled first. |
| Run **Failed** at a For each with `loop.items_changed` and a Send SMS step in the loop | As for a Write to database step (the `loop.items_changed` row above): the list was not the same when the run resumed, so nothing in the loop ran and no message was sent twice. Messages already sent for earlier items stay sent. **Check Brevo's log** (by the tag, above) for what was sent, then submit the data again with only the remaining items: a new run sends every item again. |
| Runs stay **Queued** | No Worker is running or it can't reach the database. Queued runs start once a Worker is up; lost jobs are re-queued within about 3 minutes. |
| Download returns 409 `document.integrity_failed` | The archived file no longer matches its recorded SHA-256. Nothing is served. Restore the file from backup (C3) and investigate. |
| API won't start: "Auth:Entra:TenantId … not configured" | Configure Entra (B4). |
| 401 for everyone | Wrong tenant or client id, or the token's audience isn't the API registration. |
| 403 for a user | The user lacks the Verso role (B3). |
| Folder input: workflow shows **Access denied** or **Folder unavailable** | The Worker's account lacks Modify on the folder, or the share is unreachable from the Worker's server. Fix it, then press **Test access**. Files wait; nothing is lost. |
| Files pile up in the processed folder's `.verso-inflight` | Their runs are still Queued or Running (see C2 above), or the destination is unreachable (the workflow's recent files show "Not moved yet"). They are moved once that is fixed. |
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
