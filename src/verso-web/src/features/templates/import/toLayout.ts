import {
  contentWidthMm,
  DEFAULT_PAGE_SETTINGS,
  FILTERS,
  LAYOUT_SCHEMA_VERSION,
  MAX_ELEMENTS,
  MAX_REGION_HEIGHT_MM,
  MAX_TEXT_CHARS,
  type Filter,
  type FixedRegion,
  type FlowBlock,
  type Layout,
  type PageSettings,
  type PositionedElement,
  type TableColumn,
  type TextStyle,
} from '../model/layout'
import type { ParaStyle, ParsedBlock, ParsedDoc } from './types'

export interface ImportedDesign {
  layout: Layout
  pageSettings: PageSettings
  /** What the author should know: approximations, things left out, what to do next. */
  report: string[]
  summary: { text: number; tables: number; images: number; header: number; footer: number }
}

const PT_MM = 0.3528
const BLANK_LINE_MM = 4
const TOKEN = /^\{\{\s*([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\s*(?:\|\s*([a-z]+)\s*)?\}\}$/
const NUMERIC = /^[€$£]?\s*-?\(?[\d.,\s]+\)?\s*%?$/

const clamp = (v: number, min: number, max: number) => Math.min(max, Math.max(min, v))
const round1 = (v: number) => Math.round(v * 10) / 10

function fontFamily(name?: string): TextStyle['font'] | undefined {
  if (!name) return undefined
  if (/mono|courier|consolas|lucida console/i.test(name)) return 'mono'
  if (/serif|times|georgia|cambria|garamond|palatino|book antiqua/i.test(name) && !/sans/i.test(name)) return 'serif'
  return undefined
}

/** Verso's text style from a file's: only what differs from the defaults is written. */
function textStyle(s: ParaStyle, withAlign = true): TextStyle | undefined {
  const out: TextStyle = {}
  if (s.fontSizePt && Math.abs(s.fontSizePt - 10) > 0.01) out.fontSizePt = clamp(Math.round(s.fontSizePt * 2) / 2, 5, 48)
  if (s.bold) out.bold = true
  if (s.italic) out.italic = true
  if (withAlign && (s.align === 'center' || s.align === 'right')) out.align = s.align
  if (s.color && s.color !== '#000000') out.color = s.color
  const font = fontFamily(s.fontName)
  if (font) out.font = font
  return Object.keys(out).length > 0 ? out : undefined
}

function cleanText(text: string): string {
  return (
    text
      // eslint-disable-next-line no-control-regex
      .replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/g, '')
      .replace(/\r\n?/g, '\n')
      .split('\n')
      .map((l) => l.replace(/[ \t ]+$/g, '').replace(/^[ \t]+/, (m) => (m.length > 8 ? '' : m)))
      .join('\n')
      .trim()
  )
}

/** A word-processor table's heading as a data field name: "Unit price" becomes `unitPrice`. */
function fieldName(header: string, index: number): string {
  const words = header.replace(/[^A-Za-z0-9]+/g, ' ').trim().split(' ').filter(Boolean)
  const name = words.map((w, i) => (i === 0 ? w.charAt(0).toLowerCase() + w.slice(1) : w.charAt(0).toUpperCase() + w.slice(1))).join('')
  return /^[A-Za-z_]/.test(name) ? name.slice(0, 60) : `col${index + 1}`
}

type Unit =
  | { kind: 'text'; text: string; style?: TextStyle; spaceBefore: number }
  | { kind: 'image'; src: string; widthMm?: number; heightMm?: number; spaceBefore: number }
  | { kind: 'table'; columns: TableColumn[]; style?: TextStyle; spaceBefore: number; sourceRows: number }

/** Turns blocks into layout units: neighbouring paragraphs in one style are merged, blank paragraphs become spacing. */
function toUnits(blocks: ParsedBlock[], report: Set<string>, allowTables: boolean): Unit[] {
  const units: Unit[] = []
  let pending = 0
  let lastText: Extract<Unit, { kind: 'text' }> | null = null
  let lastStyle = ''

  const addText = (raw: string, style: ParaStyle) => {
    const text = cleanText(raw)
    if (!text) {
      pending = Math.min(60, pending + BLANK_LINE_MM)
      lastText = null
      return
    }
    const ts = textStyle(style)
    const key = JSON.stringify(ts ?? {})
    if (lastText && pending === 0 && key === lastStyle && lastText.text.length + text.length < MAX_TEXT_CHARS - 10) {
      lastText.text += `\n${text}`
      return
    }
    const unit: Extract<Unit, { kind: 'text' }> = { kind: 'text', text: text.slice(0, MAX_TEXT_CHARS), style: ts, spaceBefore: pending }
    units.push(unit)
    lastText = unit
    lastStyle = key
    pending = 0
  }

  for (const block of blocks) {
    if (block.kind === 'para') {
      addText(block.text, block.style)
    } else if (block.kind === 'image') {
      units.push({ kind: 'image', src: block.src, widthMm: block.widthMm, heightMm: block.heightMm, spaceBefore: pending })
      pending = 0
      lastText = null
    } else {
      const rows = block.rows.filter((r) => r.some((c) => c.trim()))
      const cols = Math.max(0, ...rows.map((r) => r.length))
      if (!allowTables || rows.length < 2 || cols < 2) {
        // A one-row or one-column table is page layout (a letterhead, an address block), not a list of records.
        report.add('A table used only for page layout (one row or one column) was turned into text.')
        for (const row of rows) for (const cell of row) addText(cell, block.style)
        continue
      }
      const used = new Set<string>()
      const dataRow = rows[1]!
      const columns: TableColumn[] = rows[0]!.map((raw, j) => {
        const header = raw.replace(/\s+/g, ' ').trim().slice(0, 100)
        const token = TOKEN.exec((dataRow[j] ?? '').trim())
        let binding = token ? token[1]!.split('.').pop()! : fieldName(header, j)
        for (let n = 2; used.has(binding); n++) binding = `${binding.replace(/\d+$/, '')}${n}`
        used.add(binding)
        const col: TableColumn = { header, binding }
        if (token?.[2] && (FILTERS as readonly string[]).includes(token[2])) col.filter = token[2] as Filter
        if (NUMERIC.test((dataRow[j] ?? '').trim()) && !token) col.align = 'right'
        return col
      })
      if (block.widthsPct && block.widthsPct.length === columns.length) block.widthsPct.forEach((w, j) => (columns[j]!.widthPct = Math.max(1, Math.floor(w))))
      if (rows.length > 2) report.add('Rows of a table below its first data row were dropped: a Verso table repeats one row for every record in the data.')
      units.push({ kind: 'table', columns, style: textStyle(block.style, false), spaceBefore: pending, sourceRows: rows.length })
      pending = 0
      lastText = null
    }
  }
  return units
}

function estimateHeightMm(text: string, style: TextStyle | undefined, widthMm: number): number {
  const pt = style?.fontSizePt ?? 10
  const perLine = Math.max(10, widthMm / (pt * PT_MM * 0.52))
  const lines = text.split('\n').reduce((n, line) => n + Math.max(1, Math.ceil(line.length / perLine)), 0)
  return lines * pt * PT_MM * 1.3 + 1
}

function band(units: Unit[], widthMm: number, idPrefix: string, ids: Ids, report: Set<string>): FixedRegion | undefined {
  const elements: PositionedElement[] = []
  let y = 0
  for (const unit of units) {
    if (unit.kind === 'table') continue
    y += unit.spaceBefore
    if (unit.kind === 'text') {
      const heightMm = round1(estimateHeightMm(unit.text, unit.style, widthMm))
      elements.push({ id: ids.next(idPrefix), type: 'text', xMm: 0, yMm: round1(y), widthMm: round1(widthMm), heightMm, props: { text: unit.text, ...(unit.style ? { style: unit.style } : {}) } })
      y += heightMm
    } else {
      const w = clamp(unit.widthMm ?? 30, 1, widthMm)
      const h = clamp(unit.heightMm ?? (unit.widthMm ? w * 0.6 : 18), 1, 60)
      elements.push({ id: ids.next(idPrefix), type: 'image', xMm: 0, yMm: round1(y), widthMm: round1(w), heightMm: round1(h), props: { src: unit.src, alt: 'Imported image', fit: 'contain' } })
      y += h + 1
    }
  }
  if (elements.length === 0) return undefined
  if (y > MAX_REGION_HEIGHT_MM) report.add(`The ${idPrefix === 'h' ? 'header' : 'footer'} is taller than the 150 mm limit, so its lower part will be cut off.`)
  return { heightMm: round1(clamp(y + 1, 5, MAX_REGION_HEIGHT_MM)), elements }
}

class Ids {
  private n = new Map<string, number>()
  next(prefix: string) {
    const v = (this.n.get(prefix) ?? 0) + 1
    this.n.set(prefix, v)
    return `${prefix}${v}`
  }
}

/** Builds a Verso layout (header, body, footer, page margins) from a parsed document. */
export function buildDesign(doc: ParsedDoc): ImportedDesign {
  const report = new Set(doc.report)

  const pageSettings: PageSettings = structuredClone(DEFAULT_PAGE_SETTINGS)
  if (doc.page.landscape) pageSettings.orientation = 'landscape'
  if (doc.page.marginsMm) {
    const m = doc.page.marginsMm
    const margins = { top: round1(clamp(m.top, 0, 50)), right: round1(clamp(m.right, 0, 50)), bottom: round1(clamp(m.bottom, 0, 50)), left: round1(clamp(m.left, 0, 50)) }
    pageSettings.marginsMm = margins
    report.add('Page margins and orientation were taken from the file.')
  }
  if (doc.page.sizeMm) {
    const { width, height } = doc.page.sizeMm
    const [a, b] = [Math.min(width, height), Math.max(width, height)]
    if (Math.abs(a - 210) > 3 || Math.abs(b - 297) > 3) report.add(`The document’s page size (${Math.round(a)} × ${Math.round(b)} mm) is not A4; Verso prints on A4.`)
  }

  const width = contentWidthMm(pageSettings)
  const ids = new Ids()
  const headerUnits = toUnits(doc.header, report, false)
  const footerUnits = toUnits(doc.footer, report, false)
  const bodyUnits = toUnits(doc.body, report, true)
  if (doc.header.some((b) => b.kind === 'table') || doc.footer.some((b) => b.kind === 'table')) report.add('Tables in a header or footer were turned into text.')

  const header = band(headerUnits, width, 'h', ids, report)
  const footer = band(footerUnits, width, 'f', ids, report)

  const blocks: FlowBlock[] = []
  let tableCount = 0
  let tables = 0
  let images = 0
  let text = 0
  const budget = MAX_ELEMENTS - (header?.elements.length ?? 0) - (footer?.elements.length ?? 0)
  for (const unit of bodyUnits) {
    if (blocks.length >= budget) {
      report.add(`The document is longer than Verso allows (${MAX_ELEMENTS} elements); the rest was left out.`)
      break
    }
    const space = unit.spaceBefore > 0 ? { spaceBeforeMm: round1(clamp(unit.spaceBefore, 0, 100)) } : {}
    if (unit.kind === 'text') {
      text++
      blocks.push({ id: ids.next('p'), ...space, type: 'text', props: { text: unit.text, ...(unit.style ? { style: unit.style } : {}) } })
    } else if (unit.kind === 'image') {
      images++
      blocks.push({ id: ids.next('img'), ...space, type: 'image', props: { src: unit.src, alt: 'Imported image', fit: 'contain' } })
    } else {
      tables++
      tableCount++
      blocks.push({
        id: ids.next('table'),
        ...space,
        type: 'table',
        props: { binding: tableCount === 1 ? 'items' : `items${tableCount}`, columns: unit.columns, ...(unit.style ? { style: unit.style } : {}) },
      })
    }
  }
  if (tables > 0) report.add('Each table repeats over a list: choose the list and map every column to a field in Data model before publishing (the names shown are guesses from the headings).')
  if (blocks.length === 0 && !header && !footer) report.add('No text, images or tables were found in the document.')

  const layout: Layout = { layoutSchemaVersion: LAYOUT_SCHEMA_VERSION }
  if (header) layout.header = header
  if (blocks.length > 0) layout.body = { blocks }
  if (footer) layout.footer = footer
  report.add('The import is editable and approximate, not a copy of how Word or the RTF viewer draws the page: fonts, exact positions, floating objects, columns and numbering are not kept.')
  return { layout, pageSettings, report: [...report], summary: { text, tables, images, header: header?.elements.length ?? 0, footer: footer?.elements.length ?? 0 } }
}
