import { ExternalLink, RefreshCw, X } from 'lucide-react'
import { useCallback, useEffect, useRef, useState } from 'react'

import { Button } from '@/components/ui/button'
import { Segmented } from '@/components/ui/form'
import { cn } from '@/lib/utils'
import type { PdfPreviewDto, RenderIssueDto } from '@/lib/api/generated'
import { requestBlob } from '@/lib/api/client'
import { previewsApi, templatesApi } from '@/lib/api/templates'
import { waitForPreview } from '@/lib/realtime'

/** Width the preview document is laid out at (an A4 sheet plus the renderer's page gutter), then scaled to fit. */
const DOC_WIDTH_PX = 880

type Tab = 'html' | 'pdf'

interface PaneProps {
  templateId: string
  /** Changes after every save, which reloads the preview. */
  revision: number
  dirty: boolean
  onSave: () => void
  onClose: () => void
  onSelectElement: (id: string) => void
}

/**
 * Previews of the saved draft. HTML: rendered by the API (fast, sandboxed iframe). PDF: rendered by the Worker
 * with Chromium (the real output, paginated, with embedded fonts). Both show the last save; unsaved changes
 * appear after saving.
 */
export function PreviewPane(props: PaneProps) {
  const [tab, setTab] = useState<Tab>('html')
  const [refreshKey, setRefreshKey] = useState(0)

  return (
    <section aria-label="Preview" className="flex min-h-0 min-w-0 flex-1 flex-col border-l border-line bg-[var(--stage)]">
      <div className="flex flex-none items-center gap-2 border-b border-line bg-surface px-3 py-2">
        <h2 className="text-[13px] font-semibold">Preview</h2>
        <Segmented<Tab>
          label="Preview format"
          value={tab}
          onChange={setTab}
          options={[
            { value: 'html', label: 'HTML' },
            { value: 'pdf', label: 'PDF' },
          ]}
        />
        <Button variant="ghost" size="icon" className="ml-auto" aria-label="Refresh preview" onClick={() => setRefreshKey((k) => k + 1)}>
          <RefreshCw />
        </Button>
        <Button variant="ghost" size="icon" aria-label="Close preview" onClick={props.onClose}>
          <X />
        </Button>
      </div>
      {props.dirty && (
        <div className="flex flex-none items-center gap-2 border-b border-line bg-warn-soft px-3 py-1.5 text-[12.5px] text-warn">
          Showing the last saved draft.
          <Button size="sm" className="ml-auto" onClick={props.onSave}>
            Save and refresh
          </Button>
        </div>
      )}
      {tab === 'html' ? (
        <HtmlPreview {...props} refreshKey={refreshKey} />
      ) : (
        <PdfPreview {...props} refreshKey={refreshKey} />
      )}
    </section>
  )
}

function IssueList({ issues, onSelectElement }: { issues: RenderIssueDto[]; onSelectElement: (id: string) => void }) {
  if (issues.length === 0) return null
  return (
    <ul aria-label="Preview issues" className="max-h-32 flex-none overflow-auto border-b border-line bg-surface px-3 py-2 text-[12.5px]">
      {issues.map((i, n) => (
        <li key={n}>
          <button type="button" className="flex w-full gap-2 rounded px-1 py-0.5 text-left hover:bg-surface-2" onClick={() => onSelectElement(i.elementId)}>
            <span className={cn('font-medium', i.severity === 'error' ? 'text-err' : 'text-warn')}>{i.severity}</span>
            <span className="text-ink-2">{i.message}</span>
            <span className="ml-auto font-mono text-[11px] text-ink-3">{i.elementId}</span>
          </button>
        </li>
      ))}
    </ul>
  )
}

type HtmlState = { kind: 'loading' } | { kind: 'error'; message: string } | { kind: 'ready'; html: string; issues: RenderIssueDto[] }

