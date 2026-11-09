/**
 * Checks the designer runs on every change. The structural rules mirror LayoutParser so authors see
 * problems before saving (the server still validates every save). "Unmapped" bindings, i.e. paths the
 * data schema doesn't describe, are errors too: publishing is blocked until every binding is mapped.
 */
import { bindingsOf, isMapped, isValidPath, parseText } from './bindings'
import {
  contentWidthMm,
  MAX_COORDINATE_MM,
  MAX_ELEMENTS,
  MAX_IMAGE_DATA_URI_CHARS,
  MAX_REGION_HEIGHT_MM,
  MAX_TEXT_CHARS,
  REGION_LABELS,
  type Layout,
  type PageSettings,
  type PositionedElement,
  type TextStyle,
} from './layout'
import { allElements, isValidId } from './ops'
import { schemaFields } from './schema'

export interface DesignIssue {
  severity: 'error' | 'warning'
  code: string
  message: string
  elementId?: string
  /** A property the issue is about, e.g. `binding`, `columns`, `src`, `heightMm`. */
  field?: string
}

const COLOR = /^#[0-9A-Fa-f]{6}$/
const IMAGE = /^data:image\/(png|jpeg);base64,([A-Za-z0-9+/]+={0,2})$/

export function validateDesign(layout: Layout, page: PageSettings, dataSchema: unknown): DesignIssue[] {
  const issues: DesignIssue[] = []
  const fields = schemaFields(dataSchema)
  const width = contentWidthMm(page)
  const err = (elementId: string | undefined, code: string, message: string, field?: string) =>
    issues.push({ severity: 'error', code, message, elementId, field })
  const warn = (elementId: string | undefined, code: string, message: string, field?: string) =>
    issues.push({ severity: 'warning', code, message, elementId, field })

  for (const region of ['header', 'footer'] as const) {
    const band = layout[region]
    if (band && (band.heightMm < 0 || band.heightMm > MAX_REGION_HEIGHT_MM))
      err(undefined, 'region.height', `${REGION_LABELS[region]} height must be 0–${MAX_REGION_HEIGHT_MM} mm.`, `${region}.heightMm`)
  }

  const located = allElements(layout)
  if (located.length > MAX_ELEMENTS) err(undefined, 'layout.too_many', `Too many elements (${located.length}); at most ${MAX_ELEMENTS}.`)

  const seen = new Set<string>()
  for (const { region, element: e } of located) {
    const id = e.id
    if (!isValidId(id)) err(id, 'id.invalid', 'Name: use 1–64 letters, digits, - or _.', 'id')
    else if (seen.has(id)) err(id, 'id.duplicate', `Another element is also named '${id}'.`, 'id')
    seen.add(id)

    if (region !== 'body') {
      const p = e as PositionedElement
      const band = layout[region]!.heightMm
      if (p.widthMm < 0.1 || p.heightMm < 0.1 || p.xMm < 0 || p.yMm < 0 || p.xMm > MAX_COORDINATE_MM || p.yMm > MAX_COORDINATE_MM)
        err(id, 'position.range', 'Position and size must be positive numbers.', 'position')
      else {
        if (p.yMm + p.heightMm > band + 0.05)
          warn(id, 'position.clipped', `Extends below the ${region} band (${band} mm) and will be cut off.`, 'position')
        if (p.xMm + p.widthMm > width + 0.05) warn(id, 'position.outside', 'Extends past the right page margin.', 'position')
      }
    } else {
      const space = (e as { spaceBeforeMm?: number }).spaceBeforeMm
      if (space !== undefined && (space < 0 || space > 100)) err(id, 'space.range', 'Space before must be 0–100 mm.', 'spaceBeforeMm')
    }

    if ('style' in e.props && e.props.style) checkStyle(e.props.style, id, err)

    switch (e.type) {
      case 'text': {
        const text = e.props.text ?? ''
        if (text.length > MAX_TEXT_CHARS) err(id, 'text.length', `At most ${MAX_TEXT_CHARS} characters.`, 'text')
        for (const part of parseText(text))
          if (part.kind === 'malformed') err(id, 'token.malformed', `${part.raw} isn't a valid field reference.`, 'text')
        break
      }
      case 'field':
        if (!e.props.binding) err(id, 'binding.required', 'Choose a data field.', 'binding')
        else if (!isValidPath(e.props.binding)) err(id, 'binding.invalid', 'Not a valid data path.', 'binding')
        if ((e.props.label ?? '').length > 200) err(id, 'label.length', 'Label: at most 200 characters.', 'label')
        break
      case 'table': {
        if (!e.props.binding) err(id, 'binding.required', 'Choose the list the table repeats over.', 'binding')
        else if (!isValidPath(e.props.binding)) err(id, 'binding.invalid', 'Not a valid data path.', 'binding')
        const columns = e.props.columns ?? []
        if (columns.length === 0) err(id, 'columns.required', 'Add at least one column.', 'columns')
        let total = 0
        columns.forEach((c, i) => {
          if (!c.binding) err(id, 'column.binding', `Column ${i + 1}: choose a field.`, 'columns')
          else if (!isValidPath(c.binding)) err(id, 'column.binding', `Column ${i + 1}: not a valid data path.`, 'columns')
          if ((c.header ?? '').length > 100) err(id, 'column.header', `Column ${i + 1}: heading at most 100 characters.`, 'columns')
          if (c.widthPct !== undefined) {
            if (c.widthPct < 1 || c.widthPct > 100) err(id, 'column.width', `Column ${i + 1}: width must be 1–100%.`, 'columns')
            total += c.widthPct
          }
        })
        if (total > 100) err(id, 'columns.width', `Column widths add up to ${total}%, more than 100%.`, 'columns')
        break
      }
      case 'divider':
        if (e.props.thicknessPt !== undefined && (e.props.thicknessPt < 0.25 || e.props.thicknessPt > 6))
          err(id, 'divider.thickness', 'Thickness must be 0.25–6 pt.', 'thicknessPt')
        if (e.props.color !== undefined && !COLOR.test(e.props.color)) err(id, 'color', 'Use a hex color like #1A232C.', 'color')
        break
      case 'box':
        for (const key of ['borderColor', 'fill'] as const)
          if (e.props[key] !== undefined && !COLOR.test(e.props[key]!)) err(id, 'color', 'Use a hex color like #1A232C.', key)
        if (e.props.radiusMm !== undefined && (e.props.radiusMm < 0 || e.props.radiusMm > 20))
          err(id, 'box.radius', 'Corner radius must be 0–20 mm.', 'radiusMm')
        break
      case 'image':
        if (!e.props.src) err(id, 'image.required', 'Choose a PNG or JPEG image.', 'src')
        else if (e.props.src.length > MAX_IMAGE_DATA_URI_CHARS) err(id, 'image.size', 'The image is too large (about 500 KB at most).', 'src')
        else if (!IMAGE.test(e.props.src)) err(id, 'image.format', 'Only embedded PNG or JPEG images are allowed.', 'src')
        break
      case 'signature':
        break
    }

    for (const b of bindingsOf(e)) {
      if (!isValidPath(b.path)) continue
      if (!isMapped(fields, b)) {
        const shown = b.column ? `${b.column.arrayPath}[].${b.column.relative}` : b.path
        err(id, 'binding.unmapped', `${shown} isn't in the data model (${b.where}).`, b.where === 'text' ? 'text' : b.column ? 'columns' : 'binding')
      }
    }
  }
  return issues
}

function checkStyle(
  style: TextStyle,
  id: string,
  err: (elementId: string | undefined, code: string, message: string, field?: string) => void,
) {
  if (style.fontSizePt !== undefined && (style.fontSizePt < 5 || style.fontSizePt > 48)) err(id, 'style.size', 'Font size must be 5–48 pt.', 'style')
  if (style.color !== undefined && !COLOR.test(style.color)) err(id, 'style.color', 'Use a hex color like #1A232C.', 'style')
}

/** Maps a server validation path like `layout.header.elements[3].props.src` to the element it's about. */
export function elementIdForServerPath(layout: Layout, path: string): string | undefined {
  const m = /^layout\.(header|footer)\.elements\[(\d+)\]|^layout\.body\.blocks\[(\d+)\]/.exec(path)
  if (!m) return undefined
  if (m[1]) return layout[m[1] as 'header' | 'footer']?.elements[Number(m[2])]?.id
  return layout.body?.blocks[Number(m[3])]?.id
}
