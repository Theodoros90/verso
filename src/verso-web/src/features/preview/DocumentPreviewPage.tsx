import { ExternalLink, Minus, Play, Plus } from 'lucide-react'
import { useCallback, useEffect, useMemo, useState } from 'react'

import { Button } from '@/components/ui/button'
import { Field, Segmented, Select, Textarea } from '@/components/ui/form'
import { requestBlob } from '@/lib/api/client'
import type { PdfPreviewDto, RenderIssueDto, TemplateSummaryDto } from '@/lib/api/generated'
import { previewsApi, templatesApi } from '@/lib/api/templates'
import { waitForPreview } from '@/lib/realtime'
import { cn } from '@/lib/utils'

/** Width the document is laid out at (an A4 sheet plus the renderer's page gutter); scaled by the zoom buttons. */
const DOC_WIDTH_PX = 880
const DOC_HEIGHT_PX = 1250
const MAX_JSON_BYTES = 2 * 1024 * 1024

type Tab = 'html' | 'pdf'

type Result =
  | { kind: 'idle' }
  | { kind: 'rendering' }
  | { kind: 'error'; message: string }
  | { kind: 'html'; html: string; issues: RenderIssueDto[]; ms: number }
  | { kind: 'pdf'; url: string; preview: PdfPreviewDto; ms: number }

/** Parses the data box. Returns the data, or a message for the reader. */
export function parseData(text: string): { ok: true; value: unknown } | { ok: false; message: string } {
  if (new Blob([text]).size > MAX_JSON_BYTES) return { ok: false, message: 'The data is over the 2 MB limit.' }
  try {
    const value: unknown = JSON.parse(text)
    if (value === null || typeof value !== 'object' || Array.isArray(value))
      return { ok: false, message: 'Use a JSON object, for example { "invoice": { … } }.' }
    return { ok: true, value }
  } catch {
    return { ok: false, message: 'The data is not valid JSON.' }
  }
}

/**
 * Document preview: pick a template, optionally change the data, and see the saved draft rendered with that data.
 * HTML is rendered by the API; PDF is rendered by the Worker with Chromium (the real output). Nothing is stored
 * except the 24-hour PDF preview the API already keeps.
 */
