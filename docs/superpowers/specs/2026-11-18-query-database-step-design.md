# Query database step: design

Date: 2026-11-18. Status: **approved by the owner 2026-11-18**. Will become ADR 0019.

Owner decisions (2026-11-18): (1) anyone who can edit a workflow may write read and write SQL (replaces the "administrators for writes" suggestion; the per-connection "Allow writes" switch stays the control); (2) the exception to the "no author-written code" rule is approved for read and write SQL; (3) build phase by phase, starting with Phase 1 (read).

## Intent
A workflow can read data from a database connection during a run (for example, look up a customer by an id taken from the parsed XML), merge it with the XML data, and generate a document from the combined data. Today the Database connections are Test-connection-only and no step uses them.

## Goals
- New **Query database** step: pick a Database connection, type one SQL `SELECT`, map `@parameters` to run-data fields, name where the result is stored.
- Works with the four providers (SQL Server, PostgreSQL, MySQL / MariaDB, Oracle) through the existing Worker-only `Verso.Databases` project.
- Works with Parallel branches / Wait for branches / Merge data as they are today.

## Scope change, 2026-11-18 (owner)
The owner wants free-typed SQL **including writes** (for example insert values from the XML into a table), using the global connections from Configuration. Phase 1 (read) is as below; **Phase 2 adds write mode**: an administrator switch "Allow writes" on each Database connection (default off); a step in write mode accepts one INSERT / UPDATE / DELETE (no DDL, TRUNCATE, GRANT, EXEC); UPDATE and DELETE need a WHERE; the statement runs in a transaction that is rolled back if it affects more rows than the step's cap (default 1); a write step is **never retried automatically** (a failure says the outcome is unknown); values are parameters only; audit records the SQL text at publish and the row count at run time, never values. Phases 3 and later (API, SMS) are in the plan sent to the owner.

## Non-goals (Phase 1)
- No writes in Phase 1 (INSERT / UPDATE / DELETE / DDL / stored procedures). No multiple statements.
- No API connection step, SMS step or SFTP in this change (database first, others later, each with its own ADR).
- No schema browser or query builder. No change to Test connection.
- The existing Database query *input* step (simulated trigger) is unchanged.

## Anti-goals
- A workflow author must never be able to change data, run more than one statement, or build SQL by gluing values into text.

## Approaches considered
- **A. Saved queries managed by administrators.** Safest, adds a table and an admin screen. Recommended earlier.
- **B. Author types SQL in the step. CHOSEN by the project owner on 2026-11-18.** Smallest build; most flexible.
- **C. Table / column picker.** Safe but much larger.

B deviates from the CLAUDE.md rule "don't execute arbitrary user-authored code". The deviation is approved by the owner and must be added to CLAUDE.md's safety section when this ships ("approved so far: ... reading rows from Database connections with a SELECT typed by a workflow author, ADR 0019").

## Design (the how, as proposed)
1. **Step settings:** connection (dropdown of active Database connections, key), SQL text (max 4,000 chars), parameters (list: name, source data path), result name, max rows (default 1, up to 100), timeout seconds (default 15, max 60).
2. **Values are always parameters.** The SQL uses `@name` placeholders; values come from run data and are bound as driver parameters, never concatenated. A placeholder with no mapping, or a mapped path with no value, ends the step as `failed` with a clear message.
3. **Validation at save/publish (and again at run time):** exactly one statement; must start with `SELECT` or `WITH ... SELECT`; reject `;` followed by more text, `INTO`, `EXEC`/`CALL`, comments hiding text, and any keyword outside a small allow-list of read forms. This is a safety net, not the only one.
4. **Execution guards:** read-only transaction where the provider supports it (`SET TRANSACTION READ ONLY` on Oracle/PostgreSQL/MySQL; `ApplicationIntent=ReadOnly` plus rollback on SQL Server), command timeout, row cap enforced by the reader (extra rows are not read), result size cap (256 KB), always rolled back. The database account of the connection **should itself be read-only**; this is stated in the admin docs and the connection form.
5. **Output ports:** `found`, `not-found` (zero rows), `failed` (technical error, retryable; bad SQL / missing parameter not retryable). Result: a single row becomes an object, several rows an array, stored in run data under the result name (merged into the current data object, not replacing it).
6. **Privacy / audit:** the SQL text and the connection key are recorded and audited when a workflow is published (like folder paths); column values and parameter values are never logged or audited; the step result keeps only row count, duration and column names.
7. **Who:** whoever can edit and publish workflows today. Open question for the owner: should this be limited to an administrator role?
8. **Safety rails reused:** `DatabaseConnections:AllowedServers`, https/addresses guards as for Test connection, Worker only (API never connects).
9. **Layering:** `IDatabaseQueryRunner` in `Verso.Application` (engine calls it like `IEmailSender`); implementation in `Verso.Databases`, registered in the Worker only. No new package.
10. **Frontend:** new node in the catalog with a form (connection dropdown, SQL box, parameter rows, result name); Connections page note "read-only account recommended".
11. **Deliverables:** ADR 0019, CLAUDE.md update, `docs/api.md`, OpenAPI snapshot and generated types only if an endpoint changes (a connection-options endpoint like `email-relay-options` is likely), unit tests for the SQL validator and parameter binding, SQL Server integration test on LocalDB, engine tests (found / not-found / failed), frontend tests, parallel + merge + generate sample workflow. PostgreSQL, MySQL and Oracle verified only as far as the existing probes were (no live servers); I will say so plainly.

## Constraints
.NET 10, existing layering rules, no new package, migrations applied by hand (a migration is only needed if a new table is added: none expected).

## Left to the builder
Exact keyword allow-list and parser details; wording of messages; form layout.
