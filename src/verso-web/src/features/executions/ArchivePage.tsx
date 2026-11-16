import { useEffect, useState } from 'react'
import { Link } from 'react-router'

import { StatusPill } from '@/components/StatusPill'
import { Button } from '@/components/ui/button'
import { Field, Input, Select, Switch } from '@/components/ui/form'
import { documentCategoriesApi } from '@/lib/api/documentCategories'
import { documentsApi } from '@/lib/api/executions'
import type { ArchivedDocumentDto, DocumentCategoryDto } from '@/lib/api/generated'

import { DownloadButton } from './DownloadButton'
import { dayEndIso, dayStartIso, formatBytes, formatDate, formatDateTime, shortId } from './format'

const PAGE_SIZE = 25

type ListState =
  | { kind: 'loading' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; items: ArchivedDocumentDto[]; total: number }

interface Filters {
  text: string
  from: string
  to: string
  /** '' = all, 'none' = uncategorized, otherwise a category id. */
  category: string
  /** Also list documents a later archived document replaced (ADR 0014). */
  replaced: boolean
}

const NO_FILTERS: Filters = { text: '', from: '', to: '', category: '', replaced: false }

export function ArchivePage() {
  const [filters, setFilters] = useState<Filters>(NO_FILTERS)
  const [applied, setApplied] = useState<Filters>(NO_FILTERS)
  const [page, setPage] = useState(1)
  const [state, setState] = useState<ListState>({ kind: 'loading' })
  const [categories, setCategories] = useState<DocumentCategoryDto[]>([])

  // Deactivated categories are offered too: documents archived under them keep them. If this fails, the filter just has fewer options.
  useEffect(() => {
    const controller = new AbortController()
    documentCategoriesApi
      .list(true, controller.signal)
      .then(setCategories)
      .catch(() => undefined)
    return () => controller.abort()
  }, [])

  useEffect(() => {
    const timer = setTimeout(() => setApplied(filters), 300)
    return () => clearTimeout(timer)
  }, [filters])

  useEffect(() => {
    const controller = new AbortController()
    documentsApi
      .search(applied.text, dayStartIso(applied.from), dayEndIso(applied.to), page, PAGE_SIZE, controller.signal, applied.category || undefined, applied.replaced)
      .then((result) => setState({ kind: 'ready', items: result.items, total: result.total }))
      .catch((e: unknown) => {
        if (e instanceof DOMException && e.name === 'AbortError') return
        setState({ kind: 'error', message: e instanceof Error ? e.message : 'Could not search the archive.' })
      })
    return () => controller.abort()
  }, [applied, page])

  const change = (patch: Partial<Filters>) => {
    setFilters((f) => ({ ...f, ...patch }))
    setPage(1)
  }

  const total = state.kind === 'ready' ? state.total : 0
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE))
  const filtered = JSON.stringify(filters) !== JSON.stringify(NO_FILTERS)

  return (
    <div className="max-w-[1440px] px-4 pt-6 pb-10 md:px-7">
      <p className="mb-4 max-w-prose text-ink-2">
        Archived documents, newest first. Search matches the file name and the metadata values (invoice number, customer…). Each download is checked against the recorded SHA-256 and written to the audit trail.
      </p>

      <form
        aria-label="Search the archive"
        className="mb-4 grid grid-cols-[minmax(220px,2fr)_repeat(3,minmax(150px,1fr))_auto] items-end gap-3 rounded-[10px] border border-line bg-surface p-3.5 max-lg:grid-cols-2 max-md:grid-cols-1 [&>button]:h-9 [&_input]:h-9 [&_select]:h-9"
        onSubmit={(e) => e.preventDefault()}
      >
        <Field label="Search" htmlFor="a-text">
          <Input
            id="a-text"
            type="search"
            placeholder="File name, invoice number, customer…"
            value={filters.text}
            onChange={(e) => change({ text: e.target.value })}
          />
        </Field>
        <Field label="Category" htmlFor="a-category">
          <Select id="a-category" value={filters.category} onChange={(e) => change({ category: e.target.value })}>
            <option value="">All categories</option>
            <option value="none">Uncategorized</option>
            {categories.map((c) => (
              <option key={c.id} value={c.id}>
                {c.isActive ? c.name : `${c.name} (inactive)`}
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Archived from" htmlFor="a-from">
          <Input id="a-from" type="date" value={filters.from} max={filters.to || undefined} onChange={(e) => change({ from: e.target.value })} />
        </Field>
        <Field label="Archived to" htmlFor="a-to">
          <Input id="a-to" type="date" value={filters.to} min={filters.from || undefined} onChange={(e) => change({ to: e.target.value })} />
        </Field>
        <Button type="button" disabled={!filtered} onClick={() => change(NO_FILTERS)}>
          Clear
        </Button>
        <div className="col-span-full">
          <Switch checked={filters.replaced} onCheckedChange={(replaced) => change({ replaced })} label="Show replaced documents" />
        </div>
      </form>

      {state.kind === 'loading' && <p className="text-ink-3">Searching…</p>}
      {state.kind === 'error' && (
        <p role="alert" className="rounded-lg border border-err/30 bg-err-soft px-4 py-3 text-err">
          {state.message}
        </p>
      )}
      {state.kind === 'ready' && state.items.length === 0 && (
        <section className="max-w-xl rounded-lg border border-dashed border-line-strong bg-surface p-6">
          <h2 className="mb-1 text-[14.5px] font-semibold">{filtered ? 'No documents match' : 'Nothing archived yet'}</h2>
          <p className="text-ink-2">{filtered ? 'Change or clear the search.' : 'Documents appear here once a run reaches its Archive step.'}</p>
        </section>
      )}
      {state.kind === 'ready' && state.items.length > 0 && (
        <>
          <div className="overflow-x-auto rounded-[10px] border border-line bg-surface">
            <table className="w-full border-collapse text-left text-[13px]">
              <caption className="sr-only">Archived documents</caption>
              <thead>
                <tr className="border-b border-line text-xs text-ink-3">
                  <th scope="col" className="px-3.5 py-2.5 font-medium">Document</th>
                  <th scope="col" className="px-3.5 py-2.5 font-medium">Category</th>
                  <th scope="col" className="px-3.5 py-2.5 font-medium">Metadata</th>
                  <th scope="col" className="px-3.5 py-2.5 font-medium">Folder</th>
                  <th scope="col" className="px-3.5 py-2.5 font-medium">Archived</th>
                  <th scope="col" className="px-3.5 py-2.5 font-medium">Keep until</th>
                  <th scope="col" className="px-3.5 py-2.5 font-medium">SHA-256</th>
                  <th scope="col" className="px-3.5 py-2.5 font-medium"><span className="sr-only">Download</span></th>
                </tr>
              </thead>
              <tbody>
                {state.items.map((d) => (
                  <tr key={d.id} className="border-b border-line last:border-b-0 hover:bg-surface-2">
                    <td className="px-3.5 py-2.5">
                      <div className="font-medium">
                        {d.fileName}
                        {d.supersededAt && (
                          <span className="ml-2 align-middle">
                            <StatusPill tone="neutral">Replaced {formatDate(d.supersededAt)}</StatusPill>
                          </span>
                        )}
                      </div>
                      <div className="text-xs text-ink-3">
                        {formatBytes(d.sizeBytes)} ·{' '}
                        <Link to={`/executions/${d.executionId}`} className="font-mono text-cyan-strong" aria-label={`Open run ${shortId(d.executionId)}`}>
                          run {shortId(d.executionId)}
                        </Link>
                      </div>
                    </td>
                    <td className="px-3.5 py-2.5 whitespace-nowrap">
                      {d.categoryName ? <StatusPill tone="neutral">{d.categoryName}</StatusPill> : <span className="text-xs text-ink-3">Uncategorized</span>}
                    </td>
                    <td className="px-3.5 py-2.5 text-xs text-ink-2">
                      {Object.entries(d.metadata).map(([key, value]) => (
                        <div key={key}>
                          <span className="text-ink-3">{key}:</span> {value}
                        </div>
                      ))}
                    </td>
                    <td className="px-3.5 py-2.5 font-mono text-[12px] text-ink-2">{d.folderPath}</td>
                    <td className="px-3.5 py-2.5 whitespace-nowrap text-ink-2">{formatDateTime(d.archivedAt)}</td>
                    <td className="px-3.5 py-2.5 whitespace-nowrap text-ink-2">{d.retentionUntil ? formatDate(d.retentionUntil) : 'Permanent'}</td>
                    <td className="px-3.5 py-2.5 font-mono text-[12px] text-ink-3" title={d.sha256}>
                      {d.sha256.slice(0, 12)}…
                    </td>
                    <td className="px-3.5 py-2.5">
                      <DownloadButton documentId={d.id} fileName={d.fileName} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <nav aria-label="Pages" className="mt-3 flex items-center gap-3 text-[13px] text-ink-2">
            <span>
              {(page - 1) * PAGE_SIZE + 1}–{(page - 1) * PAGE_SIZE + state.items.length} of {total}
            </span>
            <Button className="ml-auto" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>
              Previous
            </Button>
            <Button disabled={page >= pages} onClick={() => setPage((p) => p + 1)}>
              Next
            </Button>
          </nav>
        </>
      )}
    </div>
  )
}