export function DocumentPreviewPage() {
  const [templates, setTemplates] = useState<TemplateSummaryDto[] | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [templateId, setTemplateId] = useState('')
  const [dataText, setDataText] = useState('')
  const [defaultData, setDefaultData] = useState('')
  const [tab, setTab] = useState<Tab>('html')
  const [zoom, setZoom] = useState(0.8)
  const [result, setResult] = useState<Result>({ kind: 'idle' })
  const [runKey, setRunKey] = useState(0)
  const [appliedData, setAppliedData] = useState<unknown>(undefined)

  useEffect(() => {
    const controller = new AbortController()
    templatesApi
      .list(1, 100, controller.signal)
      .then((page) => {
        const previewable = page.items.filter((t) => t.hasDraft)
        setTemplates(previewable)
        if (previewable[0]) setTemplateId(previewable[0].id)
      })
      .catch((e: unknown) => {
        if (!controller.signal.aborted) setLoadError(e instanceof Error ? e.message : 'The templates could not be loaded.')
      })
    return () => controller.abort()
  }, [])

  // When the template changes, load its draft sample data into the data box and render once.
  useEffect(() => {
    if (!templateId) return
    const controller = new AbortController()
    setResult({ kind: 'idle' })
    templatesApi
      .draft(templateId, controller.signal)
      .then((r) => {
        const text = JSON.stringify(r.body.sampleData ?? {}, null, 2)
        setDefaultData(text)
        setDataText(text)
        setAppliedData(undefined)
        setRunKey((k) => k + 1)
      })
      .catch((e: unknown) => {
        if (!controller.signal.aborted)
          setResult({ kind: 'error', message: e instanceof Error ? e.message : 'The draft could not be loaded.' })
      })
    return () => controller.abort()
  }, [templateId])

  const parsed = useMemo(() => (dataText ? parseData(dataText) : null), [dataText])
  const edited = dataText !== defaultData

  // Renders whenever the template, the format, the applied data or a refresh changes.
  useEffect(() => {
    if (!templateId || runKey === 0) return
    const controller = new AbortController()
    let objectUrl: string | null = null
    const started = performance.now()
    const elapsed = () => Math.round(performance.now() - started)
    setResult({ kind: 'rendering' })
    const run =
      tab === 'html'
        ? templatesApi
            .previewHtml(templateId, appliedData)
            .then((r): Result => ({ kind: 'html', html: r.html, issues: r.issues, ms: elapsed() }))
        : templatesApi
            .queuePdfPreview(templateId, appliedData)
            .then((queued) => waitForPreview(queued.previewId, controller.signal))
            .then(async (preview): Promise<Result> => {
              if (preview.status !== 'Succeeded')
                return { kind: 'error', message: `The PDF could not be rendered. ${preview.error ?? ''}`.trim() }
              objectUrl = URL.createObjectURL(await requestBlob(previewsApi.contentUrl(preview.previewId), controller.signal))
              return { kind: 'pdf', url: objectUrl, preview, ms: elapsed() }
            })
    run
      .then((next) => {
        if (!controller.signal.aborted) setResult(next)
      })
      .catch((e: unknown) => {
        if (e instanceof DOMException && e.name === 'AbortError') return
        if (!controller.signal.aborted) setResult({ kind: 'error', message: e instanceof Error ? e.message : 'The preview failed.' })
      })
    return () => {
      controller.abort()
      if (objectUrl) URL.revokeObjectURL(objectUrl)
    }
  }, [templateId, tab, appliedData, runKey])

  const render = useCallback(() => {
    if (!parsed?.ok) return
    setAppliedData(edited ? parsed.value : undefined)
    setRunKey((k) => k + 1)
  }, [parsed, edited])

  const template = templates?.find((t) => t.id === templateId)
  const issues = result.kind === 'html' ? result.issues : result.kind === 'pdf' ? result.preview.issues : []

  if (loadError)
    return (
      <p role="alert" className="m-6 rounded-lg bg-err-soft px-3 py-2 text-err">
        {loadError}
      </p>
    )
  if (templates === null) return <p className="p-6 text-ink-3">Loading templates…</p>
  if (templates.length === 0)
    return (
      <div className="max-w-[1440px] px-4 pt-6 md:px-7">
        <p className="max-w-xl rounded-lg border border-dashed border-line-strong bg-surface p-6 text-ink-2">
          There is no template to preview yet. Create one in the Template designer and save a draft.
        </p>
      </div>
    )

  return (
    <div className="flex min-h-0 flex-1 flex-col lg:flex-row">
      <section aria-label="Document" className="flex min-h-[420px] min-w-0 flex-1 flex-col bg-[var(--stage)]">
        <div className="flex flex-none flex-wrap items-center gap-2 border-b border-line bg-surface px-3 py-2">
          <Select aria-label="Template" className="w-auto" value={templateId} onChange={(e) => setTemplateId(e.target.value)}>
            {templates.map((t) => (
              <option key={t.id} value={t.id}>
                {t.name}
              </option>
            ))}
          </Select>
          <Segmented<Tab>
            label="Preview format"
            value={tab}
            onChange={setTab}
            options={[
              { value: 'html', label: 'HTML' },
              { value: 'pdf', label: 'PDF' },
            ]}
          />
          <span className="grow" />
          {tab === 'html' && (
            <>
              <Button variant="ghost" size="icon" aria-label="Zoom out" onClick={() => setZoom((z) => Math.max(0.4, +(z - 0.1).toFixed(2)))}>
                <Minus />
              </Button>
              <span className="min-w-10 text-center font-mono text-[12.5px]" aria-label="Zoom level">
                {Math.round(zoom * 100)}%
              </span>
              <Button variant="ghost" size="icon" aria-label="Zoom in" onClick={() => setZoom((z) => Math.min(1.5, +(z + 0.1).toFixed(2)))}>
                <Plus />
              </Button>
            </>
          )}
          <Button size="sm" onClick={render} disabled={!parsed?.ok || result.kind === 'rendering'}>
            <Play /> Render
          </Button>
        </div>
        <Stage result={result} zoom={zoom} />
      </section>

      <aside
        aria-label="Preview details"
        className="flex w-full flex-none flex-col gap-4 overflow-auto border-t border-line bg-surface p-4 lg:w-[360px] lg:border-t-0 lg:border-l"
      >
        <section>
          <h2 className="mb-2 text-[13px] font-semibold">Render checks</h2>
          {result.kind === 'idle' || result.kind === 'rendering' || result.kind === 'error' ? (
            <p className="text-[12.5px] text-ink-3">Checks appear after the document is rendered.</p>
          ) : issues.length === 0 ? (
            <p className="rounded-lg bg-ok-soft px-3 py-2 text-[12.5px] text-ok">Every field in the template found a value in the data.</p>
          ) : (
            <ul aria-label="Preview issues" className="flex flex-col gap-1 text-[12.5px]">
              {issues.map((i, n) => (
                <li key={n} className="flex gap-2">
                  <span className={cn('font-medium', i.severity === 'error' ? 'text-err' : 'text-warn')}>{i.severity}</span>
                  <span className="text-ink-2">{i.message}</span>
                </li>
              ))}
            </ul>
          )}
        </section>

        <section>
          <h2 className="mb-2 text-[13px] font-semibold">Render details</h2>
          <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-[12.5px]">
            <dt className="text-ink-3">Template</dt>
            <dd>{template?.name ?? '—'}</dd>
            <dt className="text-ink-3">Version</dt>
            <dd>Saved draft</dd>
            <dt className="text-ink-3">Data</dt>
            <dd>{edited ? 'Edited here' : 'Template sample data'}</dd>
            <dt className="text-ink-3">Engine</dt>
            <dd>{tab === 'html' ? 'Verso renderer (API)' : 'Chromium (Worker)'}</dd>
            <dt className="text-ink-3">Render time</dt>
            <dd>{result.kind === 'html' || result.kind === 'pdf' ? `${result.ms} ms` : '—'}</dd>
            {result.kind === 'pdf' && result.preview.sizeBytes != null && (
              <>
                <dt className="text-ink-3">File size</dt>
                <dd>{Math.max(1, Math.round(result.preview.sizeBytes / 1024))} KB</dd>
              </>
            )}
          </dl>
          {result.kind === 'pdf' && (
            <a href={result.url} target="_blank" rel="noopener noreferrer" className="mt-2 inline-flex items-center gap-1 text-[12.5px] text-cyan">
              Open PDF in new tab <ExternalLink className="size-3.5" aria-hidden="true" />
            </a>
          )}
          <p className="mt-2 text-xs text-ink-3">The preview shows the last saved draft. Nothing is archived or sent.</p>
        </section>

        <section>
          <h2 className="mb-2 text-[13px] font-semibold">Source data</h2>
          <Field
            label="Data (JSON)"
            htmlFor="preview-data"
            error={parsed && !parsed.ok ? parsed.message : undefined}
            hint="Starts as the template's sample data. Change it and choose Render."
          >
            <Textarea
              id="preview-data"
              className="h-56 font-mono text-xs"
              spellCheck={false}
              value={dataText}
              onChange={(e) => setDataText(e.target.value)}
            />
          </Field>
          <Button variant="ghost" size="sm" className="mt-1" disabled={!edited} onClick={() => setDataText(defaultData)}>
            Reset to sample data
          </Button>
        </section>
      </aside>
    </div>
  )
}

