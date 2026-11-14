# ADR 0011: Import Word and RTF documents into the template designer

- Status: accepted (implemented 2026-11-14, awaiting acceptance)
- Requested by the product owner on 2026-11-14: the designer could only be filled in by hand; it should import DOCX, DOC, RTF and similar, so a template doesn't have to be designed manually. The design reference (`Verso-prototype-v14-Dynamic-Data-and-PDF.html`, "Import DOCX") reads a `.docx` in the browser into editable blocks and says plainly that the result is approximate. `.doc` was decided on 2026-11-14: **not supported yet**, with advice to save as `.docx` (option chosen over adding a conversion library or installing LibreOffice).

## Decision

1. **Frontend only, no new package, no new endpoint.** The file is read in the browser (`features/templates/import`) and never uploaded. The result goes into the draft through the designer's normal edit path, so it is one undo step, nothing is saved until *Save draft*, and the server validates it like any other save.
2. **Formats.** DOCX (ZIP + XML; a small ZIP reader using the browser's `DecompressionStream`, `DOMParser` for XML) and RTF (a hand-written reader: groups, escapes, code pages, colour and font tables, tables, pictures, headers and footers). The format is recognised by the file's **content**, not its name, so an RTF saved as `.doc` works. A genuine binary `.doc` is refused with "open it in Word and Save As → .docx".
3. **Mapping to layout model v1.** Paragraphs become text blocks (neighbouring paragraphs in one style merge; blank paragraphs become `spaceBeforeMm`; size, bold, italic, alignment, colour and sans/serif/mono come across; text such as `{{customer.name}}` is kept as a field). Images become image blocks (PNG and JPEG up to the layout's size limit). A table with two or more rows and columns becomes a repeating table: the first row gives the headings, the second row gives the column bindings (a `{{field|filter}}` in a cell, else a name guessed from the heading) and number-like cells are right-aligned; rows below that are dropped. A one-row or one-column table is page layout and becomes text. Header and footer content becomes positioned elements in the header and footer bands, and page margins and orientation come from the file. Everything not listed (floating objects, text boxes, columns, numbering, mixed formatting inside a paragraph, exact positions, fonts) is simplified or dropped, and an **import report** says so.
4. **Publishing stays gated.** Imported table lists and columns are guesses, so they show as "not in the data model" until the author maps them; publishing is blocked until then (unchanged rule).
5. **Limits.** Files up to 20 MB, 5,000 paragraphs, 200,000 XML nodes, 500 layout elements, images up to ~500 KB; parts larger than 30 MB or that inflate beyond their declared size are refused (zip bombs); documents with a DOCTYPE/ENTITY declaration are refused (entity expansion); password-protected and ZIP64 packages are refused; external image links are never followed.

## Consequences

- Fidelity is "a good starting point", not a copy of Word. Pixel-faithful import would need a server-side conversion engine, which is a separate decision (plan §10).
- `.doc` users take one extra step in Word. Adding a library in the Worker or LibreOffice on the server would change this, and each needs approval.
- Because the parsing happens in the browser, its safety relies on the checks above and on the layout validator; the server still validates every save.
