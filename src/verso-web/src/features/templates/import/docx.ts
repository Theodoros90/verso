import { ImportError, MAX_IMAGE_BYTES, MAX_PARAGRAPHS, sniffImage, toDataUri, type ParaStyle, type ParsedBlock, type ParsedDoc } from './types'
import { readZip, type Zip } from './zip'

const W = 'http://schemas.openxmlformats.org/wordprocessingml/2006/main'
const R = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'
const A = 'http://schemas.openxmlformats.org/drawingml/2006/main'
const WP = 'http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing'

const MAX_XML_NODES = 200_000
const EMU_PER_MM = 36_000
const TWIPS_PER_MM = 56.6929

function parseXml(bytes: Uint8Array | null): Document | null {
  if (!bytes) return null
  const text = new TextDecoder('utf-8').decode(bytes)
  // Document type declarations can define entities that expand into huge text; Word never writes them.
  if (/<!DOCTYPE|<!ENTITY/i.test(text)) throw new ImportError('The document contains a document type declaration, which is not allowed.')
  const doc = new DOMParser().parseFromString(text, 'application/xml')
  if (doc.getElementsByTagName('parsererror').length > 0) throw new ImportError('The document’s XML could not be read.')
  if (doc.getElementsByTagName('*').length > MAX_XML_NODES) throw new ImportError('The document is too complex to import.')
  return doc
}

const kids = (el: Element | null | undefined, name: string): Element[] =>
  el ? [...el.children].filter((c) => c.localName === name && c.namespaceURI === W) : []
const first = (el: Element | null | undefined, name: string): Element | null => kids(el, name)[0] ?? null
const attr = (el: Element | null | undefined, name: string): string | null => el?.getAttributeNS(W, name) ?? el?.getAttribute(`w:${name}`) ?? null
/** A Word on/off property: present means on unless `val` is 0/false/off. */
const flag = (el: Element | null | undefined): boolean | undefined => {
  if (!el) return undefined
  const v = attr(el, 'val')
  return !(v === '0' || v === 'false' || v === 'off')
}

interface StyleDef extends ParaStyle {
  basedOn?: string
}

function runProps(rPr: Element | null | undefined): ParaStyle {
  const s: ParaStyle = {}
  if (!rPr) return s
  const b = flag(first(rPr, 'b'))
  if (b !== undefined) s.bold = b
  const i = flag(first(rPr, 'i'))
  if (i !== undefined) s.italic = i
  const sz = Number(attr(first(rPr, 'sz'), 'val'))
  if (sz > 0) s.fontSizePt = sz / 2
  const color = attr(first(rPr, 'color'), 'val')
  if (color && /^[0-9a-f]{6}$/i.test(color)) s.color = `#${color.toUpperCase()}`
  const fonts = first(rPr, 'rFonts')
  const font = attr(fonts, 'ascii') ?? attr(fonts, 'hAnsi')
  if (font) s.fontName = font
  return s
}

const alignOf = (jc: string | null): ParaStyle['align'] | undefined =>
  jc === 'center' ? 'center' : jc === 'right' || jc === 'end' ? 'right' : jc === 'left' || jc === 'start' || jc === 'both' || jc === 'distribute' ? 'left' : undefined

interface Context {
  zip: Zip
  rels: Map<string, string>
  styles: Map<string, StyleDef>
  defaults: ParaStyle
  report: Set<string>
  paragraphs: number
}

function resolveStyle(ctx: Context, id: string | null, depth = 0): ParaStyle {
  const def = id ? ctx.styles.get(id) : undefined
  if (!def) return {}
  const base = def.basedOn && depth < 6 ? resolveStyle(ctx, def.basedOn, depth + 1) : {}
  const { basedOn: _basedOn, ...own } = def
  return { ...base, ...own }
}

async function loadContext(zip: Zip, relsPath: string, loadStyles: boolean): Promise<Context> {
  const rels = new Map<string, string>()
  const relDoc = parseXml(await zip.read(relsPath))
  for (const r of relDoc?.getElementsByTagName('Relationship') ?? []) {
    if (r.getAttribute('TargetMode') !== 'External') rels.set(r.getAttribute('Id') ?? '', r.getAttribute('Target') ?? '')
  }
  const styles = new Map<string, StyleDef>()
  let defaults: ParaStyle = {}
  if (loadStyles) {
    const doc = parseXml(await zip.read('word/styles.xml'))
    const root = doc?.documentElement
    defaults = runProps(first(first(first(root, 'docDefaults'), 'rPrDefault'), 'rPr'))
    for (const st of kids(root, 'style')) {
      const id = attr(st, 'styleId')
      if (!id) continue
      const pPr = first(st, 'pPr')
      const def: StyleDef = { ...runProps(first(st, 'rPr')), basedOn: attr(first(st, 'basedOn'), 'val') ?? undefined }
      const align = alignOf(attr(first(pPr, 'jc'), 'val'))
      if (align) def.align = align
      styles.set(id, def)
    }
  }
  return { zip, rels, styles, defaults, report: new Set(), paragraphs: 0 }
}

