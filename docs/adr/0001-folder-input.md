# ADR 0001: Folder input (LaserNet-style watched folders)

- Status: accepted (implemented 2026-11-11)
- Context owner: requested by the product owner on 2026-11-11, as a feature separate from the simulated input steps.

## Context

Before this change, every input step except the API trigger only described an integration: `erp-connector`, `file-upload`, `sftp-folder` and `scheduled-trigger` stored settings and the graph validator checked them, but nothing read from a folder, connected to SFTP or an ERP, or ran on a schedule. All runs came from `POST /api/v1/workflows/{key}/executions`, and the engine's trigger step passed the submitted data on.

Banks commonly hand documents to output systems through watched folders on a file server. The request was a real **Folder input** step: run by the .NET Worker, reading folders approved by administrators, with safe handoff, single claiming across Workers, recorded ingestion state, restart recovery, processed/failed destinations, and a "Test access" action executed as the Worker's service identity. File upload stays a separate (still simulated) step.

## Decision

1. **New node type `folder-input`** (category input). Settings: source location and optional subfolder, file pattern, include subfolders, scan interval (1, 5, 15 or 60 minutes), processed location and subfolder, failed location and subfolder. The step title is the node's title. There are no credential settings; unknown settings are rejected.

2. **Approved locations live in the Worker's configuration** (`FolderInput:Locations:<key>`: `Name`, `Path`, `Input`, `Output`), so they are controlled by server administrators under change control. The Worker reports **key, name and purpose only** to `ingestion.FolderLocations`. Paths never reach the database, the API or a browser. Designers choose a key from a list and may add a relative subfolder.

   **Addendum (2026-11-11): administrator-managed locations.** The requester then asked to enter any source manually. Typing a path in the designer was declined (it reverses "no unrestricted server paths"); instead an **Admin-only screen** (*Folder locations*, `PUT/DELETE /api/v1/admin/folder-locations/{key}`) lets administrators add, change, test and remove locations without editing the Worker's configuration. Differences from the configuration route: the path is stored in `ingestion.ManagedFolderLocations` (readable by Admins only; the catalog designers read still has no paths), changes are audited with the path, and removed locations are marked, not deleted. The Worker reads them on every pass and validates them like configured ones, so a bad path shows up as the location's problem. A configuration key can't be taken over by the screen (409), and configuration wins at the Worker. `FolderInput:AllowedRoots` optionally limits managed paths to certain parent folders. The risk accepted: an Admin session can now point the Worker at any folder its account can reach (when `AllowedRoots` is unset), which is why the endpoints require the Admin role only and are audited; database grants keep the Worker read-only on this table.

   **Addendum 2 (2026-11-11): typed paths replace the location picker (decision reversed by the requester).** The requester asked twice to type the folders directly in the step. Decision 2's "designers only choose approved keys" no longer holds for new workflows: the step now has `sourcePath`, `processedPath` and `failedPath`, typed as absolute paths as the Worker's server sees them. Workflows saved with locations keep working (hidden legacy settings; a typed path wins). What replaces the approval step:
   - **Path shape** is checked at design time (absolute, no `.`/`..`, no wildcards, at most 400 characters) and the Worker checks the rest as its own service identity (existence, rights, links, device names), reporting scan errors that name the typed path.
   - **Accountability:** every new path (and each new purpose of a known path) is recorded in `ingestion.TypedFolderPaths` and audited as `FolderPathRegistered` with who typed it. This happens in the same save as publishing a workflow, or when "Test access" is requested.
   - **Why a table:** the workflow definition and the ingestion records hold only keys (`path-` + 16 hex of the SHA-256 of the normalized path), because the claim index and the file records have key-sized columns. The table lets the Worker resolve a path after a restart or a newer workflow version. Rows are never deleted, and the Worker only reads them (database grants).
   - **Limit:** `FolderInput:AllowedRoots` (optional, per Worker) restricts typed paths and administrator-managed locations to certain parent folders. Without it, any folder the Worker's account can reach can be typed by a workflow author, so the account's own permissions are the boundary. The risk is accepted knowingly: a workflow author (not only an Admin) can now choose where the Worker reads and writes.
   - Paths now appear in messages and in the scan status that every role can read; before, messages named keys only.
   - The Admin screen (addendum 1) stays for named locations and older workflows.
   - Rejected: locations managed through an Admin screen. That would let a web session (or a stolen Admin token) point the Worker at any server path, and it duplicates path validation in two processes.
   - Locations removed from the configuration are disabled, not deleted, so workflows that use them show why.

3. **Path safety, checked twice.** Design time (API): the subfolder pattern (segments of letters, digits, space, `.`, `_`, `-`; no leading or trailing dot or space, no `..`, no backslashes, no device names, at most 8 levels) and the location's purpose. Run time (Worker): the configured root must be absolute, normalized and not a drive or file-system root; the resolved path must stay inside the root; linked folders (symbolic links, junctions) below the root are refused and not followed when scanning.

