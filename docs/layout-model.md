# Template layout model v1

The layout is the `layout` JSON of a template version. It's parsed and validated by `Verso.Rendering.Layout.LayoutParser` (strict: unknown properties are errors) and rendered by `Verso.Rendering.Html.HtmlRenderer`. A complete example is [tests/fixtures/invoice/layout.json](../tests/fixtures/invoice/layout.json).

## Structure (plan decision D4)

```json
{
  "layoutSchemaVersion": 1,
  "header": { "heightMm": 62, "elements": [ /* positioned elements */ ] },
  "body":   { "blocks":   [ /* flow blocks */ ] },
  "footer": { "heightMm": 10, "elements": [ /* positioned elements */ ] }
}
```

- **header / footer:** fixed-height bands (0–150 mm), printed on **every page** unless `showOn` says otherwise (below). Elements are absolutely positioned inside the band, in mm, relative to the page margins: `{ "id", "type", "xMm", "yMm", "widthMm", "heightMm", "props" }`. Content outside the band is clipped.
- **body:** blocks stack top to bottom across the content width and flow onto further pages: `{ "id", "type", "spaceBeforeMm"?, "props" }`. A repeating table breaks between rows and repeats its column headings on each page.
- **`showOn`** (header and footer, optional): `all` (default), `first`, `last`, `not-first` or `not-last`, written in lower case with hyphens. The PDF prints the band only on those pages and keeps its space empty on the others; a one-page document counts as both first and last. In Word, `first` and `not-first` use "different first page", `last` writes the band at the end of the document, and `not-last` stays on every page with the warning `band.page_rule_unsupported`. The designer and the HTML preview show the band once. See [ADR 0012](adr/0012-header-footer-page-rules.md).
- `header`, `body` and `footer` are optional; `{ "layoutSchemaVersion": 1 }` is a valid (empty) layout.
- Element `id`s: 1–64 characters `A–Z a–z 0–9 - _`, unique in the layout. At most 500 elements.
- Page size and margins come from the version's `pageSettings`, not the layout.

## Element types

| `type` | Allowed in | `props` |
|---|---|---|
| `text` | header, body, footer | `text` (may contain `{{path}}` / `{{path\|filter}}`; `\n` = line break), `style`? |
| `field` | header, body, footer | `binding` (data path), `filter`?, `label`?, `style`? |
| `table` | body | `binding` (path to an array), `columns`: `[{ header, binding (relative to the row), filter?, align?, widthPct? }]` (≥ 1; widths ≤ 100% total), `emptyText`?, `style`? |
| `divider` | header, body, footer | `thicknessPt`? (0.25–6, default 0.75), `color`? |
| `box` | header, footer | `borderColor`?, `fill`?, `radiusMm`? (0–20) |
| `image` | header, body, footer | `src`: `data:image/png;base64,…` or `data:image/jpeg;base64,…` only (≤ ~500 KB). External URLs and SVG are rejected. `alt`?, `fit`? (`contain` \| `cover`) |
| `signature` | header, body, footer | `label`? (default "Authorised signatory") |

`style` (all optional): `fontSizePt` (5–48, default 10), `bold`, `italic`, `align` (`left` \| `center` \| `right`), `color`, `font` (`sans` \| `serif` \| `mono`).

Colors are always `#RRGGBB`. Enum values are lower case.

## Page numbers (header and footer)

In the **text** of a header or footer element, `{{page}}` is the number of the page it is printed on and `{{pages}}` is how many pages the document has, so `Page {{page}} of {{pages}}` counts correctly on every page. Both tokens are reserved only there, and only without a filter: in the body, or as `{{page|upper}}`, they are ordinary data paths. The designer shows them as 1 and N; the sheet preview shows page 1 of 1; the PDF fills in the real numbers (also when a [`showOn`](adr/0012-header-footer-page-rules.md) rule prints the band on some pages only, because numbering follows the whole document); a Word file gets live PAGE and NUMPAGES fields. Importing a Word or RTF document turns its page-number fields in the header and footer into these tokens.

## Bindings and filters

A data path is dotted property names: `invoice.number`, `customer.address`. No indexing, functions or expressions. Table column bindings are relative to the row (`description`, `amount`).

