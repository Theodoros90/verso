# Write to database step (Phase 2): design

Date: 2026-11-18. Status: **awaiting owner approval**. Will become ADR 0020. Builds on ADR 0019 (Query database step, Phase 1, branch `feature/query-database-step`, not yet merged).

## Intent
A workflow can change data in a database during a run: for example insert a row built from values in the parsed XML. The author types one `INSERT`, `UPDATE` or `DELETE` in the step, maps `@parameters` to data fields, and the Worker runs it with the Database connection from Configuration.

## Owner decisions already taken (2026-11-18)
- Free-typed SQL by workflow authors; anyone who can edit a workflow may write it (this includes write SQL).
- The exception to "no author-written code" is approved for read and write SQL.
- Each Database connection has an **"Allow writes" switch**, set by administrators only, off by default. A connection that is not switched on refuses any write, whatever the SQL says.
- A write step is **never retried automatically**.

## Goals
- New step **Write to database** (my call: a separate node, not a mode of Query database, so the palette, audit and reviewer can see at a glance that a flow writes).
- Settings: connection, SQL (one statement), parameters (`name = data.path`, as in Query database), `maxRows` (the most rows the statement may change, default 1, up to 1000), `timeoutSeconds` (default 15, up to 60).
- Ports: `written` (the statement ran and the row count is within the cap) and `failed` (missing or non-scalar parameter value only, as in Query database). Step result holds only `rowsAffected`, `durationMs`, `connection`: never values, never the SQL.
- Works for SQL Server, PostgreSQL, MySQL / MariaDB and Oracle with the same runner structure as Phase 1.

## Non-goals
- No `MERGE`, `TRUNCATE`, DDL, stored procedures, `INSERT ... EXEC`, `SELECT ... INTO`, multiple statements, or reading rows back (use Query database after the write).
- No automatic duplicate protection (see Risks). No per-table allow-list in this phase.
- API call step, Send SMS step and SFTP stay later phases.

## Anti-goals
- A write must never happen on a connection that was not switched on, never run as more than one statement, never be repeated by Verso without a person deciding to.

## Design
1. **Connection switch.** Add `AllowWrites` (bool, default false) to `connections.DatabaseConnections` (additive migration `DatabaseConnectionAllowWrites`; existing connections stay off). Administrators change it on the Connections page; audit records the field name, never values. The service refuses a write on a connection with the switch off (`db.writes_not_allowed`, non-retryable). The options endpoint adds `allowsWrites` so the step form can warn when a switched-off connection is chosen.
2. **WriteGuard (SQL).** Reuse SqlGuard's tokenizer rules (all the dialect-disagreement refusals stay). Write mode: the first word must be exactly one of `INSERT`, `UPDATE`, `DELETE`; `UPDATE` and `DELETE` must contain a top-level `WHERE`; any other statement verb appearing anywhere as a bare word (the full forbidden list from Phase 1, minus the single leading verb, and minus `INTO` after `INSERT`) is refused. So on SQL Server `UPDATE t SET a = 1 DELETE FROM u` (no `;`) is refused. Subselects (`SELECT` inside the statement) are allowed.
3. **Execution.** Read-write transaction (SQL Server plain transaction; PostgreSQL `BEGIN` read write on a connection string without `default_transaction_read_only`; MySQL `START TRANSACTION READ WRITE`; Oracle default). `ExecuteNonQuery`; if the rows affected exceed `maxRows` the transaction is **rolled back** and the step fails `db.too_many_rows_affected` (non-retryable); otherwise **commit**. Parameters are driver parameters only (Oracle synthetic `:p1..`), as in Phase 1. Provider error mapping as in Phase 1.
4. **Never retried, never repeated.** Any failure after the statement may have reached the database (timeout, lost connection, Worker stop) ends the run **Failed** with the message "the outcome is unknown: check the database before running again". The engine does not auto-retry this step type (attempt limit 1). A person may retry the run from the executions page; the warning text is shown there. On **resume** (after an Approval, or a restart) a write step that already succeeded is **not run again** (unlike Query database, which re-queries): its success and row count are in the step record.
5. **Privacy and audit.** Publish audit records the SQL text and connection key (`databaseWrites`), never values. Logs, events and step result never carry values. Run-time audit records the row count only.
6. **Frontend.** New node in the palette (category Output, distinct icon and a "changes data" label), the same connection select (with a clear "Writes are switched off for this connection" notice) and SQL widget as Phase 1, a visible "Maximum rows changed" setting, helper text explaining that a failed run may have partly happened. Connections page: the "Allow writes" switch with a confirmation sentence.
7. **Docs.** ADR 0020, CLAUDE.md (milestone line; the safety exception extends to this step; the approved-integrations list), setup.md (a write-capable login must be limited to the tables the flows need), api.md, OpenAPI snapshot and generated types, a sample ("insert an audit row after the document is generated").

## Risks the owner should know
- **Outcome unknown after a timeout or crash.** Verso cannot make a write in an external database atomic with its own records. The step is therefore at-most-once automatically and a person decides on a rerun, who may insert a duplicate. If the table has a natural key, the author should write an idempotent statement (for example `INSERT ... WHERE NOT EXISTS`).
- **Any workflow editor can write to any switched-on connection.** The protection is the administrator switch and a login that can only touch the needed tables. A per-connection or per-table allow-list is a possible later phase.
- **Only SQL Server will be run against a live server** (LocalDB). PostgreSQL, MySQL / MariaDB and Oracle are structural only, as in Phase 1.
- **Guard is defence in depth**, not the protection: the login's permissions are.

## Approaches considered
- **A. Separate Write to database node (chosen proposal).** Clear in the palette and audit; shares parameter and guard code; slightly more UI.
- **B. A mode switch on Query database.** One node, but a mode change silently turns a read into a write and complicates validation and the guard.
- **C. Structured insert/update (table and column picker).** Safer and idempotent-friendly, but a much larger build; later phase if wanted.

## Left to the builder
Exact verb/keyword handling in WriteGuard, wording of messages, form layout, test shapes.
