# Plan: named email relays for the Send email step

Design: ADR 0006 addendum 1. Pattern to copy: Print queue (ADR 0007, `PrintQueues`, `Print:Queues`).
Approved by the product owner 2026-11-16; implemented, tests pending (see the close-out).

## Open decision
Typed key (as the Print queue step does, no API change) or a dropdown of relay names (the API must read `Email:Relays`, so the OpenAPI snapshot and generated types change). Recommendation: typed key first.

## Steps
1. Read `PrintQueues`, the print spooler options binding and its tests; mirror their key pattern and options binding.
2. `EmailOptions`: add `Relays` (dictionary key to relay settings). Bind in `DependencyInjection.cs` next to the current section. The top-level section stays and is relay `default`.
3. `IEmailSender.SendAsync(string? relay, EmailMessage, ct)`; `SmtpEmailSender` picks the options for the key, throws `email.relay_unknown` (not retryable) for an unknown one.
4. `NodeCatalog.SendEmail`: add optional `relay` (pattern like the print queue key, help text naming the administrator's keys); keep the sample JSON working without it.
5. `ExecutionEngine` send step (around line 770-822): read `relay`, pass it, add the key to the step result.
6. Frontend: nothing if the schema form renders the new setting; check `NodeCard.tsx` summary shows the relay.
7. Docs: `docs/setup.md` and `docs/runbook.md` (configuring relays, matching keys across servers, secret names), `appsettings.json` example, plan §10 if relevant, CLAUDE.md milestone line when implemented.

## Tests
- Unit: relay lookup (default, named, unknown), per-relay allowed domains, per-relay not configured.
- Engine: step with no `relay` still sends through `default`; step with a key uses that relay; unknown key ends Failed without retry; result has the key and no address.
- Catalog/validator: `relay` accepted when valid, rejected when it doesn't match the key pattern.
- OpenAPI and golden tests only if the contract changes (`VERSO_UPDATE_OPENAPI=1` and review the diff).

## Verify
`dotnet build Verso.slnx`, `dotnet test Verso.slnx -m:1`, `npm run typecheck`, `npm test` in `src/verso-web`; report real results.
