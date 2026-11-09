/**
 * Pure layout edits. Every function returns a new layout and leaves the input untouched, so the
 * designer can keep old layouts for undo. Untouched parts keep their identity and exact content,
 * which is what makes save → reload round-trip exactly.
 */
import {
  ALLOWED_TYPES,
  contentWidthMm,
  MAX_COORDINATE_MM,
  MAX_REGION_HEIGHT_MM,
  type ElementContent,
  type ElementType,
  type FlowBlock,
  type Layout,
  type LayoutElement,
  type PageSettings,
  type PositionedElement,
  type Region,
} from './layout'

export interface Located {
  region: Region
  index: number
  element: LayoutElement
}

export function regionItems(layout: Layout, region: Region): LayoutElement[] {
  if (region === 'body') return layout.body?.blocks ?? []
  return layout[region]?.elements ?? []
}

export function allElements(layout: Layout): Located[] {
  const result: Located[] = []
  for (const region of ['header', 'body', 'footer'] as Region[]) {
    regionItems(layout, region).forEach((element, index) => result.push({ region, index, element }))
  }
  return result
}

export function findElement(layout: Layout, id: string): Located | undefined {
  return allElements(layout).find((l) => l.element.id === id)
}

function withRegionItems(layout: Layout, region: Region, items: LayoutElement[]): Layout {
  if (region === 'body') return { ...layout, body: { ...(layout.body ?? {}), blocks: items as FlowBlock[] } }
  const existing = layout[region]
  return { ...layout, [region]: { heightMm: existing?.heightMm ?? defaultBandHeight(region), ...existing, elements: items as PositionedElement[] } }
}

export function defaultBandHeight(region: Region): number {
  return region === 'header' ? 40 : 12
}

const ID_PATTERN = /^[A-Za-z0-9_-]{1,64}$/

export function isValidId(id: string): boolean {
  return ID_PATTERN.test(id)
}

export function uniqueId(layout: Layout, base: string): string {
  const taken = new Set(allElements(layout).map((l) => l.element.id))
  const stem = base.replace(/[^A-Za-z0-9_-]/g, '-').slice(0, 56) || 'element'
  if (!taken.has(stem)) return stem
  for (let n = 2; ; n++) {
    const candidate = `${stem}-${n}`
    if (!taken.has(candidate)) return candidate
  }
}

/** Default content for a new element of a type. */
export function defaultContent(type: ElementType, binding?: string): ElementContent {
  switch (type) {
    case 'text':
      return { type, props: { text: 'Text' } }
    case 'field':
      return { type, props: { binding: binding ?? '' } }
    case 'table':
      return { type, props: { binding: binding ?? '', columns: [{ header: 'Column', binding: '' }] } }
    case 'divider':
      return { type, props: {} }
    case 'box':
      return { type, props: {} }
    case 'image':
      return { type, props: { src: '' } }
    case 'signature':
      return { type, props: {} }
  }
}

function defaultSize(type: ElementType, page: PageSettings): { widthMm: number; heightMm: number } {
  const full = contentWidthMm(page)
  switch (type) {
    case 'text':
      return { widthMm: 60, heightMm: 10 }
    case 'field':
      return { widthMm: 60, heightMm: 6 }
    case 'divider':
      return { widthMm: full, heightMm: 1 }
    case 'box':
      return { widthMm: 60, heightMm: 24 }
    case 'image':
      return { widthMm: 24, heightMm: 16 }
    case 'signature':
      return { widthMm: 60, heightMm: 16 }
    case 'table':
      return { widthMm: full, heightMm: 20 }
  }
}

export interface AddOptions {
  /** Top-left position in mm for header/footer elements. */
  at?: { xMm: number; yMm: number }
  /** Insertion index for body blocks (default: end). */
  index?: number
  binding?: string
  /** Label for a new data field (e.g. derived from the bound field name). */
  label?: string
}

export function canPlace(type: ElementType, region: Region): boolean {
  return ALLOWED_TYPES[region].includes(type)
}