4. **Hosting.** The adapter uses only `System.IO`, so it works on Windows (local paths or UNC shares such as `\\fileserver\share\inbound`, reached with the Worker's gMSA) and on Linux (shares mounted by the OS, such as `/mnt/verso/inbound`). Path comparison is case-insensitive on Windows. Verso never stores or sends share passwords.

5. **Handoff convention** (documented for senders): write under a temporary name (starting with `.` or `~`, or ending with `.tmp`, `.part`, `.partial`, `.filepart`, `.crdownload`, `.lock`, `.download`), then rename to the final name. Second line of defence: a file must be unchanged for `FolderInput:SettleTime` (default 10 s). Third, on Windows: a file still open by its writer can't be opened exclusively and is left for the next scan.

6. **Claiming and state** (schema `ingestion`, table `FolderFiles`): Claimed → Submitted → Delivering → Completed, with outcome Processed or Failed.
   - **Claim**: insert the row first. A filtered unique index on the source file (`WHERE Status = 'Claimed'`) lets only one Worker claim it. Then the file is moved atomically into `<folder>/.verso-inflight/<id>`, so it is out of the scanned set and the original name is free for the sender. A failed move (vanished, in use) deletes the claim.
   - **Scans**: a `FolderWatch` row per workflow, taken under optimistic concurrency, so each folder is scanned once per interval even with several Workers.
   - **Leases**: a Worker holds a file row for `FolderInput:Lease` (2 min) while working on it. After a crash, another Worker (or the same one after a restart) takes it over.

7. **No unintended duplicate runs.** The run's idempotency key is `folder:` + the canonical SHA-256 of the JSON data. Re-submitting after a crash returns the same run, and identical data in another file (or dropped again later) never starts a second run; such a file goes to the failed location as `file.duplicate`. A filtered unique index on `FolderFiles.ExecutionId` settles two simultaneous copies. The run's correlation id is the ingestion id.

8. **Outcome and delivery.** A Succeeded run moves the file to the processed location. Rejected or Failed runs, unreadable files (not a JSON object), files over `FolderInput:MaxFileBytes` (1 MB), duplicates and refused submissions go to the failed location, with `<name>.error.json` beside the file (code, message, run id; no data values, no paths). Delivery copies the file under a hidden temporary name and then renames it, so the destination never shows a partial file, even across volumes. A taken name gets the ingestion id as a suffix, and nothing is overwritten. Delivery is idempotent after a crash (detected by SHA-256). An unreachable destination is recorded on the file and retried on every pass.

9. **Errors are visible.** Each scan records its status (`Ok`, `Unavailable`, `AccessDenied`, `Misconfigured`, `Error`) and a sanitized message naming the location key, the Worker's identity and machine (never the path). The designer shows it with the recent files (`GET /api/v1/workflows/{id}/folder-input`).

10. **Test access** (`POST /api/v1/folder-access-tests`, WorkflowAuthor/Admin): the API validates the targets and queues a Hangfire job. The Worker checks each folder as its own identity: approved for this purpose, folder reachable and listable (output folders are created if missing), and a probe file created, moved into the working folder (source) and deleted. The result names the identity and machine. Requests are audited (`FolderAccessTestRequested`) and deleted after 24 h.

11. **Content.** JSON only in this version. XML and CSV need a field mapping to the invoice data model, which is a separate decision; such files go to the failed location with an explanation. (The `parse` step now reads XML/CSV text found in a field of the submitted data, but the folder trigger doesn't feed it yet: it still submits the JSON file itself.)

12. **Database permissions** (`tools/db/least-privilege.sql`): the API reads `ingestion` and may insert access tests. The Worker reads and writes `ingestion` and may delete only claims (`FolderFiles`) and old access tests. Nobody deletes locations, and the API can't change ingestion records.

## Consequences

- The plan's "External integrations stay simulated until explicitly approved" now has one approved exception: folder input on approved locations. SFTP, ERP, scheduled triggers and file upload remain simulated.
- Operations gain a configuration area and file permissions to manage (runbook B7). All Workers must share the same `FolderInput:Locations`, because a location is resolved on the Worker that handles the file.
- A Failed run's file goes to the failed location. If an operator later retries the run successfully, the file stays there. The ingestion record and the run link them.
- Files stay in the source's `.verso-inflight` folder while their run is in progress. Operators shouldn't touch that folder.
- Open: XML/CSV mapping; per-location size limits; alerting on repeated scan failures; whether one location key may be used by several workflows' sources at once (allowed now: the claim index makes each file go to exactly one workflow).
