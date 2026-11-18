# Query database step (Phase 1, read only) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A workflow step "Query database" runs one author-typed `SELECT` against a Database connection from Configuration, binds values from the run data as parameters, and merges the row(s) into the run data.

**Architecture:** The engine (Verso.Application) calls a new `IDatabaseQuery` interface, like `IEmailSender`. `DatabaseQueryService` (Application) resolves the connection, checks active and `AllowedServers`, decrypts the password in memory and hands the validated SQL to a per-provider `IProviderQueryRunner` (SQL Server in Infrastructure, the others in Verso.Databases), found by a router like `DatabaseProbeRouter`. Worker only; the API never connects. Phase 2 (writes) is a separate plan.

**Tech Stack:** .NET 10, Microsoft.Data.SqlClient, Npgsql, MySqlConnector, Oracle.ManagedDataAccess.Core (all already approved, **no new package**), React + TypeScript (schema-driven node form), xUnit, Vitest.

**Spec:** `docs/superpowers/specs/2026-11-18-query-database-step-design.md` (approved 2026-11-18). Existing patterns to copy: `ExecutionEngine.SendEmailAsync`, `IEmailSender`, `DatabaseProbeRouter`, `DatabaseConnectionTestRunner`, `/api/v1/email-relay-options`, SchemaForm widget `email-relay`.

## Global Constraints
- Read only: exactly one statement, starting `SELECT` or `WITH`; never `INTO`, `EXEC`, `CALL`, `;` followed by text. Run in a read-only transaction that is always rolled back.
- Values are driver parameters only, never concatenated into the SQL. SQL max 4,000 chars; max rows default 1, up to 100; timeout default 15 s, up to 60 s; result max 256 KB.
- Never put row or parameter values in logs, audit entries or the step `Result` (the API returns step results to every execution reader). The step result holds `rows` (count), `columns` (names), `durationMs` only.
- Messages are Verso's own text, never the driver's (it can name the server or login), as in `DatabaseProbeMessages`.
- Worker only: `Verso.Api` must not reference or call the runners (RendererPurity-style layering rules in CLAUDE.md). No new package, no new project.
- Migrations: none expected. Do not run `dotnet ef database update`.
- Do not commit; the project owner commits. Use synthetic data only.
- Document and mark in the ADR that this is an approved exception to "no author-written code" (owner decision 2026-11-18); anyone who can edit a workflow may author the SQL.
- **My call (flag in ADR 0019):** a resumed run (after an Approval or a Worker restart) re-runs the SELECT, because the rows are not stored; the database may have changed since.

## Review Focus
- SQL with a trailing `;` plus a second statement, or `SELECT 1; DROP TABLE x` → rejected at save and at run.
- A `@name` placeholder with no mapping, or a mapped path with no value in the data → `failed` port, not retryable, no SQL sent.
- A `--` or `/* */` comment hiding `INSERT`/`;`, and `SELECT … INTO newtable` → rejected.
- Zero rows → `not-found` port; more rows than the cap → only the cap is read, result says `truncated`.
- A connection that is switched off, missing, or not in `AllowedServers`; an unreadable password (missing master key) → clear `failed` with a retryable=false message.
- A value that contains quotes or `'; DROP` → returned as data, never executed (parameter binding).

---

### Task 1: SQL guard

**Files:** Create `src/Verso.Application/Executions/SqlGuard.cs`; Test `tests/Verso.UnitTests/Executions/SqlGuardTests.cs` (put beside the other engine unit tests; check the folder name first).

**Interfaces:** Produces `public static class SqlGuard { public const int MaxLength = 4000; public static SqlCheck Check(string? sql); }` and `public sealed record SqlCheck(bool Ok, string? Code, string? Message, IReadOnlyList<string> Parameters)`. `Parameters` are the distinct `@name` placeholders in order of first use (names `^[A-Za-z_][A-Za-z0-9_]{0,63}$`).

