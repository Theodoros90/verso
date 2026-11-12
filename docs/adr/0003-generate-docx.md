# ADR 0003: Generate DOCX step (an editable Word document)

- Status: accepted (implemented 2026-11-12, awaiting acceptance)
- Requested by the product owner on 2026-11-12: build the prototype's *Generate DOCX* step ("Renders an editable Word document") for real. Plan §9 listed DOCX output under "Not in MVP".

## Context

The PDF step prints the renderer's HTML with Chromium. Word cannot be produced that way, so the same template layout and data need a second output path, and a library that writes the Open XML format.

## Decision

1. **Library: `DocumentFormat.OpenXml` 3.5.1** (Microsoft, MIT), approved by the product owner on 2026-11-12 and pinned in `Directory.Packages.props`. It runs in the Worker only.
2. **Layering.** `Verso.Rendering` stays pure and gains `Docx/DocumentResolver` (layout + data → a plain-text `ResolvedDocument`, same binding rules and the same issue codes as the HTML renderer in Final mode) and the `IDocxWriter` contract. A new project `Verso.Rendering.Docx` (Rendering + Open XML SDK, referenced only by `Verso.Worker`, mirroring `Verso.Rendering.Pdf`) implements it. The writer never sees layout JSON, data or bindings, so there is nothing to evaluate. A new project was needed to keep the SDK out of the pure renderer (RendererPurityTests).
3. **The step `generate-docx`** has the PDF step's ports (`success`, `failed`) and settings (`templateId`, `templateVersion`, `fileName`), plus **`protect`** (Editing): `allow` (Allow edits), `trackedChanges` (Tracked changes only), `readOnly` (Read only). Template versions are pinned at submission, issues reject the run (`document.data_incomplete`), the file is stored through `IDocumentStore` and archived by the Archive step exactly like a PDF. Documents carry `contentType`; storage keys and file names end in `.docx`.
4. **Editing restriction is advisory.** It sets Word's document protection without a password (and turns on Track Changes for *Tracked changes only*), so a person can lift it. It states intent; it is not a security control. Use PDF for documents that must not change.
5. **Fidelity limits.** Word has no absolute positioning in a header or footer, so positioned elements become paragraphs in reading order (top to bottom, then left to right); *box* elements (decoration only) are omitted; fonts are Word's standard ones (Arial, Times New Roman, Courier New) because the template fonts can't be assumed installed; header and footer height follow Word's rules, not the template's band height. Tables repeat their headings on each page and rows don't split. No migration: `Documents.ContentType` already existed.

## Consequences

- The Worker's installed package set grows by the Open XML SDK; the offline-bundle procedure in docs/setup.md is unchanged (nuget restore only).
- A DOCX is not pixel-identical to the PDF of the same template. Layout features added later (page numbers, columns) need a mapping in the writer as well as in the HTML renderer.
- Not done: password-protected restrictions, DOCX preview in the template designer, converting DOCX to PDF.
