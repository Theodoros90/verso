/** What the DOCX and RTF readers hand to the layout builder: a plain, format-neutral description of a document. */

export interface ParaStyle {
  fontSizePt?: number
  bold?: boolean
  italic?: boolean
  align?: 'left' | 'center' | 'right'
  /** `#RRGGBB` */
  color?: string
  /** The font's name as written in the file; mapped to sans / serif / mono when the layout is built. */
  fontName?: string
}

export type ParsedBlock =
  | { kind: 'para'; text: string; style: ParaStyle }
  | { kind: 'image'; src: string; widthMm?: number; heightMm?: number }
  | { kind: 'table'; rows: string[][]; widthsPct?: number[]; style: ParaStyle }

export interface ParsedPage {
  landscape?: boolean
  marginsMm?: { top: number; right: number; bottom: number; left: number }
  /** Page size in mm as written in the file (width, height), to report when it isn't A4. */
  sizeMm?: { width: number; height: number }
}

export interface ParsedDoc {
  body: ParsedBlock[]
  header: ParsedBlock[]
  footer: ParsedBlock[]
  page: ParsedPage
  /** Notes for the author about what was approximated or left out. Each appears once. */
  report: string[]
}

export class ImportError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'ImportError'
  }
}

export const MAX_FILE_BYTES = 20 * 1024 * 1024
/** Images are embedded as data URIs; the layout allows about 500 KB of image (700 000 characters of base64). */
export const MAX_IMAGE_BYTES = 500_000
export const MAX_PARAGRAPHS = 5000

export function toDataUri(bytes: Uint8Array, mime: 'image/png' | 'image/jpeg'): string {
  let binary = ''
  for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000))
  return `data:${mime};base64,${btoa(binary)}`
}

/** `image/png` or `image/jpeg` from the file's first bytes, or null for anything the layout can't hold (GIF, EMF, WMF…). */
export function sniffImage(bytes: Uint8Array): 'image/png' | 'image/jpeg' | null {
  if (bytes.length > 8 && bytes[0] === 0x89 && bytes[1] === 0x50 && bytes[2] === 0x4e && bytes[3] === 0x47) return 'image/png'
  if (bytes.length > 3 && bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff) return 'image/jpeg'
  return null
}

/**
 * A word processor's page-number field as Verso's header and footer token: PAGE → `{{page}}`, NUMPAGES (or SECTIONPAGES) →
 * `{{pages}}`. Any other field returns null and keeps the text it currently shows.
 */
export function pageFieldToken(instruction: string): string | null {
  const m = /^\s*(PAGE|NUMPAGES|SECTIONPAGES)\b/i.exec(instruction)
  if (!m) return null
  return m[1]!.toUpperCase() === 'PAGE' ? '{{page}}' : '{{pages}}'
}
