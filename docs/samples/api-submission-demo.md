# Sample: API submission demo

A system (or person) submits an invoice as JSON, Verso makes a PDF and archives it. It shows the **API submission** step (the workflow's trigger) working end to end. Synthetic data only (`tests/fixtures/invoice`).

## What it builds

- A template `DEMO-INV-<time>` ("Demo invoice"), published, from `tests/fixtures/invoice/layout.json`.
- A workflow `api-submission-demo-<time>`, published: **API submission** → **Generate PDF** (`{{invoice.number}}.pdf`) → **Archive** (folder `demo/{{customer.name}}`, metadata Invoice and Customer).

The API submission step's path and authentication settings are descriptive only: nothing listens at the path. The run is started by the real endpoint, `POST /api/v1/workflows/{key}/executions` (here as the synthetic Operator `dev.operator`, with an `Idempotency-Key`).

## Run it

With the API and the Worker running (Development sign-in, Chromium installed, see docs/setup.md):

```powershell
powershell -ExecutionPolicy Bypass -File tools/samples/load-api-submission-demo.ps1
```

Expected: `Status: Succeeded`, three steps `Succeeded`, and a PDF named `INV-2026-003901.pdf` in the generate step's result. Open **Executions** for the run and **Archive** for the document.

Options: `-NoSubmit` only publishes (submit yourself, see docs/setup.md "Running a workflow"); `-Broken` submits `tests/fixtures/invoice/broken.json`; `-ApiUrl` points at another API. Each run creates a new template and workflow with a time suffix; delete nothing by hand, deactivate the workflow from the workflow list if it is in the way.
