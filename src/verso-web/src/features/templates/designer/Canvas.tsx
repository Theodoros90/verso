import { GripVertical, Plus } from 'lucide-react'
import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent, type PointerEvent } from 'react'

import { cn } from '@/lib/utils'

import {
  contentWidthMm,
  pageSizeMm,
  REGION_LABELS,
  TYPE_LABELS,
  type FlowBlock,
  type LayoutElement,
  type PositionedElement,
  type Region,
} from '../model/layout'
import { clamp, resize, updatePlacement } from '../model/ops'
import { bodyPerPageMm, paginate } from '../model/pages'
import type { Draft, Selection } from './designerState'
import { getPayload, hasPayload, setPayload, type DragPayload } from './dnd'
import { ElementView, type ViewContext } from './ElementView'

/** CSS pixels per millimetre at 96 dpi (the sheet is drawn in mm and scaled by the zoom). */
export const MM_PX = 96 / 25.4
/** Height of the strip drawn between two pages. */
const PAGE_STRIP_MM = 9

export type DropTarget = { region: 'header' | 'footer'; at: { xMm: number; yMm: number } } | { region: 'body'; index: number }

export interface CanvasProps {
  draft: Draft
  selection: Selection
  zoom: number
  showGrid: boolean
  ctx: ViewContext
  readOnly: boolean
  /** Element ids with errors / warnings, for the outline colour. */
  flagged: Map<string, 'error' | 'warning'>
  onSelect: (selection: Selection) => void
  onPreview: (fn: (l: Draft['layout']) => Draft['layout']) => void
  onCommit: (origin: Draft) => void
  onDrop: (payload: DragPayload, target: DropTarget) => void
  onAddBand: (region: 'header' | 'footer') => void
  onElementKey: (e: KeyboardEvent, id: string, region: Region) => void
}

export function elementLabel(e: LayoutElement): string {
  switch (e.type) {
    case 'field':
      return e.props.binding || 'unmapped field'
    case 'text': {
      const t = (e.props.text ?? '').replace(/\s+/g, ' ').trim()
      return t.length > 22 ? `${t.slice(0, 22)}…` : t || 'Empty text'
    }
    case 'table':
      return e.props.binding ? `${e.props.binding}[ ]` : 'Repeating table'
    default:
      return TYPE_LABELS[e.type]
  }
}

interface Gesture {
  id: string
  mode: 'move' | 'resize'
  startX: number
  startY: number
  el: PositionedElement
  bandHeight: number
  origin: Draft
  moved: boolean
}

export function Canvas(props: CanvasProps) {
  const { draft, zoom, showGrid } = props
  const page = draft.pageSettings
  const size = pageSizeMm(page)
  const sheetRef = useRef<HTMLDivElement>(null)
  const [sheetHeightPx, setSheetHeightPx] = useState(size.height * MM_PX)

  useEffect(() => {
    const el = sheetRef.current
    if (!el || typeof ResizeObserver === 'undefined') return
    const ro = new ResizeObserver(() => setSheetHeightPx(el.offsetHeight))
    ro.observe(el)
    return () => ro.disconnect()
  }, [])

  const [pageCount, setPageCount] = useState(1)

  return (
    <div
      className="relative min-h-0 flex-1 overflow-auto bg-[var(--stage)] [background-image:radial-gradient(var(--stage-dot)_1px,transparent_1px)] [background-size:16px_16px]"
      onPointerDown={(e) => {
        if (e.target === e.currentTarget) props.onSelect(null)
      }}
    >
      <div
        className="relative mx-auto mt-11 mb-16"
        style={{ width: size.width * MM_PX * zoom, height: sheetHeightPx * zoom }}
        onPointerDown={(e) => {
          if (e.target === e.currentTarget) props.onSelect(null)
        }}
      >
        <div
          ref={sheetRef}
          role="region"
          aria-label="Page"
          className={cn('absolute top-0 left-0 flex origin-top-left flex-col bg-[var(--paper)] text-[var(--paper-ink)] shadow-[var(--sheet-shadow)]')}
          style={{
            width: `${size.width}mm`,
            minHeight: `${size.height}mm`,
            paddingTop: `${page.marginsMm.top}mm`,
            paddingBottom: `${page.marginsMm.bottom}mm`,
            transform: `scale(${zoom})`,
            fontFamily: 'var(--font)',
            fontSize: '10pt',
            backgroundImage: showGrid
              ? 'linear-gradient(rgba(10,110,138,.07) 1px, transparent 1px), linear-gradient(90deg, rgba(10,110,138,.07) 1px, transparent 1px)'
              : undefined,
            backgroundSize: showGrid ? '5mm 5mm' : undefined,
          }}
          onPointerDown={(e) => {
            if (e.target === e.currentTarget) props.onSelect(null)
          }}
        >
          <FixedBand {...props} region="header" />
          <BodyArea {...props} onPageCount={setPageCount} />
          <FixedBand {...props} region="footer" />
        </div>
      </div>
      <div aria-hidden="true" className="pointer-events-none sticky bottom-2 z-20 h-0">
        <span className="absolute right-3 bottom-0 rounded border border-line bg-surface/90 px-2 py-0.5 font-mono text-[10.5px] text-ink-3">
        {page.size} {size.width}×{size.height} mm · content {contentWidthMm(page)} mm wide · {pageCount} {pageCount === 1 ? 'page' : 'pages'} (approx.)
      </span>
      </div>
    </div>
  )
}

