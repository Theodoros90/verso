import { describe, expect, it } from 'vitest'

import { DEFAULT_PAGE_SETTINGS } from './layout'
import { bodyPerPageMm, paginate } from './pages'

describe('bodyPerPageMm', () => {
  it('is the page height minus margins and both bands', () => {
    expect(bodyPerPageMm(DEFAULT_PAGE_SETTINGS, 62, 10)).toBe(297 - 18 - 18 - 62 - 10)
    expect(bodyPerPageMm({ ...DEFAULT_PAGE_SETTINGS, orientation: 'landscape' }, 0, 0)).toBe(210 - 36)
  })
})

describe('paginate', () => {
  it('keeps everything on one page while it fits', () => {
    expect(paginate([40, 40, 40], 150)).toEqual([0, 0, 0])
  })

  it('moves a block that would cross the boundary to the next page, reporting the space left behind', () => {
    // 100 + 40 = 140 fits; the next 30 would end at 170 > 150, so it starts page 2 leaving 10 empty
    expect(paginate([100, 40, 30, 20], 150)).toEqual([0, 0, 10, 0])
  })

  it('starts counting after what already sits above the first block', () => {
    expect(paginate([60, 60], 150, 40)).toEqual([0, 50])
  })

  it('handles several pages, each starting fresh', () => {
    expect(paginate([100, 100, 100, 100], 150)).toEqual([0, 50, 50, 50])
  })

  it('lets a block taller than a page start where it falls and carry on over the next pages', () => {
    // the 400 starts at 50 (fits nothing more: 50 + 400 > 150 so it moves to page 2), then spans 2.67 pages
    expect(paginate([50, 400, 30], 150)).toEqual([0, 100, 0])
    expect(paginate([400, 30], 150)).toEqual([0, 0]) // 400 % 150 = 100 used, 30 fits
    expect(paginate([400, 60], 150)).toEqual([0, 50]) // 100 used, 60 doesn't fit
  })

  it('does nothing when there is no room for a body', () => {
    expect(paginate([10, 10], 0)).toEqual([0, 0])
    expect(paginate([], 100)).toEqual([])
  })
})
