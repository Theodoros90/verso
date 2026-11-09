/**
 * Layout model v1 as TypeScript types. Mirrors docs/layout-model.md and Verso.Rendering.Layout.LayoutParser,
 * which stay the source of truth (the server validates every save).
 */

export const LAYOUT_SCHEMA_VERSION = 1
export const MAX_REGION_HEIGHT_MM = 150
export const MAX_COORDINATE_MM = 420
export const MAX_ELEMENTS = 500
export const MAX_TEXT_CHARS = 10_000
export const MAX_IMAGE_DATA_URI_CHARS = 700_000

export const FILTERS = ['date', 'money', 'number', 'upper', 'lower', 'year', 'month', 'day'] as const
export type Filter = (typeof FILTERS)[number]

export type Align = 'left' | 'center' | 'right'
export type FontFamily = 'sans' | 'serif' | 'mono'

export interface TextStyle {
  fontSizePt?: number
  bold?: boolean
  italic?: boolean
  align?: Align
  color?: string
  font?: FontFamily
}

export interface TableColumn {
  header: string
  binding: string
  filter?: Filter
  align?: Align
  widthPct?: number
}

export type ElementContent =
  | { type: 'text'; props: { text: string; style?: TextStyle } }
  | { type: 'field'; props: { binding: string; filter?: Filter; label?: string; style?: TextStyle } }
  | { type: 'table'; props: { binding: string; columns: TableColumn[]; emptyText?: string; style?: TextStyle } }
  | { type: 'divider'; props: { thicknessPt?: number; color?: string } }
  | { type: 'box'; props: { borderColor?: string; fill?: string; radiusMm?: number } }
  | { type: 'image'; props: { src: string; alt?: string; fit?: 'contain' | 'cover' } }
  | { type: 'signature'; props: { label?: string } }

export type ElementType = ElementContent['type']

export type PositionedElement = { id: string; xMm: number; yMm: number; widthMm: number; heightMm: number } & ElementContent
export type FlowBlock = { id: string; spaceBeforeMm?: number } & ElementContent
export type LayoutElement = PositionedElement | FlowBlock

export interface FixedRegion {
  heightMm: number
  elements: PositionedElement[]
}

export interface FlowRegion {
  blocks: FlowBlock[]
}

export interface Layout {
  layoutSchemaVersion: number
  header?: FixedRegion
  body?: FlowRegion
  footer?: FixedRegion
}

export type Region = 'header' | 'body' | 'footer'
export const REGIONS: Region[] = ['header', 'body', 'footer']

export const REGION_LABELS: Record<Region, string> = { header: 'Header', body: 'Body', footer: 'Footer' }

/** Which element types each region accepts (layout-model.md, "Element types"). */
export const ALLOWED_TYPES: Record<Region, ElementType[]> = {
  header: ['text', 'field', 'divider', 'box', 'image', 'signature'],
  body: ['text', 'field', 'table', 'divider', 'image', 'signature'],
  footer: ['text', 'field', 'divider', 'box', 'image', 'signature'],
}

export const TYPE_LABELS: Record<ElementType, string> = {
  text: 'Text',
  field: 'Data field',
  table: 'Repeating table',
  image: 'Image',
  divider: 'Divider',
  box: 'Box',
  signature: 'Signature',
}

export interface PageSettings {
  size: string
  orientation: 'portrait' | 'landscape'
  marginsMm: { top: number; right: number; bottom: number; left: number }
}

export const DEFAULT_PAGE_SETTINGS: PageSettings = {
  size: 'A4',
  orientation: 'portrait',
  marginsMm: { top: 18, right: 16, bottom: 18, left: 16 },
}

export function pageSizeMm(page: PageSettings): { width: number; height: number } {
  return page.orientation === 'landscape' ? { width: 297, height: 210 } : { width: 210, height: 297 }
}

export function contentWidthMm(page: PageSettings): number {
  return pageSizeMm(page).width - page.marginsMm.left - page.marginsMm.right
}

export function emptyLayout(): Layout {
  return { layoutSchemaVersion: LAYOUT_SCHEMA_VERSION }
}

export function isPositioned(element: LayoutElement): element is PositionedElement {
  return 'xMm' in element
}

/** Reads a layout from the API. Anything unexpected is kept as is; the server is the validator. */
export function asLayout(value: unknown): Layout {
  if (value && typeof value === 'object' && !Array.isArray(value)) return value as Layout
  return emptyLayout()
}

export function asPageSettings(value: unknown): PageSettings {
  const v = value as Partial<PageSettings> | null | undefined
  if (!v || !v.marginsMm) return structuredClone(DEFAULT_PAGE_SETTINGS)
  return {
    size: v.size ?? 'A4',
    orientation: v.orientation === 'landscape' ? 'landscape' : 'portrait',
    marginsMm: { ...v.marginsMm },
  }
}
