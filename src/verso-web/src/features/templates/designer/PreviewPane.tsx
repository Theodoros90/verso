import { RefreshCw, X } from 'lucide-react'
import { useCallback, useEffect, useRef, useState } from 'react'

import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import type { RenderIssueDto } from '@/lib/api/generated'
import { templatesApi } from '@/lib/api/templates'

/** Width the preview document is laid out at (an A4 sheet plus the renderer's page gutter), then scaled to fit. */
const DOC_WIDTH_PX = 880

type PreviewState = { kind: 'loading' } | { kind: 'error'; message: string } | { kind: 'ready'; html: string; issues: RenderIssueDto[] }

/**
 * Server-rendered HTML preview of the saved draft (the faithful output, plan D2). The API renders the
 * stored draft, so unsaved changes show only after saving. Displayed in a fully sandboxed iframe.
 */
export function PreviewPane({
  templateId,
  revision,
  dirty,
  onSave,
  onClose,
  onSelectElement,
}: {
  templateId: string
  /** Changes after every save, which reloads the preview. */
  revision: number
  dirty: boolean
  onSave: () => void
  onClose: () => void
  onSelectElement: (id: string) => void
}) {
  const [state, setState] = useState<PreviewState>({ kind: 'loading' })

  const load = useCallback(() => {
    setState({ kind: 'loading' })
    templatesApi
      .previewHtml(templateId)
      .then((r) => setState({ kind: 'ready', html: r.html, issues: r.issues }))
      .catch((e: unknown) => setState({ kind: 'error', message: e instanceof Error ? e.message : 'Preview failed.' }))
  }, [templateId])

  useEffect(() => load(), [load, revision])

  const frameBox = useRef<HTMLDivElement>(null)
  const [box, setBox] = useState({ width: DOC_WIDTH_PX, height: 800 })
  useEffect(() => {
    const el = frameBox.current
    if (!el || typeof ResizeObserver === 'undefined') return
    const ro = new ResizeObserver(() => setBox({ width: el.clientWidth, height: el.clientHeight }))
    ro.observe(el)
    return () => ro.disconnect()
  }, [state.kind])
  const scale = Math.min(1, box.width / DOC_WIDTH_PX)

  return (
    <section aria-label="HTML preview" className="flex min-h-0 min-w-0 flex-1 flex-col border-l border-line bg-[var(--stage)]">
      <div className="flex flex-none items-center gap-2 border-b border-line bg-surface px-3 py-2">
        <h2 className="text-[13px] font-semibold">Preview</h2>
        <span className="text-xs text-ink-3">server renderer, sample data</span>
        <Button variant="ghost" size="icon" className="ml-auto" aria-label="Refresh preview" onClick={load}>
          <RefreshCw />
        </Button>
        <Button variant="ghost" size="icon" aria-label="Close preview" onClick={onClose}>
          <X />
        </Button>
      </div>
      {dirty && (
        <div className="flex flex-none items-center gap-2 border-b border-line bg-warn-soft px-3 py-1.5 text-[12.5px] text-warn">
          Showing the last saved draft.
          <Button size="sm" className="ml-auto" onClick={onSave}>
            Save and refresh
          </Button>
        </div>
      )}
      {state.kind === 'loading' && <p className="p-4 text-ink-3">Rendering…</p>}
      {state.kind === 'error' && (
        <p role="alert" className="m-3 rounded-lg bg-err-soft px-3 py-2 text-err">
          {state.message}
        </p>
      )}
      {state.kind === 'ready' && (
        <>
          {state.issues.length > 0 && (
            <ul aria-label="Preview issues" className="max-h-32 flex-none overflow-auto border-b border-line bg-surface px-3 py-2 text-[12.5px]">
              {state.issues.map((i, n) => (
                <li key={n}>
                  <button type="button" className="flex w-full gap-2 rounded px-1 py-0.5 text-left hover:bg-surface-2" onClick={() => onSelectElement(i.elementId)}>
                    <span className={cn('font-medium', i.severity === 'error' ? 'text-err' : 'text-warn')}>{i.severity}</span>
                    <span className="text-ink-2">{i.message}</span>
                    <span className="ml-auto font-mono text-[11px] text-ink-3">{i.elementId}</span>
                  </button>
                </li>
              ))}
            </ul>
          )}
          {/* Empty sandbox: no scripts, no same-origin access. The document also carries its own CSP. */}
          <div ref={frameBox} className="relative min-h-0 flex-1 overflow-hidden">
            <iframe
              title="Document preview"
              sandbox=""
              srcDoc={state.html}
              className="absolute top-0 left-0 origin-top-left border-0 bg-[var(--stage)]"
              style={{ width: DOC_WIDTH_PX, height: box.height / scale, transform: `scale(${scale})` }}
            />
          </div>
        </>
      )}
    </section>
  )
}
