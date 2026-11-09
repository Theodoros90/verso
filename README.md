# Verso

Document-generation and workflow platform for the bank: maintain invoice templates and workflows, validate submitted invoice data, generate PDFs, archive them, and show execution history.

- Setup and running locally: [docs/setup.md](docs/setup.md)
- API reference (implemented endpoints): [docs/api.md](docs/api.md)
- Architecture and milestones: [docs/plan/2026-11-09-verso-mvp-plan.md](docs/plan/2026-11-09-verso-mvp-plan.md)
- Rules for contributors and coding agents: [CLAUDE.md](CLAUDE.md)

```
src/Verso.Api             ASP.NET Core Web API (REST, later SignalR)
src/Verso.Worker          Background worker process (later Hangfire + PDF rendering)
src/Verso.Application     Use cases
src/Verso.Domain          Domain model
src/Verso.Infrastructure  EF Core (SQL Server), migrations, health checks
src/verso-web             React + TypeScript + Vite SPA
tests/                    Backend tests (frontend tests live next to the source)
docs/                     Plan, setup, API reference
design-reference/         Visual prototype (reference only, not production code)
```