/** The end of a PDF page in the designer: empty space where the block no longer fits, then a strip marking where the next page starts. */
function PageGap({ emptyMm, next, marginLeft, marginRight }: { emptyMm: number; next: number; marginLeft: number; marginRight: number }) {
  return (
    <div aria-hidden="true" data-testid="page-gap" className="pointer-events-none relative" style={{ height: `${emptyMm + PAGE_STRIP_MM}mm`, marginLeft: `-${marginLeft}mm`, marginRight: `-${marginRight}mm` }}>
      <div className="absolute inset-x-0 bottom-0 flex items-center justify-end border-y border-dashed border-cyan bg-[var(--stage)] px-3" style={{ height: `${PAGE_STRIP_MM}mm` }}>
        <span className="rounded-full border border-cyan bg-surface px-2 py-px font-sans text-[10.5px] font-medium text-cyan-strong">Page {next} starts here</span>
      </div>
    </div>
  )
}

function RegionTag({ region, selected, detail }: { region: Region; selected: boolean; detail?: string }) {
  return (
    <span
      aria-hidden="true"
      className={cn(
        'pointer-events-none absolute top-0 right-full mr-1.5 rounded-sm px-1 font-mono text-[7pt] leading-[1.6] whitespace-nowrap',
        selected ? 'bg-[#0A6E8A] text-white' : 'text-[#9AA5B0]',
      )}
    >
      {REGION_LABELS[region]}
      {detail ? ` ${detail}` : ''}
    </span>
  )
}

