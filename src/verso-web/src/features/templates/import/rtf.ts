import { ImportError, MAX_IMAGE_BYTES, MAX_PARAGRAPHS, pageFieldToken, sniffImage, toDataUri, type ParaStyle, type ParsedDoc } from './types'

const TWIPS_PER_MM = 56.6929

/** Destinations whose content is never text of the document. */
const SKIPPED = new Set([
  'stylesheet', 'info', 'listtable', 'listoverridetable', 'rsidtbl', 'generator', 'xmlnstbl', 'filetbl', 'revtbl', 'themedata', 'colorschememapping',
  'datastore', 'latentstyles', 'object', 'nonshppict', 'headerl', 'headerf', 'footerl', 'footerf', 'pnseclvl', 'private', 'userprops', 'mmathPr',
])

const SYMBOLS: Record<string, string> = {
  emdash: '—', endash: '–', bullet: '•', lquote: '‘', rquote: '’', ldblquote: '“', rdblquote: '”', emspace: ' ', enspace: ' ', tab: ' ',
}

interface Fmt {
  bold: boolean
  italic: boolean
  /** Half-points. */
  size?: number
  color?: number
  font?: number
}

interface Group extends Fmt {
  /** Where paragraphs of this group go. */
  target: 'body' | 'header' | 'footer'
  skip: boolean
  /** Characters still to ignore after a `\uN` escape (its ANSI fallback). */
  skipChars: number
  pict?: { hex: string; mime?: 'image/png' | 'image/jpeg'; widthMm?: number; heightMm?: number; unsupported?: boolean }
  fonttbl?: boolean
  colortbl?: boolean
  /** Inside a `\\fldinst` group: the field instruction collected so far (`PAGE`, `NUMPAGES`…). */
  fieldInst?: string
}

interface Run {
  text: string
  fmt: Fmt
}

const codePageLabel = (cp: number) => (cp >= 1250 && cp <= 1258 ? `windows-${cp}` : cp === 28591 ? 'iso-8859-1' : 'windows-1252')