/** Adds a new element and returns the new layout plus the new id. */
export function addElement(
  layout: Layout,
  page: PageSettings,
  region: Region,
  type: ElementType,
  options: AddOptions = {},
): { layout: Layout; id: string } {
  if (!canPlace(type, region)) throw new Error(`'${type}' isn't allowed in the ${region}.`)

  const base = options.binding ? options.binding.split('.').pop()! : type
  const id = uniqueId(layout, base)
  const content = defaultContent(type, options.binding)
  if (content.type === 'field' && options.label) content.props.label = options.label

  const items = regionItems(layout, region)
  if (region === 'body') {
    const block = { id, ...content } as FlowBlock
    const index = Math.max(0, Math.min(options.index ?? items.length, items.length))
    const next = [...items.slice(0, index), block, ...items.slice(index)]
    return { layout: withRegionItems(layout, region, next), id }
  }

  const bandHeight = layout[region]?.heightMm ?? defaultBandHeight(region)
  const size = defaultSize(type, page)
  const heightMm = Math.min(size.heightMm, bandHeight)
  const widthMm = Math.min(size.widthMm, contentWidthMm(page))
  // Without a drop position, stack below the lowest element so new elements don't cover existing ones.
  const below = items.reduce((max, e) => Math.max(max, (e as PositionedElement).yMm + (e as PositionedElement).heightMm), 0)
  const defaultY = items.length > 0 && below + 1 + heightMm <= bandHeight ? below + 1 : 0
  const xMm = round(clamp(options.at?.xMm ?? 0, 0, Math.max(0, contentWidthMm(page) - widthMm)))
  const yMm = round(clamp(options.at?.yMm ?? defaultY, 0, Math.max(0, bandHeight - heightMm)))
  const element = { id, xMm, yMm, widthMm, heightMm, ...content } as PositionedElement
  return { layout: withRegionItems(layout, region, [...items, element]), id }
}

function replaceElement(layout: Layout, id: string, map: (element: LayoutElement) => LayoutElement): Layout {
  const found = findElement(layout, id)
  if (!found) return layout
  const items = regionItems(layout, found.region)
  const next = items.map((e, i) => (i === found.index ? map(e) : e))
  return withRegionItems(layout, found.region, next)
}

/** Merges props into an element. `undefined` values remove the property (so optional props stay absent). */
export function updateProps(layout: Layout, id: string, patch: Record<string, unknown>): Layout {
  return replaceElement(layout, id, (e) => {
    const props: Record<string, unknown> = { ...(e.props as Record<string, unknown>) }
    for (const [k, v] of Object.entries(patch)) {
      if (v === undefined) delete props[k]
      else props[k] = v
    }
    return { ...e, props } as LayoutElement
  })
}

/** Merges style properties into an element's props.style; removes the style object when it becomes empty. */
export function updateStyle(layout: Layout, id: string, patch: Record<string, unknown>): Layout {
  const found = findElement(layout, id)
  if (!found) return layout
  const style: Record<string, unknown> = { ...((found.element.props as { style?: object }).style ?? {}) }
  for (const [k, v] of Object.entries(patch)) {
    if (v === undefined) delete style[k]
    else style[k] = v
  }
  return updateProps(layout, id, { style: Object.keys(style).length ? style : undefined })
}

/** Updates placement fields (xMm, yMm, widthMm, heightMm, spaceBeforeMm). `undefined` removes the field. */
export function updatePlacement(layout: Layout, id: string, patch: Partial<Record<'xMm' | 'yMm' | 'widthMm' | 'heightMm' | 'spaceBeforeMm', number | undefined>>): Layout {
  return replaceElement(layout, id, (e) => {
    const next: Record<string, unknown> = { ...e }
    for (const [k, v] of Object.entries(patch)) {
      if (v === undefined) delete next[k]
      else next[k] = round(v)
    }
    return next as unknown as LayoutElement
  })
}