- [ ] **Step 1: Write the failing tests** (names and assertions): `Accepts_plain_select` (`SELECT name FROM customers WHERE id = @id` → Ok, Parameters `["id"]`); `Accepts_cte` (`WITH x AS (SELECT 1 a) SELECT a FROM x`); `Rejects_empty_and_too_long`; `Rejects_second_statement` (`SELECT 1; DROP TABLE t`, and a trailing `;` alone is allowed); `Rejects_non_select_start` (`INSERT`, `UPDATE`, `DELETE`, `EXEC`, `CALL`, `MERGE`, `DROP`); `Rejects_select_into`; `Rejects_comment_hiding_text` (`SELECT 1 -- ; DROP` is fine only if nothing executable remains after stripping comments; `SELECT 1 /* x */; DELETE FROM t` rejected); `Ignores_keywords_inside_string_literals_and_identifiers` (`SELECT 'drop; into' AS note` Ok; column named `updated_at` Ok); `Rejects_unterminated_string_or_comment`; `Collects_distinct_parameters_case_sensitively`. Codes: `sql.empty`, `sql.too_long`, `sql.multiple_statements`, `sql.not_select`, `sql.select_into`, `sql.unterminated`.
- [ ] **Step 2:** Run `dotnet test tests/Verso.UnitTests --filter SqlGuard -m:1`. Expected: FAIL (type missing).
- [ ] **Step 3:** Implement `SqlGuard.Check` as a small tokenizer (strip `--` and `/* */` comments, skip `'…'`, `"…"`, `[…]`, `` `…` `` literals/identifiers, find a `;` that is followed by non-whitespace, find the first keyword, find a top-level `INTO` keyword token). Not regex over the raw text.
- [ ] **Step 4:** Run the same filter. Expected: PASS.

### Task 2: Query contract, service and runners (SQL Server first)

**Files:** Create `src/Verso.Application/Connections/DatabaseQueryModels.cs`, `src/Verso.Application/Connections/DatabaseQueryService.cs`, `src/Verso.Infrastructure/Connections/SqlServerQueryRunner.cs`; Modify `src/Verso.Infrastructure/DependencyInjection.cs` (`AddVersoDatabaseConnections`), `src/Verso.Application/DependencyInjection.cs` is **not** touched (Worker-only registration like `IEmailSender`); Test `tests/Verso.Api.Tests/DatabaseQueryTests.cs` (LocalDB, beside `DatabaseProviderTests.cs`).

**Interfaces:**
- Produces in Application: `public sealed record DatabaseQueryRequest(string ConnectionKey, string Sql, IReadOnlyDictionary<string, object?> Parameters, int MaxRows, TimeSpan Timeout)`; `public sealed record DatabaseQueryResult(string? Code, string? Message, bool Retryable, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, bool Truncated, long DurationMs)` with `static DatabaseQueryResult Failure(string code, bool retryable)` (message from a new `DatabaseQueryMessages.For(code)`); `public interface IDatabaseQuery { Task<DatabaseQueryResult> RunAsync(DatabaseQueryRequest request, CancellationToken ct); }`; `public interface IProviderQueryRunner { string Provider { get; } Task<DatabaseQueryResult> RunAsync(DatabaseConnectionSettings settings, DatabaseQueryRequest request, CancellationToken ct); }`.
- `DatabaseQueryService(IVersoDbContext db, ISecretProtector secrets, IEnumerable<IProviderQueryRunner> runners, DatabaseConnectionOptions options) : IDatabaseQuery` — repeats the lookup / active / `AllowedServers` / decrypt steps of `DatabaseConnectionTestRunner` (copy their codes: `db.connection_unavailable`, `db.server_not_allowed`, `db.password_unreadable`, plus `db.provider_unavailable`), re-runs `SqlGuard.Check`, then calls the runner. Result codes for failures: non-retryable for the codes above and `sql.*`; retryable for `db.timeout`, `db.unreachable`, `db.query_failed`.
- Consumes: Task 1 `SqlGuard`.