function Stage({ result, zoom }: { result: Result; zoom: number }) {
  if (result.kind === 'idle') return <p className="p-4 text-ink-3">Choose Render to preview the document.</p>
  if (result.kind === 'rendering')
    return (
      <p role="status" className="p-4 text-ink-3">
        Rendering…
      </p>
    )
  if (result.kind === 'error')
    return (
      <p role="alert" className="m-3 rounded-lg bg-err-soft px-3 py-2 text-err">
        {result.message}
      </p>
    )
  if (result.kind === 'pdf')
    // Our own PDF (a blob: URL of this page); not sandboxed because Chrome's PDF viewer doesn't run in a sandboxed frame.
    return <iframe title="PDF preview" src={result.url} className="min-h-0 w-full flex-1 border-0" />
  return (
    <div className="min-h-0 flex-1 overflow-auto">
      <div style={{ width: DOC_WIDTH_PX * zoom, height: DOC_HEIGHT_PX * zoom }}>
        {/* Empty sandbox: no scripts, no same-origin access. The document also carries its own CSP. */}
        <iframe
          title="Document preview"
          sandbox=""
          srcDoc={result.html}
          className="origin-top-left border-0"
          style={{ width: DOC_WIDTH_PX, height: DOC_HEIGHT_PX, transform: `scale(${zoom})` }}
        />
      </div>
    </div>
  )
}