/** Moves a header/footer element by a delta, keeping it inside its band and the content width. */
export function nudge(layout: Layout, page: PageSettings, id: string, dxMm: number, dyMm: number): Layout {
  const found = findElement(layout, id)
  if (!found || found.region === 'body') return layout
  const e = found.element as PositionedElement
  const band = layout[found.region]!.heightMm
  const xMm = clamp(e.xMm + dxMm, 0, Math.max(0, contentWidthMm(page) - e.widthMm))
  const yMm = clamp(e.yMm + dyMm, 0, Math.max(0, band - e.heightMm))
  return updatePlacement(layout, id, { xMm, yMm })
}

export function resize(layout: Layout, id: string, widthMm: number, heightMm: number): Layout {
  return updatePlacement(layout, id, {
    widthMm: clamp(widthMm, 1, MAX_COORDINATE_MM),
    heightMm: clamp(heightMm, 0.5, MAX_COORDINATE_MM),
  })
}

export function renameElement(layout: Layout, id: string, newId: string): Layout {
  if (id === newId) return layout
  return replaceElement(layout, id, (e) => ({ ...e, id: newId }))
}

export function removeElement(layout: Layout, id: string): Layout {
  const found = findElement(layout, id)
  if (!found) return layout
  return withRegionItems(layout, found.region, regionItems(layout, found.region).filter((_, i) => i !== found.index))
}

export function duplicateElement(layout: Layout, id: string): { layout: Layout; id: string } {
  const found = findElement(layout, id)
  if (!found) return { layout, id }
  const newId = uniqueId(layout, `${id}-copy`)
  const copy = structuredClone(found.element) as LayoutElement
  copy.id = newId
  if (found.region !== 'body') {
    const p = copy as PositionedElement
    const band = layout[found.region]!.heightMm
    p.yMm = round(clamp(p.yMm + 4, 0, Math.max(0, band - p.heightMm)))
    p.xMm = round(p.xMm + 4)
  }
  const items = regionItems(layout, found.region)
  const next = [...items.slice(0, found.index + 1), copy, ...items.slice(found.index + 1)]
  return { layout: withRegionItems(layout, found.region, next), id: newId }
}

/** Moves a body block to a new index (0-based, among the blocks after removal). */
export function moveBlock(layout: Layout, id: string, toIndex: number): Layout {
  const found = findElement(layout, id)
  if (!found || found.region !== 'body') return layout
  const items = [...regionItems(layout, 'body')]
  const [block] = items.splice(found.index, 1)
  const index = clamp(toIndex, 0, items.length)
  items.splice(index, 0, block!)
  return withRegionItems(layout, 'body', items)
}

/** Brings a header/footer element forward or back in drawing order (later = on top). */
export function reorderInRegion(layout: Layout, id: string, delta: number): Layout {
  const found = findElement(layout, id)
  if (!found) return layout
  if (found.region === 'body') return moveBlock(layout, id, found.index + delta)
  const items = [...regionItems(layout, found.region)]
  const target = clamp(found.index + delta, 0, items.length - 1)
  const [el] = items.splice(found.index, 1)
  items.splice(target, 0, el!)
  return withRegionItems(layout, found.region, items)
}

export function setBandHeight(layout: Layout, region: 'header' | 'footer', heightMm: number): Layout {
  const existing = layout[region]
  return { ...layout, [region]: { ...(existing ?? { elements: [] }), heightMm: round(clamp(heightMm, 0, MAX_REGION_HEIGHT_MM)) } }
}

export function removeBand(layout: Layout, region: 'header' | 'footer'): Layout {
  const next = { ...layout }
  delete next[region]
  return next
}

export function addBand(layout: Layout, region: 'header' | 'footer'): Layout {
  if (layout[region]) return layout
  return { ...layout, [region]: { heightMm: defaultBandHeight(region), elements: [] } }
}

export function clamp(v: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, v))
}

/** Rounds to 0.1 mm so dragging doesn't store long fractions. */
export function round(v: number): number {
  return Math.round(v * 10) / 10
}
