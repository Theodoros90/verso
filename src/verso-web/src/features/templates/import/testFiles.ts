/// <reference types="node" />
// Test helpers: build small but real DOCX packages and RTF text, so the importer is tested on actual file formats.
import { crc32, deflateRawSync } from 'node:zlib'

export const PNG_BASE64 = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=='

/** A ZIP file with the given parts, deflated (or stored, when `store` is set). */
export function zip(parts: Record<string, string | Uint8Array>, store = false): ArrayBuffer {
  const chunks: Uint8Array[] = []
  const central: Uint8Array[] = []
  let offset = 0
  const u32 = (n: number) => new Uint8Array([n & 255, (n >> 8) & 255, (n >> 16) & 255, (n >>> 24) & 255])
  const u16 = (n: number) => new Uint8Array([n & 255, (n >> 8) & 255])
  const cat = (...a: Uint8Array[]) => {
    const out = new Uint8Array(a.reduce((n, x) => n + x.length, 0))
    let at = 0
    for (const x of a) {
      out.set(x, at)
      at += x.length
    }
    return out
  }
  for (const [name, content] of Object.entries(parts)) {
    const raw = typeof content === 'string' ? new TextEncoder().encode(content) : content
    const data = store ? raw : new Uint8Array(deflateRawSync(raw))
    const method = store ? 0 : 8
    const nameBytes = new TextEncoder().encode(name)
    const crc = crc32(raw)
    const local = cat(u32(0x04034b50), u16(20), u16(0), u16(method), u16(0), u16(0), u32(crc), u32(data.length), u32(raw.length), u16(nameBytes.length), u16(0), nameBytes, data)
    central.push(cat(u32(0x02014b50), u16(20), u16(20), u16(0), u16(method), u16(0), u16(0), u32(crc), u32(data.length), u32(raw.length), u16(nameBytes.length), u16(0), u16(0), u16(0), u16(0), u32(0), u32(offset), nameBytes))
    chunks.push(local)
    offset += local.length
  }
  const dir = cat(...central)
  const end = cat(u32(0x06054b50), u16(0), u16(0), u16(central.length), u16(central.length), u32(dir.length), u32(offset), u16(0))
  const all = cat(...chunks, dir, end)
  return all.buffer.slice(all.byteOffset, all.byteOffset + all.byteLength) as ArrayBuffer
}

const NS = 'xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"'

export const para = (text: string, opts: { bold?: boolean; size?: number; center?: boolean; style?: string } = {}) =>
  `<w:p><w:pPr>${opts.style ? `<w:pStyle w:val="${opts.style}"/>` : ''}${opts.center ? '<w:jc w:val="center"/>' : ''}</w:pPr><w:r><w:rPr>${opts.bold ? '<w:b/>' : ''}${opts.size ? `<w:sz w:val="${opts.size * 2}"/>` : ''}</w:rPr><w:t xml:space="preserve">${text}</w:t></w:r></w:p>`

export const table = (rows: string[][], widths: number[] = []) =>
  `<w:tbl><w:tblGrid>${widths.map((w) => `<w:gridCol w:w="${w}"/>`).join('')}</w:tblGrid>${rows.map((r) => `<w:tr>${r.map((c) => `<w:tc><w:p><w:r><w:t>${c}</w:t></w:r></w:p></w:tc>`).join('')}</w:tr>`).join('')}</w:tbl>`

export const imagePara = `<w:p><w:r><w:drawing><wp:inline><wp:extent cx="1080000" cy="360000"/><a:graphic><a:graphicData><a:blip r:embed="rId9"/></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>`

export interface DocxOptions {
  body: string
  header?: string
  footer?: string
  /** Page section: size and margins in twips. */
  page?: { w: number; h: number; top: number; right: number; bottom: number; left: number }
  styles?: string
  withImage?: boolean
}

export function docx({ body, header, footer, page, styles, withImage }: DocxOptions): ArrayBuffer {
  const sect = `<w:sectPr>${header ? '<w:headerReference w:type="default" r:id="rId5"/>' : ''}${footer ? '<w:footerReference w:type="default" r:id="rId6"/>' : ''}${
    page ? `<w:pgSz w:w="${page.w}" w:h="${page.h}"/><w:pgMar w:top="${page.top}" w:right="${page.right}" w:bottom="${page.bottom}" w:left="${page.left}"/>` : ''
  }</w:sectPr>`
  const rels = `<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">${header ? '<Relationship Id="rId5" Target="header1.xml" Type="x"/>' : ''}${footer ? '<Relationship Id="rId6" Target="footer1.xml" Type="x"/>' : ''}${withImage ? '<Relationship Id="rId9" Target="media/image1.png" Type="x"/>' : ''}</Relationships>`
  const parts: Record<string, string | Uint8Array> = {
    'word/document.xml': `<?xml version="1.0"?><w:document ${NS}><w:body>${body}${sect}</w:body></w:document>`,
    'word/_rels/document.xml.rels': rels,
    'word/styles.xml': `<?xml version="1.0"?><w:styles ${NS}>${styles ?? ''}</w:styles>`,
  }
  if (header) parts['word/header1.xml'] = `<?xml version="1.0"?><w:hdr ${NS}>${header}</w:hdr>`
  if (footer) parts['word/footer1.xml'] = `<?xml version="1.0"?><w:ftr ${NS}>${footer}</w:ftr>`
  if (withImage) parts['word/media/image1.png'] = Uint8Array.from(atob(PNG_BASE64), (c) => c.charCodeAt(0))
  return zip(parts)
}

export const asFile = (data: ArrayBuffer | string, name: string) => new File([data], name)