/** Reads an RTF document into blocks. Approximate by design: see the report for what is simplified or left out. */
export function parseRtf(text: string): ParsedDoc {
  if (!/^\s*\{\\rtf/.test(text)) throw new ImportError('That file is not a valid RTF document.')

  const report = new Set<string>()
  const doc: ParsedDoc = { body: [], header: [], footer: [], page: {}, report: [] }
  const colors: string[] = []
  const fonts = new Map<number, { name: string; family?: string }>()
  let codePage = 1252
  let uc = 1
  let defaultFont: number | undefined

  const stack: Group[] = []
  let g: Group = { target: 'body', skip: false, skipChars: 0, bold: false, italic: false }
  let align: ParaStyle['align']
  let runs: Run[] = []
  let inTable = false
  let cell: Run[] = []
  let rowCells: string[] = []
  let cellStyle: ParaStyle | undefined
  let table: { rows: string[][]; cellx: number[]; style?: ParaStyle } | null = null
  let rowCellx: number[] = []
  let paragraphs = 0
  let fontCapture = null as { index: number; name: string } | null
  let colorBuf = { r: -1, g: -1, b: -1 }
  let pendingBytes: number[] = []
  /** A page field in a header or footer was written as its token: its shown result (`\\fldrslt`) is dropped. */
  let suppressResult = false

  const styleOf = (fmt: Fmt, a: ParaStyle['align']): ParaStyle => {
    const s: ParaStyle = {}
    if (fmt.bold) s.bold = true
    if (fmt.italic) s.italic = true
    if (fmt.size) s.fontSizePt = fmt.size / 2
    if (fmt.color !== undefined && colors[fmt.color]) s.color = colors[fmt.color]
    const font = fmt.font !== undefined ? fonts.get(fmt.font) : defaultFont !== undefined ? fonts.get(defaultFont) : undefined
    if (font) s.fontName = font.family === 'modern' ? `${font.name} monospace` : font.family === 'roman' && !/sans/i.test(font.name) ? `${font.name} serif` : font.name
    if (a) s.align = a
    return s
  }
  const flushDecoded = () => {
    if (pendingBytes.length === 0) return
    const decoded = new TextDecoder(codePageLabel(codePage)).decode(new Uint8Array(pendingBytes))
    pendingBytes = []
    add(decoded)
  }
  const add = (s: string) => {
    if (g.skip || g.pict || !s) return
    const target = inTable ? cell : runs
    const last = target[target.length - 1]
    if (last && last.fmt.bold === g.bold && last.fmt.italic === g.italic && last.fmt.size === g.size && last.fmt.color === g.color && last.fmt.font === g.font) last.text += s
    else target.push({ text: s, fmt: { bold: g.bold, italic: g.italic, size: g.size, color: g.color, font: g.font } })
  }
  const textOf = (rs: Run[]) => rs.map((r) => r.text).join('')
  const blocks = () => doc[g.target]

  const emitParagraph = () => {
    if (++paragraphs > MAX_PARAGRAPHS) throw new ImportError('The document is too long to import.')
    const lead = runs.find((r) => r.text.trim())
    const text = textOf(runs)
    if (runs.some((r) => r.text.trim() && lead && (r.fmt.bold !== lead.fmt.bold || r.fmt.italic !== lead.fmt.italic || r.fmt.size !== lead.fmt.size || r.fmt.color !== lead.fmt.color)))
      report.add('Mixed formatting inside a paragraph (for example one bold word) is simplified to the paragraph’s first style.')
    blocks().push({ kind: 'para', text, style: styleOf(lead?.fmt ?? { bold: g.bold, italic: g.italic, size: g.size, color: g.color, font: g.font }, align) })
    runs = []
  }
  const flushTable = () => {
    if (!table) return
    const { rows, cellx, style } = table
    table = null
    if (rows.length === 0) return
    const widths = cellx.length === rows[0]!.length && cellx.length > 0 ? cellx.map((x, i) => x - (cellx[i - 1] ?? 0)) : []
    const total = widths.reduce((a, b) => a + b, 0)
    blocks().push({ kind: 'table', rows, widthsPct: total > 0 && widths.every((w) => w > 0) ? widths.map((w) => (w / total) * 100) : undefined, style: style ?? {} })
  }
  const flushRuns = () => {
    flushDecoded()
    if (runs.length > 0) {
      flushTable()
      emitParagraph()
    }
  }

  const finishPict = (p: NonNullable<Group['pict']>) => {
    if (p.unsupported || !p.mime) {
      report.add('Only PNG and JPEG images can be imported; other pictures (EMF, WMF…) were left out.')
      return
    }
    const hex = p.hex.replace(/[^0-9a-fA-F]/g, '')
    const bytes = new Uint8Array(hex.length >> 1)
    for (let i = 0; i < bytes.length; i++) bytes[i] = parseInt(hex.slice(i * 2, i * 2 + 2), 16)
    const mime = sniffImage(bytes)
    if (!mime) {
      report.add('Only PNG and JPEG images can be imported; other pictures (EMF, WMF…) were left out.')
      return
    }
    if (bytes.length > MAX_IMAGE_BYTES) {
      report.add('An image over about 500 KB was left out; add a smaller copy with the Image element.')
      return
    }
    flushRuns()
    blocks().push({ kind: 'image', src: toDataUri(bytes, mime), widthMm: p.widthMm, heightMm: p.heightMm })
  }

  const n = text.length
  let i = 0
  while (i < n) {
    const ch = text[i]!
    if (ch === '{') {
      flushDecoded()
      stack.push(g)
      g = { ...g, pict: undefined, fieldInst: undefined, fonttbl: g.fonttbl, colortbl: g.colortbl }
      i++
    } else if (ch === '}') {
      flushDecoded()
      if (g.pict) finishPict(g.pict)
      if (fontCapture && g.fonttbl && stack.length > 0 && !stack[stack.length - 1]!.fonttbl) fontCapture = null
      const closing = g
      const parent = stack.pop()
      if (!parent) throw new ImportError('That RTF document is damaged (unbalanced braces).')
      if (closing.target !== parent.target) {
        // Leaving a header or footer group.
        flushRuns()
        flushTable()
      }
      g = parent
      if (closing.fieldInst !== undefined && closing.target !== 'body') {
        const token = pageFieldToken(closing.fieldInst)
        if (token) {
          add(token)
          suppressResult = true
        }
      }
      i++
    } else if (ch === '\\') {
      i++
      const c = text[i] ?? ''
      if (!/[A-Za-z]/.test(c)) {
        i++
        if (c === "'") {
          const hex = text.slice(i, i + 2)
          i += 2
          if (g.skipChars > 0) g.skipChars--
          else if (!g.skip && !g.pict && /^[0-9a-fA-F]{2}$/.test(hex)) pendingBytes.push(parseInt(hex, 16))
        } else {
          flushDecoded()
          if (c === '*') {
            // An ignorable destination: skipped unless it is the picture wrapper we read.
            const rest = /^\s*\\([A-Za-z]+)/.exec(text.slice(i))
            if (!rest || (rest[1] !== 'shppict' && rest[1] !== 'fldinst')) g.skip = true
          } else if (c === '~') add(' ')
          else if (c === '_') add('-')
          else if (c === '\\' || c === '{' || c === '}') add(c)
          else if (c === '\n' || c === '\r') {
            // A backslash before a line break is a paragraph break.
            if (!g.skip) endParagraph()
          }
        }
        continue
      }
      let word = ''
      while (i < n && /[A-Za-z]/.test(text[i]!)) word += text[i++]
      let param: number | undefined
      let sign = 1
      if (text[i] === '-') {
        sign = -1
        i++
      }
      let digits = ''
      while (i < n && /[0-9]/.test(text[i]!)) digits += text[i++]
      if (digits) param = sign * parseInt(digits, 10)
      if (text[i] === ' ') i++
      control(word, param)
    } else if (ch === '\r' || ch === '\n') {
      i++
    } else {
      // Plain text up to the next control character.
      let j = i
      while (j < n && text[j] !== '\\' && text[j] !== '{' && text[j] !== '}' && text[j] !== '\r' && text[j] !== '\n') j++
      let chunk = text.slice(i, j)
      i = j
      if (g.skipChars > 0) {
        const cut = Math.min(g.skipChars, chunk.length)
        chunk = chunk.slice(cut)
        g.skipChars -= cut
      }
      flushDecoded()
      if (g.pict) g.pict.hex += chunk
      else if (g.fieldInst !== undefined) g.fieldInst += chunk
      else if (g.fonttbl && fontCapture) {
        const semi = chunk.indexOf(';')
        fontCapture.name += semi >= 0 ? chunk.slice(0, semi) : chunk
        if (semi >= 0) {
          const family = fonts.get(fontCapture.index)?.family
          fonts.set(fontCapture.index, { name: fontCapture.name.trim(), family })
          fontCapture = null
        }
      } else if (g.colortbl) {
        for (const part of chunk) if (part === ';') commitColor()
      } else add(chunk)
    }
  }
  if (stack.length > 0) throw new ImportError('That RTF document is damaged (unbalanced braces).')
  flushRuns()
  flushTable()

  doc.report = [...report]
  return doc

  function commitColor() {
    colors.push(colorBuf.r >= 0 ? `#${[colorBuf.r, colorBuf.g, colorBuf.b].map((v) => Math.max(0, Math.min(255, v)).toString(16).padStart(2, '0')).join('').toUpperCase()}` : '')
    colorBuf = { r: -1, g: -1, b: -1 }
  }

  function endParagraph() {
    flushDecoded()
    if (inTable) {
      // A paragraph break inside a table cell is a line break in the cell.
      add('\n')
      return
    }
    flushTable()
    emitParagraph()
  }

  function control(word: string, param: number | undefined) {
    if (g.pict) {
      if (word === 'pngblip') g.pict.mime = 'image/png'
      else if (word === 'jpegblip') g.pict.mime = 'image/jpeg'
      else if (word === 'emfblip' || word === 'wmetafile' || word === 'macpict' || word === 'pmmetafile' || word === 'dibitmap' || word === 'wbitmap') g.pict.unsupported = true
      else if (word === 'picwgoal' && param) g.pict.widthMm = param / TWIPS_PER_MM
      else if (word === 'pichgoal' && param) g.pict.heightMm = param / TWIPS_PER_MM
      return
    }
    if (g.skip) return
    if (word === 'pict') {
      g.pict = { hex: '' }
      return
    }
    if (SKIPPED.has(word)) {
      g.skip = true
      return
    }
    switch (word) {
      case 'fldinst':
        g.fieldInst = ''
        return
      case 'fldrslt':
        if (suppressResult) {
          g.skip = true
          suppressResult = false
        }
        return
      case 'fonttbl':
        g.fonttbl = true
        return
      case 'colortbl':
        g.colortbl = true
        colorBuf = { r: -1, g: -1, b: -1 }
        return
      case 'red':
        colorBuf.r = param ?? 0
        return
      case 'green':
        colorBuf.g = param ?? 0
        return
      case 'blue':
        colorBuf.b = param ?? 0
        return
      case 'ansicpg':
        if (param) codePage = param
        return
      case 'uc':
        uc = param ?? 1
        return
      case 'deff':
        defaultFont = param
        return
      case 'header':
      case 'headerr':
      case 'footer':
      case 'footerr':
        flushRuns()
        flushTable()
        g.target = word.startsWith('header') ? 'header' : 'footer'
        return
      case 'paperw':
        if (param) (doc.page.sizeMm ??= { width: 0, height: 0 }).width = param / TWIPS_PER_MM
        return
      case 'paperh':
        if (param) (doc.page.sizeMm ??= { width: 0, height: 0 }).height = param / TWIPS_PER_MM
        return
      case 'landscape':
        doc.page.landscape = true
        return
      case 'margl':
      case 'margr':
      case 'margt':
      case 'margb': {
        const m = (doc.page.marginsMm ??= { top: 18, right: 16, bottom: 18, left: 16 })
        const key = { margl: 'left', margr: 'right', margt: 'top', margb: 'bottom' }[word] as 'left'
        m[key] = Math.max(0, (param ?? 0) / TWIPS_PER_MM)
        return
      }
    }
    if (g.fonttbl) {
      if (word === 'f' && param !== undefined) {
        fontCapture = { index: param, name: '' }
        fonts.set(param, { name: '' })
      } else if (fontCapture && (word === 'fmodern' || word === 'froman' || word === 'fswiss' || word === 'fnil')) {
        fonts.set(fontCapture.index, { name: '', family: word.slice(1) })
      }
      return
    }
    if (g.colortbl) return

    switch (word) {
      case 'b':
        g.bold = param !== 0
        break
      case 'i':
        g.italic = param !== 0
        break
      case 'fs':
        if (param) g.size = param
        break
      case 'cf':
        g.color = param
        break
      case 'f':
        g.font = param
        break
      case 'plain':
        g.bold = false
        g.italic = false
        g.size = undefined
        g.color = undefined
        g.font = undefined
        break
      case 'pard':
        align = undefined
        inTable = false
        break
      case 'ql':
      case 'qj':
        align = 'left'
        break
      case 'qc':
        align = 'center'
        break
      case 'qr':
        align = 'right'
        break
      case 'par':
      case 'sect':
        endParagraph()
        break
      case 'line':
        add('\n')
        break
      case 'u': {
        if (param === undefined) break
        flushDecoded()
        add(String.fromCharCode(param < 0 ? param + 65536 : param))
        g.skipChars = uc
        break
      }
      case 'trowd':
        flushRuns()
        rowCells = []
        rowCellx = []
        cellStyle = undefined
        table ??= { rows: [], cellx: [] }
        break
      case 'cellx':
        if (param !== undefined) rowCellx.push(param)
        break
      case 'intbl':
        inTable = true
        break
      case 'cell': {
        flushDecoded()
        if (cell.length === 0 && runs.length > 0) {
          // Some writers leave out intbl: the text before cell is still the cell's.
          cell = runs
          runs = []
        }
        const lead = cell.find((r) => r.text.trim())
        cellStyle ??= lead ? styleOf(lead.fmt, align) : undefined
        rowCells.push(textOf(cell).trim())
        cell = []
        break
      }
      case 'row':
        if (!table) table = { rows: [], cellx: [] }
        table.rows.push(rowCells)
        if (table.cellx.length === 0) table.cellx = rowCellx
        table.style ??= cellStyle
        rowCells = []
        inTable = false
        break
      default:
        if (SYMBOLS[word]) add(SYMBOLS[word]!)
        break
    }
  }
}
