# ADR 0012: Page rules for headers and footers

- Status: accepted (implemented 2026-11-14, awaiting acceptance)
- Requested by the product owner on 2026-11-14: the footer should be settable, for example shown on the first page, the last page or others.

## Context

A header or footer band is a Chromium print template. Chromium repeats it identically on every page, has no per-page condition, and runs with JavaScript disabled, so a rule can't be written inside the template. Word has "different first page" but nothing for "last page".

## Decision

1. **A rule per band.** `header` and `footer` in the layout JSON take an optional `showOn`: `all` (default, written as no property), `first`, `last`, `not-first`, `not-last`. It applies to the PDF and the Word file; the designer canvas and the HTML preview show the band once.
2. **PDF: print the runs, join them.** `PageRulePdfPrinter` (in `Verso.Rendering`, wired in the Worker around the Chromium printer) prints the document once without bands to learn the page count, prints each run of consecutive pages that share the same bands with Chromium's page ranges and the right templates, and joins the runs with the existing PDF combiner. The margins are identical in every print, so the body paginates exactly as with bands, and a page where a band is off keeps that space empty. A document with no rule takes the original single print. A page-count mismatch after joining fails the render rather than shipping a wrong document. At most three prints are needed (first, middle, last) plus the count.
3. **Word.** `first` and `not-first` use Word's "different first page" (an empty header or footer on the other pages). `last` writes the band at the end of the document, since Word has no last-page header or footer. `not-last` can't be expressed: the band stays on every page and the render reports the warning `band.page_rule_unsupported`.
4. **Designer.** Each band has a *Show on* choice (every page, first only, last only, every page except the first, every page except the last), and the band's tag on the canvas shows the rule. Because the space is kept on every page, the height hint says so.

## Consequences

- A rule costs extra Chromium prints (up to four instead of one) and a combine step, so such documents render a bit slower; they carry more repeated font data than a single print.
- A "last page only" footer leaves its space empty on the other pages. A footer that should sit directly under the body on the last page is a different feature (flow content at the end), not this one.
- The HTML preview and the canvas don't simulate pages for the bands; **Preview (PDF)** shows the real result.

## Addendum: page numbers (2026-11-15)

Reported by the product owner after importing a Word document whose footer read "Page 1 of 1" on every page. Word's page-number fields were imported as their current text, and the layout had no way to show a live page number.

- Header and footer text can use the reserved tokens `{{page}}` and `{{pages}}` (see layout-model.md, "Page numbers"). In the Chromium print templates they become Chromium's own page-number and total-pages spans. Chromium keeps the real document numbers when a page range is printed (checked with real Chromium: pages 3.. of 7 read "3 of 7", "4 of 7"…), so the page-rule printer above needs no change and the numbers stay right under "first page only", "all but the first" and so on.
- Word output gets PAGE and NUMPAGES fields; the Word and RTF importers turn those fields, in a header or footer, into the tokens.
- No new package and no change to the API: the tokens are part of the layout JSON.
