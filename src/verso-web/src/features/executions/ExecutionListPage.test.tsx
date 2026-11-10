import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { routes } from '@/app/routes'
import type { ArchivedDocumentDto, ExecutionSummaryDto } from '@/lib/api/generated'
import { hub } from '@/test/fakeHub'

vi.mock('@microsoft/signalr', async () => (await import('@/test/fakeHub')).fakeSignalR)

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

const summary = (id: string, status: string, extra: Partial<ExecutionSummaryDto> = {}): ExecutionSummaryDto => ({
  id,
  status,
  workflowKey: 'invoice-standard',
  workflowVersion: 2,
  correlationId: `corr-${id}`,
  submittedAt: '2026-11-10T09:00:00Z',
  completedAt: status === 'Queued' || status === 'Running' ? null : '2026-11-10T09:00:04Z',
  attempt: 1,
  failureCode: null,
  failureMessage: null,
  ...extra,
})

function stubApi(handler: (url: URL) => unknown) {
  const calls: URL[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: string) => {
      const url = new URL(input, 'http://x')
      calls.push(url)
      if (url.pathname === '/api/health') return json({ status: 'Healthy', totalDurationMs: 1, checks: [] })
      if (url.pathname === '/api/v1/workflows')
        return json({ items: [{ id: 'w1', key: 'invoice-standard', name: 'Invoice standard', latestPublishedVersion: 2, hasDraft: false, updatedAt: '2026-11-09T08:00:00Z' }], page: 1, pageSize: 100, total: 1 })
      return json(handler(url))
    }),
  )
  return calls
}

const lastQuery = (calls: URL[], path: string) => calls.filter((u) => u.pathname === path).at(-1)!.searchParams