function FixedBand(props: CanvasProps & { region: 'header' | 'footer' }) {
  const { region, draft, selection, zoom, readOnly } = props
  const page = draft.pageSettings
  const band = draft.layout[region]
  const [dragOver, setDragOver] = useState(false)
  const gesture = useRef<Gesture | null>(null)
  const bandRef = useRef<HTMLDivElement>(null)
  const regionSelected = selection?.kind === 'region' && selection.region === region

  const margins = { marginLeft: `${page.marginsMm.left}mm`, marginRight: `${page.marginsMm.right}mm` }

  if (!band) {
    if (readOnly) return null
    return (
      <div className="relative" style={margins}>
        <button
          type="button"
          onClick={() => props.onAddBand(region)}
          className="my-[1mm] flex w-full items-center justify-center gap-1 rounded-sm border border-dashed border-[#C4CDD6] py-[1.5mm] text-[8pt] text-[#9AA5B0] hover:border-[#0A6E8A] hover:text-[#0A6E8A]"
        >
          <Plus className="size-3" aria-hidden="true" /> Add {region}
        </button>
      </div>
    )
  }

  const toMm = (clientX: number, clientY: number) => {
    const rect = bandRef.current!.getBoundingClientRect()
    return { xMm: (clientX - rect.left) / (zoom * MM_PX), yMm: (clientY - rect.top) / (zoom * MM_PX) }
  }

  const startGesture = (e: PointerEvent, el: PositionedElement, mode: Gesture['mode']) => {
    if (e.button !== 0) return
    e.stopPropagation()
    props.onSelect({ kind: 'element', id: el.id })
    if (readOnly) return
    ;(e.currentTarget as HTMLElement).setPointerCapture?.(e.pointerId)
    gesture.current = { id: el.id, mode, startX: e.clientX, startY: e.clientY, el, bandHeight: band.heightMm, origin: draft, moved: false }
  }

  const moveGesture = (e: PointerEvent) => {
    const g = gesture.current
    if (!g) return
    const dx = (e.clientX - g.startX) / (zoom * MM_PX)
    const dy = (e.clientY - g.startY) / (zoom * MM_PX)
    if (!g.moved && Math.hypot(e.clientX - g.startX, e.clientY - g.startY) < 3) return
    g.moved = true
    // Snap to whole millimetres; hold Alt for 0.1 mm.
    const snap = (v: number) => (e.altKey ? Math.round(v * 10) / 10 : Math.round(v))
    const maxW = contentWidthMm(page)
    if (g.mode === 'move') {
      const xMm = clamp(snap(g.el.xMm + dx), 0, Math.max(0, maxW - g.el.widthMm))
      const yMm = clamp(snap(g.el.yMm + dy), 0, Math.max(0, g.bandHeight - g.el.heightMm))
      props.onPreview((l) => updatePlacement(l, g.id, { xMm, yMm }))
    } else {
      const w = clamp(snap(g.el.widthMm + dx), 1, Math.max(1, maxW - g.el.xMm))
      const h = clamp(snap(g.el.heightMm + dy), 1, Math.max(1, g.bandHeight - g.el.yMm))
      props.onPreview((l) => resize(l, g.id, w, h))
    }
  }

  const endGesture = () => {
    const g = gesture.current
    gesture.current = null
    if (g?.moved) props.onCommit(g.origin)
  }

  return (
    <div className="relative flex-none" style={margins}>
      <RegionTag region={region} selected={regionSelected} detail={`${band.heightMm} mm`} />
      <div
        ref={bandRef}
        data-region={region}
        className={cn(
          'relative outline-1 -outline-offset-1',
          regionSelected ? 'outline-[#0A6E8A]' : 'outline-dashed outline-[rgba(10,110,138,.25)]',
          dragOver && 'outline-2 outline-[#0A6E8A]',
        )}
        style={{ height: `${band.heightMm}mm` }}
        onPointerDown={(e) => {
          if (e.target === e.currentTarget) props.onSelect({ kind: 'region', region })
        }}
        onDragOver={(e) => {
          if (readOnly || !hasPayload(e)) return
          e.preventDefault()
          setDragOver(true)
        }}
        onDragLeave={() => setDragOver(false)}
        onDrop={(e) => {
          setDragOver(false)
          const payload = getPayload(e)
          if (!payload || readOnly) return
          e.preventDefault()
          props.onDrop(payload, { region, at: toMm(e.clientX, e.clientY) })
        }}
      >
        {band.elements.map((el) => (
          <ElementFrame key={el.id} {...props} element={el} region={region} positioned onGestureStart={startGesture} onGestureMove={moveGesture} onGestureEnd={endGesture} />
        ))}
      </div>
    </div>
  )
}