function HtmlPreview({ templateId, revision, refreshKey, onSelectElement }: PaneProps & { refreshKey: number }) {
  const [state, setState] = useState<HtmlState>({ kind: 'loading' })

  const load = useCallback(() => {
    setState({ kind: 'loading' })
    templatesApi
      .previewHtml(templateId)
      .then((r) => setState({ kind: 'ready', html: r.html, issues: r.issues }))
      .catch((e: unknown) => setState({ kind: 'error', message: e instanceof Error ? e.message : 'Preview failed.' }))
  }, [templateId])

  useEffect(() => load(), [load, revision, refreshKey])

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

  if (state.kind === 'loading') return <p className="p-4 text-ink-3">Rendering…</p>
  if (state.kind === 'error')
    return (
      <p role="alert" className="m-3 rounded-lg bg-err-soft px-3 py-2 text-err">
        {state.message}
      </p>
    )
  return (
    <>
      <IssueList issues={state.issues} onSelectElement={onSelectElement} />
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
  )
}

type PdfState =
  | { kind: 'rendering'; since: number }
  | { kind: 'ready'; preview: PdfPreviewDto; url: string }
  | { kind: 'failed'; preview: PdfPreviewDto }
  | { kind: 'error'; message: string }

function PdfPreview({ templateId, revision, refreshKey, onSelectElement }: PaneProps & { refreshKey: number }) {
  const [state, setState] = useState<PdfState>({ kind: 'rendering', since: Date.now() })

  // Renders when the tab opens, after every save, and on refresh. A newer request cancels the wait for an older one.
  // The PDF is fetched with the user's token and shown from a blob: URL (an iframe src can't carry a bearer token).
  useEffect(() => {
    const controller = new AbortController()
    let objectUrl: string | null = null
    setState({ kind: 'rendering', since: Date.now() })
    templatesApi
      .queuePdfPreview(templateId)
      .then((queued) => waitForPreview(queued.previewId, controller.signal))
      .then(async (preview): Promise<PdfState> => {
        if (preview.status !== 'Succeeded') return { kind: 'failed', preview }
        objectUrl = URL.createObjectURL(await requestBlob(previewsApi.contentUrl(preview.previewId), controller.signal))
        return { kind: 'ready', preview, url: objectUrl }
      })
      .then((next) => {
        if (!controller.signal.aborted) setState(next)
      })
      .catch((e: unknown) => {
        if (e instanceof DOMException && e.name === 'AbortError') return
        setState({ kind: 'error', message: e instanceof Error ? e.message : 'The PDF preview failed.' })
      })
    return () => {
      controller.abort()
      if (objectUrl) URL.revokeObjectURL(objectUrl)
    }
  }, [templateId, revision, refreshKey])

  if (state.kind === 'rendering')
    return (
      <p role="status" className="p-4 text-ink-3">
        Rendering PDF in the Worker…
      </p>
    )
  if (state.kind === 'error')
    return (
      <p role="alert" className="m-3 rounded-lg bg-err-soft px-3 py-2 text-err">
        {state.message}
      </p>
    )
  if (state.kind === 'failed')
    return (
      <p role="alert" className="m-3 rounded-lg bg-err-soft px-3 py-2 text-err">
        The PDF could not be rendered. {state.preview.error}
      </p>
    )

  const url = state.url
  return (
    <>
      <IssueList issues={state.preview.issues} onSelectElement={onSelectElement} />
      <div className="flex flex-none items-center gap-3 border-b border-line bg-surface px-3 py-1.5 text-[12.5px] text-ink-2">
        <span>{state.preview.sizeBytes != null ? `${Math.max(1, Math.round(state.preview.sizeBytes / 1024))} KB` : ''}</span>
        <a href={url} target="_blank" rel="noopener noreferrer" className="ml-auto inline-flex items-center gap-1 text-cyan">
          Open in new tab <ExternalLink className="size-3.5" aria-hidden="true" />
        </a>
      </div>
      {/* Our own PDF (a blob: URL of this page); not sandboxed because Chrome's PDF viewer doesn't run in a sandboxed frame. */}
      <iframe title="PDF preview" src={url} className="min-h-0 w-full flex-1 border-0 bg-[var(--stage)]" />
    </>
  )
}
