import { ImageIcon } from 'lucide-react'
import type { CSSProperties, ReactNode } from 'react'

import { cn } from '@/lib/utils'

import { isMapped, parseText } from '../model/bindings'
import type { Filter, LayoutElement, TextStyle } from '../model/layout'
import { formatValue, resolvePath } from '../model/sampleValues'
import type { SchemaField } from '../model/schema'

export interface ViewContext {
  fields: SchemaField[]
  sampleData: unknown
  showSample: boolean
}

const FONTS = { sans: 'var(--font)', serif: 'var(--font-serif-stack)', mono: 'var(--font-mono-stack)' }

export function textStyleCss(style: TextStyle | undefined): CSSProperties {
  return {
    fontSize: `${style?.fontSizePt ?? 10}pt`,
    fontWeight: style?.bold ? 700 : undefined,
    fontStyle: style?.italic ? 'italic' : undefined,
    textAlign: style?.align ?? 'left',
    color: style?.color ?? undefined,
    fontFamily: style?.font ? FONTS[style.font] : undefined,
  }
}

/** `{{binding}}` chip (.chip-bind). Red when the data model doesn't have the path. */
export function BindingChip({ label, mapped }: { label: string; mapped: boolean }) {
  return (
    <span
      title={mapped ? undefined : 'Unmapped: not in the data model'}
      className={cn(
        'inline-flex max-w-full items-center rounded-[3px] px-1 align-baseline font-mono text-[0.8em] leading-[1.5] whitespace-nowrap',
        mapped ? 'bg-[#E0EFF4] text-[#075A72]' : 'bg-[#FAE6E4] text-[#BE3D37]',
      )}
    >
      {label}
    </span>
  )
}

function bound(ctx: ViewContext, path: string, filter: Filter | undefined, mapped: boolean, label: string): ReactNode {
  if (ctx.showSample) {
    const value = formatValue(resolvePath(ctx.sampleData, path), filter)
    if (value !== undefined) return value
  }
  return <BindingChip label={label} mapped={mapped} />
}

function lines(text: string): ReactNode[] {
  return text.split('\n').flatMap((line, i) => (i === 0 ? [line] : [<br key={`br${i}`} />, line]))
}

/** Editing view of one element's content. Approximates the server renderer's output (DocumentCss). */
export function ElementView({ element, ctx }: { element: LayoutElement; ctx: ViewContext }) {
  switch (element.type) {
    case 'text': {
      const parts = parseText(element.props.text ?? '')
      return (
        <div className="h-full overflow-hidden leading-[1.35] [overflow-wrap:anywhere]" style={textStyleCss(element.props.style)}>
          {parts.length === 0 && <span className="text-[#9AA5B0] italic">Empty text</span>}
          {parts.map((p, i) => {
            if (p.kind === 'literal') return <span key={i}>{lines(p.text)}</span>
            if (p.kind === 'malformed')
              return (
                <span key={i} className="text-[#BE3D37] underline decoration-wavy">
                  {p.raw}
                </span>
              )
            const mapped = isMapped(ctx.fields, { path: p.path, where: 'text', expects: 'scalar' })
            return <span key={i}>{bound(ctx, p.path, p.filter, mapped, `{{${p.path}}}`)}</span>
          })}
        </div>
      )
    }
    case 'field': {
      const { binding, filter, label, style } = element.props
      const mapped = !!binding && isMapped(ctx.fields, { path: binding, where: 'binding', expects: 'scalar' })
      return (
        <div className="h-full overflow-hidden leading-[1.35] break-words" style={textStyleCss(style)}>
          {label && <span className="mr-[2mm] text-[var(--paper-mute)]">{label}</span>}
          {binding ? bound(ctx, binding, filter, mapped, `{{${binding}}}`) : <BindingChip label="unmapped field" mapped={false} />}
        </div>
      )
    }
    case 'table':
      return <TableView element={element} ctx={ctx} />
    case 'divider':
      return (
        <div className="flex h-full min-h-[2px] items-center">
          <div className="w-full" style={{ borderTop: `${element.props.thicknessPt ?? 0.75}pt solid ${element.props.color ?? '#1A232C'}` }} />
        </div>
      )
    case 'box':
      return (
        <div
          className="h-full w-full"
          style={{
            border: `0.75pt solid ${element.props.borderColor ?? '#C4CDD6'}`,
            background: element.props.fill ?? 'transparent',
            borderRadius: `${element.props.radiusMm ?? 1}mm`,
          }}
        />
      )
    case 'image':
      return element.props.src ? (
        <img
          src={element.props.src}
          alt={element.props.alt ?? ''}
          draggable={false}
          className="block h-full max-h-full w-full"
          style={{ objectFit: element.props.fit ?? 'contain' }}
        />
      ) : (
        <div className="grid h-full min-h-[10mm] w-full place-items-center border border-dashed border-[#C4CDD6] bg-[#F8FAFB] text-[#9AA5B0]">
          <ImageIcon className="size-5" aria-hidden="true" />
        </div>
      )
    case 'signature':
      return (
        <div className="flex h-full min-h-[14mm] flex-col justify-end">
          <div className="border-t-[0.75pt] border-[#1A232C]" />
          <div className="pt-[1mm] text-[8pt] text-[var(--paper-mute)]">{element.props.label ?? 'Authorised signatory'}</div>
        </div>
      )
  }
}