/** Reads a DOCX package into blocks. Approximate by design: see the report for what is simplified or left out. */
export async function parseDocx(buffer: ArrayBuffer): Promise<ParsedDoc> {
  const zip = readZip(buffer)
  const main = parseXml(await zip.read('word/document.xml'))
  const body = first(main?.documentElement, 'body')
  if (!main || !body) throw new ImportError('The package has no Word document.')
  const ctx = await loadContext(zip, 'word/_rels/document.xml.rels', true)

  const blocks = await readBlocks(ctx, body, true)
  const doc: ParsedDoc = { body: blocks, header: [], footer: [], page: {}, report: [] }

  const sect = last(kids(body, 'sectPr'))
  if (sect) {
    const size = first(sect, 'pgSz')
    const w = Number(attr(size, 'w'))
    const h = Number(attr(size, 'h'))
    if (w > 0 && h > 0) {
      doc.page.sizeMm = { width: w / TWIPS_PER_MM, height: h / TWIPS_PER_MM }
      doc.page.landscape = w > h
    }
    const mar = first(sect, 'pgMar')
    if (mar) {
      const m = (n: string) => Math.max(0, Number(attr(mar, n)) / TWIPS_PER_MM) || 0
      doc.page.marginsMm = { top: m('top'), right: m('right'), bottom: m('bottom'), left: m('left') }
    }
    for (const [kind, target] of [['headerReference', 'header'], ['footerReference', 'footer']] as const) {
      const refs = kids(sect, kind)
      const ref = refs.find((r) => attr(r, 'type') === 'default') ?? refs[0]
      const part = ref ? ctx.rels.get(ref.getAttributeNS(R, 'id') ?? ref.getAttribute('r:id') ?? '') : undefined
      if (!part || part.includes('..')) continue
      const partDoc = parseXml(await zip.read(`word/${part}`))
      if (!partDoc) continue
      const partCtx = { ...(await loadContext(zip, `word/_rels/${part}.rels`, false)), styles: ctx.styles, defaults: ctx.defaults, report: ctx.report }
      doc[target] = await readBlocks(partCtx, partDoc.documentElement, false)
    }
  }
  doc.report = [...ctx.report]
  return doc
}

const last = <T>(list: T[]): T | undefined => list[list.length - 1]

/** Paragraphs, tables and drawings under `container`, in order. Content controls and similar wrappers are looked through. */
async function readBlocks(ctx: Context, container: Element, isBody: boolean): Promise<ParsedBlock[]> {
  const out: ParsedBlock[] = []
  for (const el of container.children) {
    if (el.namespaceURI !== W) continue
    if (el.localName === 'p') {
      if (++ctx.paragraphs > MAX_PARAGRAPHS) throw new ImportError('The document is too long to import.')
      out.push(...(await readParagraph(ctx, el)))
    } else if (el.localName === 'tbl') {
      out.push(readTable(ctx, el))
    } else if (el.localName === 'sdt') {
      const content = first(el, 'sdtContent')
      if (content) out.push(...(await readBlocks(ctx, content, isBody)))
    }
  }
  return out
}

