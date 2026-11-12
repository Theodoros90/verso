# ADR 0005: Combine PDFs step (several generated PDFs into one pack)

- Status: accepted (implemented 2026-11-12, awaiting acceptance)
- Requested by the product owner on 2026-11-12: build the prototype's *Combine PDFs* step ("Joins several PDFs into one pack") for real. Plan §9 listed it under "Not in MVP".

## Context

The prototype's step has an input that accepts several connections, plus *Page order* and *Add bookmarks*. Our engine follows one path and holds one current document, so a true fan-in (several branches into one step) does not exist and was not built. Joining PDFs also needs a PDF library; PdfPig is test-only and cannot write a merged file with bookmarks.

## Decision

1. **What is combined.** The PDFs the run has already generated: the same execution and, inside a For each loop, the same item (iteration); only `application/pdf` documents (Word and email files are ignored); never this step's own output. They are taken in the order they were made (`order: generated`, default) or reversed (`reverse`). The prototype's "Invoice first, then attachments" is not offered: attachments don't exist in Verso yet. A pack across all items of a loop is not built either.
2. **Settings and ports.** `order`, `bookmarks` (one bookmark per combined file, named after its file name, pointing at its first page) and `fileName` (pattern, default `{{invoice.number}}-pack.pdf`); ports `success` and `failed`. The step has no template, so nothing is pinned; the new document has a null `TemplateVersionId` (nullable since ADR 0004). Archive stores it like any generated document. The source PDFs stay as documents of the execution (generated, not archived) unless a workflow archives them separately.
3. **Fewer than two PDFs** is found while running (it depends on the path taken and on loop items): port `failed`, and the run ends **Rejected** with `combine.not_enough_documents` unless the workflow handles the port.
4. **Library: PDFsharp 6.2.4** (MIT), approved by the product owner on 2026-11-12 and pinned in `Directory.Packages.props`. **Licence warning:** PDFsharp 6.5.0 (the latest stable) and the 7.x previews are AGPL-3.0-only; 6.2.4 is the last MIT release. Do not upgrade without a licence decision from the bank. The consequence of staying on 6.2.4 is that later PDFsharp fixes are not picked up.
5. **Layering.** `Verso.Rendering` stays pure and gains the `IPdfCombiner` contract (parts in, one PDF and its page count out). A new project `Verso.Rendering.PdfCombine` (Rendering + PDFsharp, referenced only by `Verso.Worker`, like `Verso.Rendering.Docx`) implements it. It never sees templates, data or bindings; it only copies pages between PDFs.
6. **Integrity.** Each source is read back from storage and its SHA-256 compared with the recorded hash before it is combined (a mismatch is a retryable technical failure, `storage.integrity`). A part that can't be read as a PDF fails the step without retry (`combine.invalid_pdf`); the error names the file, never its content. The output is stored through `IDocumentStore` under a Verso-generated key and is idempotent per (execution, node, iteration), like the other document steps.

## Consequences

- Page copying keeps each source's page size; metadata of the sources is not carried over (the pack's title is generic).
- No preview in the UI; download only, as for other documents.
- Not done: fan-in of branches, packs across loop items, attachments as a source, page ranges, a table of contents page, PDF/A output.