const MAX_SAMPLE_ROWS = 8

function TableView({ element, ctx }: { element: Extract<LayoutElement, { type: 'table' }>; ctx: ViewContext }) {
  const { binding, columns, emptyText, style } = element.props
  const arrayMapped = !!binding && isMapped(ctx.fields, { path: binding, where: 'binding', expects: 'array' })
  const rows = ctx.showSample && binding ? resolvePath(ctx.sampleData, binding) : undefined
  const sampleRows = Array.isArray(rows) ? rows : undefined

  const align = (a: string | undefined): CSSProperties => ({ textAlign: (a ?? 'left') as CSSProperties['textAlign'] })
  const colMapped = (b: string) =>
    !!binding && !!b && isMapped(ctx.fields, { path: `${binding}.${b}`, where: 'column', expects: 'scalar', column: { arrayPath: binding, relative: b, index: 0 } })

  return (
    <table className="w-full border-collapse leading-[1.35]" style={textStyleCss(style)}>
      <thead>
        <tr>
          {columns.map((c, i) => (
            <th
              key={i}
              className="border-b-[1pt] border-[#1A232C] px-[1mm] py-[1.2mm] font-semibold"
              style={{ ...align(c.align), width: c.widthPct ? `${c.widthPct}%` : undefined }}
            >
              {c.header}
            </th>
          ))}
        </tr>
      </thead>
      <tbody>
        {sampleRows && sampleRows.length === 0 && (
          <tr>
            <td colSpan={columns.length} className="px-[1mm] py-[1.2mm] text-[var(--paper-mute)]">
              {emptyText ?? ''}
            </td>
          </tr>
        )}
        {sampleRows?.slice(0, MAX_SAMPLE_ROWS).map((row, r) => (
          <tr key={r}>
            {columns.map((c, i) => (
              <td key={i} className="border-b-[0.5pt] border-[var(--paper-line)] px-[1mm] py-[1.2mm] align-top" style={align(c.align)}>
                {formatValue(c.binding ? resolvePath(row, c.binding) : undefined, c.filter) ?? (
                  <BindingChip label={`${binding}[].${c.binding || '?'}`} mapped={colMapped(c.binding)} />
                )}
              </td>
            ))}
          </tr>
        ))}
        {!sampleRows && (
          <tr>
            {columns.map((c, i) => (
              <td key={i} className="border-b-[0.5pt] border-[var(--paper-line)] px-[1mm] py-[1.2mm] align-top" style={align(c.align)}>
                <BindingChip label={`${binding || '?'}[].${c.binding || '?'}`} mapped={arrayMapped && colMapped(c.binding)} />
              </td>
            ))}
          </tr>
        )}
      </tbody>
      {sampleRows && sampleRows.length > MAX_SAMPLE_ROWS && (
        <tfoot>
          <tr>
            <td colSpan={columns.length} className="px-[1mm] pt-[1mm] text-[8pt] text-[var(--paper-mute)]">
              … {sampleRows.length - MAX_SAMPLE_ROWS} more rows
            </td>
          </tr>
        </tfoot>
      )}
    </table>
  )
}