async function readParagraph(ctx: Context, p: Element): Promise<ParsedBlock[]> {
  const pPr = first(p, 'pPr')
  const base: ParaStyle = { ...ctx.defaults, ...resolveStyle(ctx, attr(first(pPr, 'pStyle'), 'val')) }
  const align = alignOf(attr(first(pPr, 'jc'), 'val'))
  if (align) base.align = align

  const runs: { text: string; style: ParaStyle }[] = []
  const images: ParsedBlock[] = []
  const walk = async (node: Element) => {
    for (const el of node.children) {
      if (el.namespaceURI !== W) continue
      switch (el.localName) {
        case 'r': {
          const style = { ...base, ...runProps(first(el, 'rPr')) }
          let text = ''
          for (const c of el.children) {
            if (c.namespaceURI !== W) continue
            if (c.localName === 't') text += c.textContent ?? ''
            else if (c.localName === 'tab') text += ' '
            else if (c.localName === 'br' || c.localName === 'cr') text += '\n'
            else if (c.localName === 'noBreakHyphen') text += '-'
            else if (c.localName === 'drawing') {
              const image = await readImage(ctx, c)
              if (image) images.push(image)
            } else if (c.localName === 'pict' || c.localName === 'object') ctx.report.add('A legacy drawing or embedded object was left out.')
          }
          if (text) runs.push({ text, style })
          break
        }
        case 'hyperlink':
        case 'smartTag':
        case 'sdt':
        case 'ins':
        case 'fldSimple':
          await walk(first(el, 'sdtContent') ?? el)
          break
      }
    }
  }
  await walk(p)

  const text = runs.map((r) => r.text).join('')
  const lead = runs.find((r) => r.text.trim())
  const style = lead?.style ?? base
  if (runs.some((r) => r.text.trim() && (r.style.bold !== style.bold || r.style.italic !== style.italic || r.style.fontSizePt !== style.fontSizePt || r.style.color !== style.color)))
    ctx.report.add('Mixed formatting inside a paragraph (for example one bold word) is simplified to the paragraph’s first style.')
  const listed = first(pPr, 'numPr') ? `• ${text}` : text
  const out: ParsedBlock[] = [{ kind: 'para', text: listed, style: { ...style } }]
  return [...out, ...images]
}

async function readImage(ctx: Context, drawing: Element): Promise<ParsedBlock | null> {
  const blip = drawing.getElementsByTagNameNS(A, 'blip')[0]
  const rid = blip?.getAttributeNS(R, 'embed') ?? blip?.getAttribute('r:embed')
  const target = rid ? ctx.rels.get(rid) : undefined
  if (!target || target.includes('..') || target.startsWith('/')) {
    ctx.report.add('An externally linked or unreadable image was left out.')
    return null
  }
  const bytes = await ctx.zip.read(`word/${target}`)
  const mime = bytes ? sniffImage(bytes) : null
  if (!bytes || !mime) {
    ctx.report.add('Only PNG and JPEG images can be imported; other pictures (GIF, EMF, WMF…) were left out.')
    return null
  }
  if (bytes.length > MAX_IMAGE_BYTES) {
    ctx.report.add('An image over about 500 KB was left out; add a smaller copy with the Image element.')
    return null
  }
  const extent = drawing.getElementsByTagNameNS(WP, 'extent')[0]
  const cx = Number(extent?.getAttribute('cx'))
  const cy = Number(extent?.getAttribute('cy'))
  return { kind: 'image', src: toDataUri(bytes, mime), widthMm: cx > 0 ? cx / EMU_PER_MM : undefined, heightMm: cy > 0 ? cy / EMU_PER_MM : undefined }
}

function readTable(ctx: Context, tbl: Element): ParsedBlock {
  let merged = false
  const rows = kids(tbl, 'tr').map((tr) =>
    kids(tr, 'tc').map((tc) => {
      const props = first(tc, 'tcPr')
      if (first(props, 'vMerge') || Number(attr(first(props, 'gridSpan'), 'val')) > 1) merged = true
      if (kids(tc, 'tbl').length > 0) ctx.report.add('A table inside a table was flattened to text.')
      return kids(tc, 'p')
        .map((p) => paragraphText(p))
        .join('\n')
        .trim()
    }),
  )
  if (merged) ctx.report.add('Merged table cells were flattened; check the imported tables.')
  const grid = kids(first(tbl, 'tblGrid'), 'gridCol').map((c) => Number(attr(c, 'w')) || 0)
  const total = grid.reduce((a, b) => a + b, 0)
  const firstPara = first(first(first(tbl, 'tr'), 'tc'), 'p')
  const style: ParaStyle = { ...ctx.defaults, ...resolveStyle(ctx, attr(first(first(firstPara, 'pPr'), 'pStyle'), 'val')), ...runProps(first(first(firstPara, 'r'), 'rPr')) }
  return { kind: 'table', rows, widthsPct: total > 0 && grid.length === (rows[0]?.length ?? -1) ? grid.map((g) => (g / total) * 100) : undefined, style }
}

function paragraphText(p: Element): string {
  let text = ''
  const walk = (node: Element) => {
    for (const el of node.children) {
      if (el.namespaceURI !== W) continue
      if (el.localName === 'r') {
        for (const c of el.children) {
          if (c.namespaceURI !== W) continue
          if (c.localName === 't') text += c.textContent ?? ''
          else if (c.localName === 'tab') text += ' '
          else if (c.localName === 'br') text += '\n'
        }
      } else if (['hyperlink', 'smartTag', 'sdt', 'ins', 'fldSimple'].includes(el.localName)) walk(first(el, 'sdtContent') ?? el)
    }
  }
  walk(p)
  return text
}
