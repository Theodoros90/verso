# Sample: XML invoice batch

One XML file with several invoices goes in; each invoice comes out as PDFs, routed by rules, with every PDF having a second page. Everything is synthetic. It uses only steps that exist today; nothing here needs a new feature.

Files (all in [tests/fixtures/xml-batch](../../tests/fixtures/xml-batch)):

| File | What it is |
|---|---|
| `invoices.xml` | The input: three invoices (domestic, German customer, domestic over 10,000) |
| `workflow.json` | The workflow definition (14 steps) |
| `invoice.layout.json` | Template 1: invoice (page 1) + payment instructions and terms (page 2) |
| `notice.layout.json` | Template 2: cross-border VAT notice (page 1) + customer declaration (page 2) |
| `sample-data.json` | The data model both templates read; their sample data |

## The flow

```
Folder input (*.json) ─ Parse XML ─ For each invoice ──┐
                                                       │ per invoice (own step rows, own documents)
   Map fields ─ Transform ─ Validate ─ If gross ≥ 10,000 ── yes ─ Approval ─┐
                              │                          └─ no ───────────────┤
                              └ invalid → run Rejected                       ▼
                                                                  Generate PDF: invoice (2 pages)
                                                                              │
                                                    If customer country ≠ CY ─┤
                                         yes ─ Generate PDF: VAT notice (2 pages) ─ Combine PDFs ─ Archive pack
                                         no ─ Archive invoice
```

| Step | Does |
|---|---|
| Folder input | Scans the folder for `*.json` files and starts a run per file |
| Parse | Reads the XML text in the file's `content` field; `recordPath: InvoiceBatch` makes the batch the run's data |
| For each (`split`) | One pass per `<Invoice>`; each invoice is its own record |
| Map fields | `lines.line` → `lines` (XML wraps list items in an element); `dd/MM/yyyy` dates → `YYYY-MM-DD`; country upper-cased |
| Transform | Defaults the currency to EUR; builds `customer.label` ("Name (DE)") |
| Validate | Required fields, date, number, email, EU VAT and IBAN checks; failing = run **Rejected** |
| If / else (gross ≥ 10,000) | Yes → **Approval** (role Operator, overdue after 24 h); no → straight on |
| Generate PDF | Invoice template, always 2 pages |
| If / else (country ≠ CY) | Yes → second PDF (VAT notice, 2 pages) → **Combine PDFs** into one pack with bookmarks → Archive the pack; no → Archive the invoice |

## Second pages

The layout model has no page-break element. Each template puts two invisible full-height spacer blocks (white dividers with `spaceBeforeMm: 100`) after its page-1 content, so the page-2 section always starts on a new page. Header and footer repeat on both pages. Checked in real Chromium for 2, 3 and 8 invoice lines (`SecondPageTemplateTests`). A page-1 table longer than about one page would add pages; a real page-break element would be the clean fix (not built).

## Load it

Needs the API running with `Auth:Mode=Development` ([setup](../setup.md)):

```powershell
powershell -ExecutionPolicy Bypass -File tools/samples/load-xml-invoice-batch.ps1 -Submit
```

It creates and publishes the two templates and the workflow (unique keys per run) and, with `-Submit`, sends `invoices.xml` as one run. Open the workflow in the designer to see the graph; open the run in History to see the steps per invoice. `-InboxRoot` sets the three folders of the Folder input step (default `D:\Verso\xml-batch`).

## Feeding it a file

Folder input reads **JSON files only** ([ADR 0001](../adr/0001-folder-input.md)), so a dropped `.xml` file would go to the failed folder. The XML therefore travels inside a JSON file: `{ "content": "<?xml …>" }`. To make one:

```powershell
powershell -ExecutionPolicy Bypass -File tools/samples/load-xml-invoice-batch.ps1 -WrapXml tests/fixtures/xml-batch/invoices.xml
```

It writes the file under a temporary name and renames it, as the folder convention requires. The same XML can also be submitted straight to `POST /api/v1/workflows/{key}/executions` as `{ "data": { "content": "<xml>" } }`.

## Things to know

- **Approval inside a loop:** decide with `POST /api/v1/executions/{id}/approvals/{nodeId}` and include `"iteration": <n>` (the invoice's position, here 3); without it the API answers 404.
- **First failure ends the run:** a Rejected invoice (bad data) stops the whole file; later invoices are not processed. A retry resumes in the item that failed.
- **XML lists need two or more entries.** A single `<line>` is read as one object, not a list, and the invoice table would fail with `table.not_array` (run Rejected, `document.data_incomplete`).
- **Archive:** a domestic invoice's PDF is archived; for a cross-border invoice the pack is archived, and the invoice and notice inside it are not archived separately. At most 200 invoices per file.
- **Send email / Print queue:** not in the sample, because they need an SMTP relay and spool folders configured by an administrator (ADR 0006, 0007). They fit after Generate PDF.

## Tests

- `tests/Verso.Api.Tests/XmlInvoiceBatchSampleTests.cs`: publishes the templates and workflow from these files and runs the real engine (fake printer): routing per invoice, the approval pause and resume, rejected data, invalid XML.
- `tests/Verso.Pdf.Tests/SecondPageTemplateTests.cs`: prints both templates with Chromium and checks two pages and their text.