describe('Execution list', () => {
  beforeEach(() => hub.reset())

  it('lists runs newest first with status, workflow version and a link to each run', async () => {
    stubApi(() => ({
      items: [
        summary('0193a6c2-0000-7000-8000-00000000aaaa', 'Rejected', { failureCode: 'data.invalid', failureMessage: 'The data failed 2 validation rule(s).' }),
        summary('0193a6c2-0000-7000-8000-00000000bbbb', 'Succeeded'),
        summary('0193a6c2-0000-7000-8000-00000000cccc', 'Running'),
      ],
      page: 1,
      pageSize: 25,
      total: 3,
    }))
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/executions'] })} />)

    const table = await screen.findByRole('table', { name: 'Executions' })
    const rows = within(table).getAllByRole('row').slice(1)
    expect(rows).toHaveLength(3)
    expect(within(rows[0]!).getByText('Rejected')).toBeInTheDocument()
    expect(within(rows[0]!).getByText('The data failed 2 validation rule(s).')).toBeInTheDocument()
    expect(within(rows[0]!).getByRole('link', { name: 'Open run 0000aaaa' })).toHaveAttribute('href', '/executions/0193a6c2-0000-7000-8000-00000000aaaa')
    expect(within(rows[1]!).getByText('4.0 s')).toBeInTheDocument()
    expect(within(rows[2]!).getByText('Running')).toBeInTheDocument()
    expect(screen.getByText('1–3 of 3')).toBeInTheDocument()
  })

  it('sends the chosen filters to the API and returns to the first page', async () => {
    const calls = stubApi(() => ({ items: [summary('0193a6c2-0000-7000-8000-00000000aaaa', 'Rejected')], page: 1, pageSize: 25, total: 1 }))
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/executions'] })} />)
    const user = userEvent.setup()
    await screen.findByRole('table', { name: 'Executions' })
    await screen.findByRole('option', { name: 'Invoice standard (invoice-standard)' })

    await user.selectOptions(screen.getByLabelText('Workflow'), 'invoice-standard')
    await user.selectOptions(screen.getByLabelText('Status'), 'Rejected')
    await user.type(screen.getByLabelText('Submitted from'), '2026-11-02')
    await user.type(screen.getByLabelText('Submitted to'), '2026-11-04')
    await user.type(screen.getByLabelText('Correlation id'), ' corr-9 ')

    await waitFor(() => expect(lastQuery(calls, '/api/v1/executions').get('correlationId')).toBe('corr-9'))
    const q = lastQuery(calls, '/api/v1/executions')
    expect(q.get('workflow')).toBe('invoice-standard')
    expect(q.get('status')).toBe('Rejected')
    expect(q.get('page')).toBe('1')
    // from is the start of the first day, to the start of the day after the last one (the range is inclusive of both days)
    const spanHours = (new Date(q.get('to')!).getTime() - new Date(q.get('from')!).getTime()) / 3_600_000
    expect(spanHours).toBeGreaterThanOrEqual(71)
    expect(spanHours).toBeLessThanOrEqual(73)
    expect(new Date(q.get('from')!).getTime()).toBe(new Date('2026-11-02T00:00:00').getTime())
  })

  it('says when no run matches, and clearing the filters brings the list back', async () => {
    let empty = false
    stubApi((url) => {
      empty = url.searchParams.has('status')
      return empty ? { items: [], page: 1, pageSize: 25, total: 0 } : { items: [summary('0193a6c2-0000-7000-8000-00000000aaaa', 'Succeeded')], page: 1, pageSize: 25, total: 1 }
    })
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/executions'] })} />)
    const user = userEvent.setup()
    await screen.findByRole('table', { name: 'Executions' })

    await user.selectOptions(screen.getByLabelText('Status'), 'Failed')
    expect(await screen.findByText('No executions match these filters')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Clear filters' }))
    expect(await screen.findByRole('table', { name: 'Executions' })).toBeInTheDocument()
  })

  it('pages through results', async () => {
    const calls = stubApi((url) => ({
      items: [summary(url.searchParams.get('page') === '2' ? '0193a6c2-0000-7000-8000-00000000bbbb' : '0193a6c2-0000-7000-8000-00000000aaaa', 'Succeeded')],
      page: Number(url.searchParams.get('page')),
      pageSize: 25,
      total: 30,
    }))
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/executions'] })} />)
    const user = userEvent.setup()
    await screen.findByText('1–1 of 30')
    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled()

    await user.click(screen.getByRole('button', { name: 'Next' }))

    expect(await screen.findByText('26–26 of 30')).toBeInTheDocument()
    expect(lastQuery(calls, '/api/v1/executions').get('page')).toBe('2')
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled()
  })

  it('updates when the API reports progress, and subscribes to all executions', async () => {
    let status = 'Running'
    stubApi(() => ({ items: [summary('0193a6c2-0000-7000-8000-00000000aaaa', status)], page: 1, pageSize: 25, total: 1 }))
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/executions'] })} />)
    const table = await screen.findByRole('table', { name: 'Executions' })
    expect(within(table).getByText('Running')).toBeInTheDocument()
    await vi.waitFor(() => expect(hub.invoked).toContainEqual(['SubscribeExecutions']))

    status = 'Succeeded'
    hub.handlers.get('ExecutionUpdated')!({ executionId: '0193a6c2-0000-7000-8000-00000000aaaa', status: 'Succeeded' })

    expect(await within(table).findByText('Succeeded')).toBeInTheDocument()
    expect(within(table).queryByText('Running')).not.toBeInTheDocument()
  })
})

const doc = (id: string, extra: Partial<ArchivedDocumentDto> = {}): ArchivedDocumentDto => ({
  id,
  executionId: '0193a6c2-0000-7000-8000-00000000aaaa',
  nodeId: 'pdf',
  templateVersionId: 'tv1',
  fileName: 'INV-2026-003901.pdf',
  contentType: 'application/pdf',
  sizeBytes: 21284,
  sha256: 'c'.repeat(64),
  status: 'Archived',
  createdAt: '2026-11-10T09:00:03Z',
  archivedAt: '2026-11-10T09:00:04Z',
  folderPath: 'invoices/2026/11',
  retention: '7y',
  retentionUntil: '2033-11-10T09:00:04Z',
  metadata: { InvoiceNumber: 'INV-2026-003901', Customer: 'Kestrel Logistics Ltd' },
  ...extra,
})

describe('Archive', () => {
  beforeEach(() => hub.reset())

  it('lists archived documents with metadata, retention and a download link', async () => {
    stubApi(() => ({ items: [doc('d1'), doc('d2', { fileName: 'INV-2026-003902.pdf', retention: 'permanent', retentionUntil: null })], page: 1, pageSize: 25, total: 2 }))
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/archive'] })} />)

    const table = await screen.findByRole('table', { name: 'Archived documents' })
    const rows = within(table).getAllByRole('row').slice(1)
    expect(within(rows[0]!).getByText('INV-2026-003901.pdf')).toBeInTheDocument()
    expect(within(rows[0]!).getByText('Kestrel Logistics Ltd')).toBeInTheDocument()
    expect(within(rows[0]!).getByText('invoices/2026/11')).toBeInTheDocument()
    expect(within(rows[0]!).getByRole('button', { name: 'Download INV-2026-003901.pdf' })).toBeEnabled() // fetched with the token on click (see the run view test)
    expect(within(rows[0]!).getByRole('link', { name: 'Open run 0000aaaa' })).toHaveAttribute('href', '/executions/0193a6c2-0000-7000-8000-00000000aaaa')
    expect(within(rows[1]!).getByText('Permanent')).toBeInTheDocument()
  })

  it('searches by text and dates', async () => {
    const calls = stubApi(() => ({ items: [doc('d1')], page: 1, pageSize: 25, total: 1 }))
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/archive'] })} />)
    const user = userEvent.setup()
    await screen.findByRole('table', { name: 'Archived documents' })

    await user.type(screen.getByLabelText('Search'), '  kestrel ')
    await user.type(screen.getByLabelText('Archived from'), '2026-11-01')

    await waitFor(() => expect(lastQuery(calls, '/api/v1/documents').get('from')).not.toBeNull())
    const q = lastQuery(calls, '/api/v1/documents')
    expect(q.get('q')).toBe('kestrel')
    expect(new Date(q.get('from')!).getTime()).toBe(new Date('2026-11-01T00:00:00').getTime())
    expect(q.has('to')).toBe(false)
  })

  it('shows an empty state for an empty archive', async () => {
    stubApi(() => ({ items: [], page: 1, pageSize: 25, total: 0 }))
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/archive'] })} />)

    expect(await screen.findByText('Nothing archived yet')).toBeInTheDocument()
  })
})
