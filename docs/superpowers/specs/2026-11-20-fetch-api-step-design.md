# Fetch from API step (Phase 3): design

Date: 2026-11-20. Status: **awaiting owner approval**. Will become ADR 0021. Builds on ADR 0017 (API connections, test only), ADR 0019 (Query database step) and ADR 0020 (Write to database step); branch `feature/fetch-api-step`, started from the unmerged Phase 2 branch.

## Intent
A workflow can read data from a web API during a run, using an API connection from Configuration: for example look up a customer's exchange rate or status by a value from the parsed XML, merge the answer into the run data, and generate a document from the combined data. The same way Query database reads a database.

## Owner decisions already taken
- Anyone who can edit a workflow may author steps that use connections (decided for the database steps on 2026-11-18; applied here).
- Phase by phase: read first (this phase), then the step that changes data (Phase 3b, below), each with its own approval.
- Free-typed values are only ever bound as parameters (here: URL-encoded path segments and query values), never glued into text.

## Goals (Phase 3, read only)
- New step **Fetch from API** (node type `fetch-api`, category process). HTTP **GET** only.
- Settings: `connection` (API connection key), `path` (template starting with `/`, may contain `{{data.path}}` pieces whose values are URL-encoded; no `?`, max 500), `query` (lines `name = data.path`, max 20; names and values URL-encoded), `resultName` (default `api`, same rules as Query database, not `iteration`), `maxResponseKB` (1..256, default 64), `timeoutSeconds` (1..60, default 15).
- Ports: `found` (2xx with a JSON answer), `not-found` (HTTP 404, or an empty answer such as 204), `failed` (a missing or non-scalar data value only, ends the run Rejected like the database steps).
- The JSON answer (object, array or a plain value) is merged into the run data under `resultName` (replacing a top-level field of that name). A non-JSON answer fails the step (`api.not_json`); an answer above the size limit fails it (`api.response_too_large`).
- The host can never change: the connection fixes the base address, the author supplies only a path and query values, so a data value cannot send the Worker to another host.

## Non-goals
- No POST, PUT, PATCH or DELETE, no request body, no custom headers, no file or document upload: that is Phase 3b (below) and the existing simulated **Deliver via API** step stays untouched.
- No XML or text answers, no pagination, no response caching, no following redirects, no other OAuth flows (ADR 0017 limits stand).

## Anti-goals
- A step must never call a host the connection does not name, never put a secret, a request address or an answer in a log, event, audit entry or step result, and never keep an answer anywhere except the run data.

## Design
1. **Shared HTTP sending.** Extract what `HttpApiProbe` already does (handler with the blocked-address guard and TLS, no redirects, https-only address rules, `ApiConnections:AllowedHosts`, auth application incl. the OAuth client-credentials token request) into one shared Infrastructure component used by the probe and the new sender; the probe's behaviour and tests stay unchanged.
2. **`IApiFetch` / `ApiFetchService`** (Application, Worker-only like the database services): resolves the connection (active, host allowed, secret decrypted in memory), builds the address (base + encoded path + encoded query; for the "API key in query" kind the key is appended as today), sends one GET with the configured timeout, reads at most `maxResponseKB` of the answer, parses JSON. Messages are Verso's own words (the `api.*` codes of ADR 0017 plus `api.not_json`, `api.response_too_large`, `api.bad_request` for refused addresses); the answer's text is never put in a message.
3. **Retry and resume.** A GET is safe to repeat: timeouts, connection failures, HTTP 5xx and 429 are retryable (the engine's three attempts); other 4xx, auth failures and token failures are not. On resume of a run that already passed the step, the engine fetches again to rebuild the data (as Query database does), with the same caveat (the answer may have changed) and the same `fetch.restore` failure if it fails then.
4. **Privacy.** Step result: `{ status, durationMs, responseBytes, connection }` only (the API returns step results to every execution reader). Publish audit: `apiFetches: [{ nodeId, connection, path, queryNames }]` (the path template and the query parameter names, never values). Logs carry codes only.
5. **Options endpoint** `GET /api/v1/api-connection-options` for workflow authors and administrators: `[{ key, name, host }]` of active connections, nothing else (no secret, no auth kind, no base path).
6. **Validation at publish:** connection key shape, path rules (single leading `/`, no `//` start, no spaces or control characters, no backslash, no `?`, `{{ }}` pieces are valid data paths), query lines `name = data.path` (names `[A-Za-z0-9_.-]`, no duplicates), limits. Publishing does not look up the connection.
7. **Frontend:** the connection select (a new widget fed by the options endpoint, like the database one), the path box with a hint, the query lines, the two limits, node card icon and summary, ports Found / Not found / Failed.
8. **Tests:** all HTTP is faked with a message handler: no test calls the network. Real egress is documented as a manual check (for example the Frankfurter example of ADR 0017: `GET /v1/latest` with `base` and `symbols`). Only the shared sender's address and TLS guards are covered by unit tests; nothing is verified against a live third-party server.
9. **Docs:** ADR 0021, CLAUDE.md (milestone line; the "approved so far" list gains "reading JSON from the API connections an administrator creates, with GET requests only, from the Fetch from API step"), api.md, runbook, setup.md (allowed hosts for the Worker's egress, read-only API credentials), a sample ("look up an exchange rate for an invoice").
10. **No migration, no new package.**

## Phase 3b outline (not part of this approval; its own design and approval later)
**Send to API** (`send-api`): POST, PUT, PATCH, DELETE. It would copy the Phase 2 pattern: a per-connection **"Allow calls that change data"** switch (administrators, off by default, migration), a JSON body built from a template whose `{{data.path}}` pieces are inserted into the parsed JSON tree (so a value can never break the structure), an `Idempotency-Key` header derived from the execution, node and item (so a person's retry after an unknown outcome is safe where the API honours it), never retried automatically, never run again after it was started (the Running-step guard of ADR 0020, generalised), the loop-fingerprint protection extended to loops containing it, answers **not** merged into the data (they can't be rebuilt on resume without calling the API again) unless a later decision stores them. The hazards are the same as the database writes (an unknown outcome after a timeout, any editor can use a switched-on connection) plus that the remote side effects can't be rolled back.

## Risks the owner should know
- **Outbound calls from the Worker** to hosts administrators named: set `ApiConnections:AllowedHosts` in production and make sure the Worker's egress allows them.
- **Credentials:** a read-only or least-privilege key per connection; a GET with the key in the query string puts the key in the address, which the remote side may log (ADR 0017).
- **The answer is third-party data** merged into documents: the template decides what is shown; a very large or odd answer is bounded by the size limit and the JSON-only rule.
- **Nothing is verified against a live API in tests** (fake handler only).
- **A GET can still have side effects** on badly designed APIs; the step cannot know.

## Approaches considered
- **A. Two steps, Fetch from API (GET) now and Send to API later (proposal).** Same split as Query / Write: the palette and audit show which step changes data, the read step stays free of switches and "outcome unknown" rules.
- **B. One Call API step with a method setting.** One node, but changing the method silently turns a read into a change and complicates validation, retries and the never-repeat rules (the reason Phase 2 chose a separate node).
- **C. Extend the simulated Deliver via API step.** That step is about posting the generated document; real document upload (multipart, large files) is a different feature.

## Left to the builder
Exact refusal wording, helper-text wording, the shared sender's internal class names, form layout, test shapes.