| Filter | Input | Output |
|---|---|---|
| `money` | number or numeric string | `€1,234.50`, `-€12.00` (2 decimals, half away from zero) |
| `number` | number or numeric string | `18,450`, `0.2185` |
| `date` | `YYYY-MM-DD` or ISO 8601 date-time | `02 Nov 2026` (the date as written; no time-zone shift) |
| `year` / `month` / `day` | same as `date` | `2026` / `11` / `02` |
| `upper` / `lower` | any single value | case-converted text |

Formatting is fixed (English, EUR) and doesn't depend on the server's culture. Other locales and currencies are an open decision (plan §10).

## Rendering

`HtmlRenderer.Render(layout, page, data, mode, title)` returns:

- `Html`: a self-contained preview document, one continuous sheet (header, body, footer), for screens. Page breaks are only visible in the PDF.
- `Print`: the same content split for Chromium: `BodyHtml` (flows across pages), `HeaderTemplate` / `FooterTemplate` (Chromium repeats them in the top/bottom margin of every page), paper size, and top/bottom margins (page margin + band height). The PDF step (M5) passes these to `Page.printToPDF` with `displayHeaderFooter`. Verified with Chromium in M3: a 60-line invoice paginated over 3 pages with the full header, footer and table headings on each.
- `Issues`: problems found while rendering.

Modes: **Preview** shows unresolved values as a dashed `{{path}}` placeholder. **Final** renders them empty. Both report the issue, so the execution engine can reject an incomplete document.

PDF (Verso.Rendering.Pdf, Worker only): Chromium prints the `Print` parts with JavaScript disabled, offline, and every request aborted. Fonts: the HTML names Schibsted Grotesk, Source Serif 4 and IBM Plex Mono, with system fallbacks. The PDF step inlines those fonts (static 400/600 weights, normal and italic; latin subset, including €) as `data:` URIs, so PDFs embed real subset fonts. Static rather than variable fonts, because Chromium turns variable fonts into Type 3 outlines. The screen preview keeps the system fallbacks. Bold (`"bold": true`) is weight 600.

Safety: every text and data value is HTML-encoded (data that looks like `{{…}}` is printed literally, never re-evaluated). CSS values only come from validated numbers, enums and colors. Documents carry a Content-Security-Policy (`default-src 'none'`), so no scripts and no network requests. The renderer has no file, network, database, ASP.NET or Playwright dependencies (checked by `RendererPurityTests`).

## Issue codes

| Code | Severity | Meaning |
|---|---|---|
| `binding.missing` | error | No value at the path (missing, or JSON `null`) |
| `binding.not_scalar` | error | The path points at an object or array where a single value is needed |
| `table.not_array` | error | A table's binding isn't a list |
| `token.malformed` | error | Text contains `{{…}}` that isn't a valid binding (bad path or unknown filter) |
| `filter.invalid_input` | warning | The filter can't format the value (e.g. `money` of `"abc"`); the raw value is shown |
| `band.page_rule_unsupported` | warning | Word output only: a header or footer set to `not-last` can't be left off the last page, so it stays on every page |

An empty list is not an issue: the table shows `emptyText` (if set) or no rows.

## Not in v1

QR codes, barcodes, conditional blocks, page numbers in the body (they exist in headers and footers only), fonts in the screen preview (they're embedded only in PDFs), side-by-side columns in the body, and per-template locale/currency.

## Word (DOCX) output

The Generate DOCX step (ADR 0003) uses the same layout and bindings. `Verso.Rendering.Docx.DocumentResolver` applies the data (same rules and the same issue codes as `HtmlRenderer` in Final mode) and `Verso.Rendering.Docx` (Worker only) writes the result with the Open XML SDK. Mapping: `text`/`field` → paragraphs (size, bold, italic, colour, alignment; fonts Arial / Times New Roman / Courier New); `table` → a Word table whose heading row repeats on each page; `divider` → a bordered paragraph; `image` → an inline picture (header/footer: the element's size; body: its own size up to the content width); `signature` → a line with the label under it; `box` → omitted. Header and footer elements become paragraphs in reading order (Word can't position them absolutely). Page size, orientation and margins come from the page settings.