- [ ] **Step 1: Write failing tests** against LocalDB (`VERSO_TEST_SQL_SERVER`): create a throwaway table in the test database with 3 rows; assert: `Returns_one_row_as_object_data` (`WHERE id = @id`, MaxRows 1), `Caps_rows_and_marks_truncated` (MaxRows 2 of 3 → 2 rows, Truncated), `Zero_rows_is_success_with_no_rows`, `Binds_hostile_text_as_data` (parameter `x' OR 1=1 --` finds nothing, table intact), `Cannot_write` (call the runner directly with `INSERT` bypassing the guard → fails, row count unchanged, code `db.query_failed`: proves the read-only transaction + rollback), `Honours_timeout` (`WAITFOR DELAY '00:00:05'` with 1 s timeout → `db.timeout`, retryable), `Rejects_inactive_connection`, `Rejects_server_not_in_allowed_list`, `Rejects_second_statement` (service level, no SQL sent). Reuse how `DatabaseProviderTests.cs` creates a connection row and the master key.
- [ ] **Step 2:** `dotnet test tests/Verso.Api.Tests --filter DatabaseQuery -m:1` → FAIL.
- [ ] **Step 3:** Implement. SQL Server runner: connection string like `SqlServerProbe` (reuse `DataSource`, make it `internal` shared), `ApplicationIntent.ReadOnly`, `Pooling=false`, `ApplicationName="Verso query"`; open, `BeginTransaction(IsolationLevel.ReadCommitted)`, bind `SqlParameter`s (null → `DBNull`), `CommandTimeout`, read with `SequentialAccess` up to `MaxRows` rows and 256 KB of JSON-serialised size (over → `truncated`), always `Rollback()`. Map exceptions to Verso codes with `SqlServerProbe.Describe` plus `db.query_failed` (retryable false for syntax/permission errors numbers 102, 156, 207, 208, 229, 262, 4060; true otherwise). Register `IProviderQueryRunner` in `AddVersoDatabaseConnections` and `IDatabaseQuery` → `DatabaseQueryService` (singleton-safe: take `IServiceScopeFactory` if `IVersoDbContext` is scoped; check how `DatabaseConnectionTestRunner` is registered and copy).
- [ ] **Step 4:** Same filter → PASS.

### Task 3: Other providers

**Files:** Modify `src/Verso.Databases/DatabaseDrivers.cs`; Create `src/Verso.Databases/QueryRunners.cs`; Test `tests/Verso.Api.Tests/DatabaseProviderTests.cs` (extend).

**Interfaces:** Consumes `IProviderQueryRunner`, and the internal `Build(settings)` of each probe (make them reusable; set `ApplicationName`/session read-only as there). Produces `PostgreSqlQueryRunner`, `MySqlQueryRunner`, `OracleQueryRunner`.