function BodyArea(props: CanvasProps & { onPageCount: (pages: number) => void }) {
  const { draft, readOnly, selection, zoom, onPageCount } = props
  const page = draft.pageSettings
  const blocks = draft.layout.body?.blocks ?? []
  const [dropIndex, setDropIndex] = useState<number | null>(null)
  const ref = useRef<HTMLDivElement>(null)
  const regionSelected = selection?.kind === 'region' && selection.region === 'body'
  /** Empty space (mm) left on a page before the block at this index, which has to start the next page. */
  const [gaps, setGaps] = useState<number[]>([])
  const capacityMm = bodyPerPageMm(page, draft.layout.header?.heightMm ?? 0, draft.layout.footer?.heightMm ?? 0)

  // Measure the blocks as they are drawn and leave the space the PDF would: a block that doesn't fit on the page moves
  // to the next one. A block's own height doesn't depend on the gaps before it, so this settles in one pass.
  const measure = () => {
    const root = ref.current
    if (!root || capacityMm < 20) return
    const wrappers = Array.from(root.querySelectorAll<HTMLElement>(':scope > [data-block-wrap]'))
    const gapNodes = new Map(Array.from(root.querySelectorAll<HTMLElement>(':scope > [data-page-gap]')).map((n) => [Number(n.dataset.pageGap), n]))
    const scale = zoom * MM_PX // screen px per mm
    let previousBottom = root.getBoundingClientRect().top
    const extents = wrappers.map((w, i) => {
      const gap = gapNodes.get(i)?.getBoundingClientRect().height ?? 0
      const bottom = w.getBoundingClientRect().bottom
      const extent = (bottom - previousBottom - gap) / scale
      previousBottom = bottom
      return Math.max(0, extent)
    })
    const next = paginate(extents, capacityMm, 0).map((g) => Math.round(g * 10) / 10)
    setGaps((old) => (old.length === next.length && old.every((g, i) => g === next[i]) ? old : next))
  }
  useLayoutEffect(measure)
  useEffect(() => {
    const root = ref.current
    if (!root || typeof ResizeObserver === 'undefined') return
    const ro = new ResizeObserver(measure)
    ro.observe(root)
    return () => ro.disconnect()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [zoom, capacityMm])
  const pages = 1 + gaps.filter((g, i) => g > 0 && i < blocks.length).length
  useEffect(() => onPageCount(pages), [pages, onPageCount])

  const indexAt = (clientY: number) => {
    const nodes = Array.from(ref.current?.querySelectorAll<HTMLElement>('[data-block-id]') ?? [])
    const i = nodes.findIndex((n) => {
      const r = n.getBoundingClientRect()
      return clientY < r.top + r.height / 2
    })
    return i === -1 ? nodes.length : i
  }

  return (
    <div className="relative flex flex-1 flex-col" style={{ marginLeft: `${page.marginsMm.left}mm`, marginRight: `${page.marginsMm.right}mm` }}>
      <RegionTag region="body" selected={regionSelected} />
      <div
        ref={ref}
        data-region="body"
        className={cn('relative flex-1 py-[2mm]', regionSelected && 'outline-1 -outline-offset-1 outline-[#0A6E8A]')}
        onPointerDown={(e) => {
          if (e.target === e.currentTarget) props.onSelect({ kind: 'region', region: 'body' })
        }}
        onDragOver={(e) => {
          if (readOnly || !hasPayload(e)) return
          e.preventDefault()
          setDropIndex(indexAt(e.clientY))
        }}
        onDragLeave={(e) => {
          if (!ref.current?.contains(e.relatedTarget as Node)) setDropIndex(null)
        }}
        onDrop={(e) => {
          const index = indexAt(e.clientY)
          setDropIndex(null)
          const payload = getPayload(e)
          if (!payload || readOnly) return
          e.preventDefault()
          props.onDrop(payload, { region: 'body', index })
        }}
      >
        {blocks.length === 0 && !readOnly && (
          <div className="pointer-events-none grid min-h-[30mm] place-items-center rounded-sm border border-dashed border-[#C4CDD6] text-[9pt] text-[#9AA5B0]">
            Body: drop components or data fields here. Blocks stack top to bottom and flow onto more pages.
          </div>
        )}
        {blocks.flatMap((block, i) => {
          const empty = gaps[i] ?? 0
          const before = gaps.slice(0, i).filter((g) => g > 0).length
          return [
            ...(empty > 0 ? [<div key={`gap-${block.id}`} data-page-gap={i}><PageGap emptyMm={empty} next={before + 2} marginLeft={page.marginsMm.left} marginRight={page.marginsMm.right} /></div>] : []),
            <div key={block.id} data-block-wrap>
              {dropIndex === i && <DropLine />}
              <ElementFrame {...props} element={block} region="body" positioned={false} />
            </div>,
          ]
        })}
        {dropIndex === blocks.length && blocks.length > 0 && <DropLine />}
      </div>
    </div>
  )
}

function DropLine() {
  return <div aria-hidden="true" className="h-0.5 rounded bg-[#A82E6B]" />
}

function ElementFrame(
  props: CanvasProps & {
    element: LayoutElement
    region: Region
    positioned: boolean
    onGestureStart?: (e: PointerEvent, el: PositionedElement, mode: Gesture['mode']) => void
    onGestureMove?: (e: PointerEvent) => void
    onGestureEnd?: () => void
  },
) {
  const { element, region, positioned, selection, readOnly, ctx, flagged } = props
  const selected = selection?.kind === 'element' && selection.id === element.id
  const flag = flagged.get(element.id)
  const ref = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (selected && ref.current && document.activeElement?.closest('[data-designer-canvas-focus]') == null) {
      ref.current.scrollIntoView?.({ block: 'nearest', inline: 'nearest' })
    }
  }, [selected])

  const label = `${TYPE_LABELS[element.type]}: ${elementLabel(element)}`
  const common = {
    ref,
    role: 'button' as const,
    tabIndex: 0,
    'aria-label': label,
    'aria-pressed': selected,
    'data-element-id': element.id,
    'data-designer-canvas-focus': '',
    onFocus: () => {
      if (!selected) props.onSelect({ kind: 'element', id: element.id })
    },
    onKeyDown: (e: KeyboardEvent) => props.onElementKey(e, element.id, region),
  }
  const frameClass = cn(
    'group/el border border-dashed border-transparent outline-none',
    !readOnly && 'hover:border-[rgba(10,110,138,.45)]',
    flag === 'error' && 'border-solid border-[rgba(190,61,55,.6)]',
    flag === 'warning' && 'border-[rgba(168,110,18,.7)]',
    selected && 'z-[5] border-[1.5px] border-solid border-[#0A6E8A]',
    'focus-visible:border-[1.5px] focus-visible:border-solid focus-visible:border-[#0A6E8A]',
  )

  const tag = selected && (
    <span
      aria-hidden="true"
      className="pointer-events-none absolute -top-[19px] -left-[1.5px] rounded-t-[3px] bg-[#0A6E8A] px-1.5 py-px font-sans text-[10.5px] leading-[1.5] whitespace-nowrap text-white"
      style={{ transform: `scale(${1 / props.zoom})`, transformOrigin: 'bottom left' }}
    >
      {TYPE_LABELS[element.type]} · {element.id}
    </span>
  )

  if (positioned) {
    const el = element as PositionedElement
    return (
      <div
        {...common}
        className={cn(frameClass, 'absolute rounded-[2px]', !readOnly && 'cursor-move')}
        style={{ left: `${el.xMm}mm`, top: `${el.yMm}mm`, width: `${el.widthMm}mm`, height: `${el.heightMm}mm`, touchAction: 'none' }}
        onPointerDown={(e) => props.onGestureStart?.(e, el, 'move')}
        onPointerMove={props.onGestureMove}
        onPointerUp={props.onGestureEnd}
        onPointerCancel={props.onGestureEnd}
      >
        <ElementView element={el} ctx={ctx} />
        {tag}
        {selected && !readOnly && (
          <span
            aria-hidden="true"
            className="absolute -right-[5px] -bottom-[5px] size-2.5 cursor-nwse-resize rounded-[2px] border-[1.5px] border-[#0A6E8A] bg-white"
            style={{ transform: `scale(${1 / props.zoom})` }}
            onPointerDown={(e) => props.onGestureStart?.(e, el, 'resize')}
          />
        )}
      </div>
    )
  }

  const block = element as FlowBlock
  return (
    <div
      {...common}
      data-block-id={block.id}
      draggable={!readOnly}
      onDragStart={(e) => {
        setPayload(e, { kind: 'block', id: block.id })
      }}
      onPointerDown={(e) => {
        e.stopPropagation()
        props.onSelect({ kind: 'element', id: block.id })
      }}
      className={cn(frameClass, 'relative rounded-[2px]', !readOnly && 'cursor-grab')}
      style={{ marginTop: block.spaceBeforeMm ? `${block.spaceBeforeMm}mm` : undefined }}
    >
      {!readOnly && (
        <GripVertical
          aria-hidden="true"
          className="absolute top-0.5 -left-[18px] size-3.5 text-[#9AA5B0] opacity-0 group-hover/el:opacity-100"
          style={{ transform: `scale(${1 / props.zoom})` }}
        />
      )}
      <ElementView element={block} ctx={ctx} />
      {tag}
    </div>
  )
}
