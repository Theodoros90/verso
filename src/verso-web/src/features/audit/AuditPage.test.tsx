import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it, vi } from 'vitest'

import { routes } from '@/app/routes'

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

const entry = (sequence: number, action: string, entityType: string, entityId: string, details: unknown = null) => ({
  id: `a${sequence}`,
  sequence,
  occurredAt: '2026-11-10T09:00:00Z',
  actorId: 'dev.operator',
  actorName: 'Omar Operator',
  action,
  entityType,
  entityId,
  details,
  traceId: '0123456789abcdef0123456789abcdef',
})

function stubApi() {
  const calls: URL[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: string) => {
      const url = new URL(input, 'http://x')
      calls.push(url)
      if (url.pathname === '/api/health') return json({ status: 'Healthy', totalDurationMs: 1, checks: [] })
      if (url.pathname === '/api/v1/audit/actions') return json(['DocumentDownloaded', 'ExecutionSubmitted', 'TemplatePublished'])
      if (url.pathname === '/api/v1/audit')
        return json({
          items: [
            entry(9, 'DocumentDownloaded', 'Document', '0193a6c2-0000-7000-8000-0000000000d1', { sha256: 'abc' }),
            entry(8, 'ExecutionSubmitted', 'Execution', '0193a6c2-0000-7000-8000-0000000000e1', { workflowKey: 'invoice-standard' }),
          ],
          page: 1,
          pageSize: 50,
          total: 2,
        })
      return json({ title: 'Not found' }, 404)
    }),
  )
  return calls
}

describe('Audit trail', () => {
  it('lists entries newest first, with who, what, details and a link to the run', async () => {
    const calls = stubApi()
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/audit'] })} />)

    const table = await screen.findByRole('table', { name: 'Audit trail' })
    const rows = within(table).getAllByRole('row').slice(1)
    expect(within(rows[0]!).getByText('DocumentDownloaded')).toBeInTheDocument()
    expect(within(rows[0]!).getByText('{"sha256":"abc"}')).toBeInTheDocument()
    expect(within(rows[1]!).getByText('Omar Operator')).toBeInTheDocument()
    expect(within(rows[1]!).getByRole('link', { name: 'Open Execution 000000e1' })).toHaveAttribute('href', '/executions/0193a6c2-0000-7000-8000-0000000000e1')
    expect(calls.find((u) => u.pathname === '/api/v1/audit')!.searchParams.get('newestFirst')).toBe('true')
  })

  it('sends the filters to the API', async () => {
    const calls = stubApi()
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/audit'] })} />)
    const user = userEvent.setup()
    await screen.findByRole('option', { name: 'ExecutionSubmitted' })

    await user.selectOptions(screen.getByLabelText('Action'), 'ExecutionSubmitted')
    await user.selectOptions(screen.getByLabelText('Entity type'), 'Execution')
    await user.type(screen.getByLabelText('Actor id'), 'dev.operator')
    await user.type(screen.getByLabelText('From'), '2026-11-01')

    // Wait for the last filter typed (From), not the first: the request for it goes out after the Actor id one.
    await waitFor(() => expect(calls.filter((u) => u.pathname === '/api/v1/audit').at(-1)!.searchParams.get('from')).not.toBeNull())
    const q = calls.filter((u) => u.pathname === '/api/v1/audit').at(-1)!.searchParams
    expect(q.get('action')).toBe('ExecutionSubmitted')
    expect(q.get('entityType')).toBe('Execution')
    expect(new Date(q.get('from')!).getTime()).toBe(new Date('2026-11-01T00:00:00').getTime())
  })
})