- [ ] **Step 1: Failing tests** (no live servers): for each runner assert it exposes the right `Provider`, builds a connection string equal to its probe's `Build` result (so TLS, timeout and no-pooling rules stay shared), and that `AddVersoDatabaseDrivers` registers all three `IProviderQueryRunner`s. State plainly in the test file header that execution against live servers is **not** covered.
- [ ] **Step 2:** `dotnet test tests/Verso.Api.Tests --filter DatabaseProvider -m:1` → FAIL.
- [ ] **Step 3:** Implement each runner like the SQL Server one: PostgreSQL `BEGIN READ ONLY` transaction (already `default_transaction_read_only=on`); MySQL `START TRANSACTION READ ONLY`; Oracle `SET TRANSACTION READ ONLY` then the query (Oracle: named parameters `:name`, so rewrite `@name` → `:name` outside literals, in `SqlGuard`-aware code shared in `SqlGuard.RewriteParameters(sql, prefix)`; add a unit test for it in Task 1's file). Map driver errors with each probe's `Describe`, else `db.query_failed`.
- [ ] **Step 4:** Same filter → PASS.

### Task 4: Node, validation and options endpoint

**Files:** Modify `src/Verso.Application/Workflows/NodeCatalog.cs` (constant `QueryDatabase = "query-database"`, node definition next to `SendEmail`), `src/Verso.Application/Workflows/GraphValidator.cs` (`case NodeCatalog.QueryDatabase`, and `PortNote` text for `failed`), `src/Verso.Application/Workflows/WorkflowService.cs:347` area (publish audit), `src/Verso.Application/Connections/DatabaseConnectionService.cs` (add `ListOptionsAsync`), `src/Verso.Api/` the database connection endpoints file (new `GET /api/v1/database-connection-options`, **any signed-in workflow editor**, returns `[{ key, name, provider }]` for active connections, never server/login), `docs/api.md`, `docs/openapi/v1.json`, `src/verso-web/src/lib/api/generated.ts`; Test `tests/Verso.UnitTests/Workflows/QueryDatabaseNodeTests.cs` and `tests/Verso.Api.Tests/DatabaseConnectionApiTests.cs` (extend).

**Interfaces:** Node config schema: `connection` (key, pattern `DatabaseServers.KeyPattern`, `x-widget: "database-connection"`), `sql` (string, max 4000, `x-widget: "sql"`), `parameters` (multiline, one `name = data.path` per line, max 20), `resultName` (identifier, default `db`), `maxRows` (1–100, default 1), `timeoutSeconds` (1–60, default 15). Ports: in; out `found`, `not-found`, `failed`. Produces `DatabaseQueryStep.ParseParameters(string text) : (IReadOnlyList<(string Name, string Path)> Items, string? Problem)` in `src/Verso.Application/Executions/DatabaseQueryStep.cs`, and `GraphValidator` reports: connection missing, `SqlGuard` problem, a `@placeholder` with no mapping line, a mapping with no placeholder, duplicate names, bad `resultName`.

- [ ] **Step 1: Failing tests:** node appears in `NodeCatalog.All` with the three ports; validator cases above (one test each, assert error text mentions the field); options endpoint returns only active connections and only `key,name,provider` (assert the JSON has no `server`/`userName`), 401 when not signed in; publish audit entry for a workflow with the node contains the SQL text and connection key but **no** parameter values.
- [ ] **Step 2:** `dotnet test --filter "QueryDatabase|DatabaseConnectionApi" -m:1` → FAIL.
- [ ] **Step 3:** Implement. Audit: add the SQL and connection key the same way `FolderInputSettings.TypedPaths` is added at `WorkflowService.cs:347`.
- [ ] **Step 4:** Regenerate artefacts: `$env:VERSO_UPDATE_OPENAPI = "1"; dotnet test --filter OpenApiDocument -m:1`, review the diff of `docs/openapi/v1.json`, then `cd src/verso-web; npm run gen:api`. Run the filter from Step 2 → PASS.

### Task 5: Engine step

**Files:** Modify `src/Verso.Application/Executions/ExecutionEngine.cs` (constructor gets `IDatabaseQuery query`; `ExecuteNodeAsync` case; new `QueryDatabaseAsync`; `Rejects`/`FinishAsync` `(QueryDatabase,"failed")` → Reject `data.query_invalid`; `RestoreAsync` branch for `QueryDatabase` + `found`/`not-found`: re-run the query, throw `StepException("query.restore", …, retryable: true)` if it fails), `tests/Verso.Api.Tests/ExecutionTestKit.cs:323`, `LoggingTests.cs:110`, `LeastPrivilegeTests.cs:403` (pass `new FakeDatabaseQuery()`); Create `tests/Verso.Api.Tests/FakeDatabaseQuery.cs`; Test `tests/Verso.Api.Tests/QueryDatabaseExecutionTests.cs`.

**Interfaces:** Consumes `IDatabaseQuery`, `DatabaseQueryStep.ParseParameters`, `SqlGuard`. `QueryDatabaseAsync(Run, WorkflowNode, CancellationToken) : Task<Outcome>` — resolves each parameter path against `run.Data` (missing → `Outcome("failed", new { issues })`, no query), runs, on `Code` with `Retryable` throws `StepException(code, message, retryable)`, non-retryable codes `db.*`/`sql.*` → `Outcome("failed", issues)`; rows empty → `"not-found"`; else merge into `run.Data` at `resultName` (1 row → object, several → array) using the same JSON rebuild helper `Merge` uses, port `"found"`, result `{ rows, columns, truncated, durationMs }`. `FakeDatabaseQuery` is scriptable (returns given rows / failure, records the request).

- [ ] **Step 1: Failing tests** with the XML+parallel sample: `Found_merges_rows_and_document_uses_them` (workflow: trigger → parse → query-database → generate-pdf; assert the generated text contains a value that only the fake query returned, and the step result has `rows=1`, **no values**), `Not_found_port_followed`, `Missing_parameter_rejects_without_calling_the_database`, `Retryable_failure_marks_step_failed_and_retry_works`, `Execution_dto_never_contains_row_values`, `Resumed_run_requeries` (approval step after the query; resume; fake called twice), `Parallel_branch_with_query_then_merge` (Parallel branches → B: query; A: nothing; Wait for branches).
- [ ] **Step 2:** `dotnet test tests/Verso.Api.Tests --filter QueryDatabaseExecution -m:1` → FAIL.
- [ ] **Step 3:** Implement as described.
- [ ] **Step 4:** Same filter → PASS; then `dotnet build Verso.slnx` to confirm the three test call sites compile.

### Task 6: Frontend

**Files:** Modify `src/verso-web/src/features/workflows/designer/SchemaForm.tsx` (widget union + `database-connection` and `sql` cases), Create `src/verso-web/src/features/workflows/designer/DatabaseConnectionSelect.tsx` (copy `EmailRelaySelect.tsx`; fetch `/api/v1/database-connection-options`), `src/verso-web/src/lib/api/databaseConnections.ts` (add `listOptions`); Test `DatabaseConnectionSelect.test.tsx` and a `QueryDatabaseNode.test.tsx` beside `EmailRelaySelect.test.tsx`; also add the node to the palette/icon map wherever `send-email` is listed (grep `send-email` in `src/verso-web/src`).

- [ ] **Step 1: Failing tests:** select lists the options and shows "No connections yet. Create one in Configuration, Connections" when empty; shows an unknown key as "Unknown connection (key)"; SQL box is monospace with the "use `@name`; a read-only account is recommended" hint; node appears in the palette.
- [ ] **Step 2:** `cd src/verso-web; npm test -- DatabaseConnectionSelect QueryDatabaseNode` → FAIL.
- [ ] **Step 3:** Implement. Connections page text: add the one-line "Use a read-only login" note to the database connection form.
- [ ] **Step 4:** Same command → PASS; `npm run typecheck`; `npm run build`.

### Task 7: Docs, ADR, close-out

**Files:** Create `docs/adr/0019-query-database-step.md` (context, decision, the exception, guards, the resume re-query call, consequences, "PostgreSQL / MySQL / Oracle runners not verified against live servers"); Modify `CLAUDE.md` (milestone line, "External integrations" safety list, replace "Database query step ... simulated" wording for the new step only, state the rule exception), `docs/setup.md` (read-only login advice), `docs/api.md` (done in Task 4), `docs/samples/` add `query-database-xml-customer.md` (XML + parse + query + merge + PDF example with synthetic data).

- [ ] **Step 1:** Write the documents.
- [ ] **Step 2:** Run the full check: `powershell -ExecutionPolicy Bypass -File tools/ci.ps1 -NoInstall`. Expected: build, frontend, all test projects (including browser E2E) and vulnerable-package check pass. Report any failure as it is; do not weaken tests.
- [ ] **Step 3:** Report in the close-out format of CLAUDE.md: what was built, commands and results, acceptance, limitations (resume re-queries; three providers unverified live; SQL authored by editors is run with the connection's login).
