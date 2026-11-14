import { pageSizeMm, type PageSettings } from './layout'

/** The body space one PDF page offers, in mm: the page height minus margins and the header and footer bands (docs/layout-model.md, "Rendering"). */
export function bodyPerPageMm(page: PageSettings, headerMm: number, footerMm: number): number {
  const { marginsMm } = page
  return pageSizeMm(page).height - marginsMm.top - marginsMm.bottom - headerMm - footerMm
}

/**
 * Where the PDF would start a new page, so the designer can leave the same space. A block is never split across a page:
 * when it doesn't fit in what is left, it moves to the next page. A block taller than a whole page (a long table) starts
 * where it falls and continues over the following pages.
 *
 * @param extents the height each body block takes, spacing before it included, in any one unit
 * @param capacity how much body one page holds, in the same unit
 * @param start how much of the first page is already used above the first block
 * @returns for each block, how much empty space is left on its page when it has to move to the next one (0 = it stays; a break on an exactly full page reports a token 0.01)
 */
export function paginate(extents: number[], capacity: number, start = 0): number[] {
  const gaps = extents.map(() => 0)
  if (!(capacity > 0)) return gaps
  let used = start
  extents.forEach((extent, i) => {
    if (used > 0 && used + extent > capacity + 0.5) {
      gaps[i] = Math.max(0.01, capacity - used)
      used = 0
    }
    used += extent
    if (used > capacity) used %= capacity
  })
  return gaps
}
