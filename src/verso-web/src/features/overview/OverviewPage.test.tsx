import { render, screen, within } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { routes } from '@/app/routes'
import type { ExecutionSummaryDto } from '@/lib/api/generated'
import { hub } from '@/test/fakeHub'

vi.mock('@microsoft/signalr', async () => (await import('@/test/fakeHub')).fakeSignalR)

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
const page = (items: unknown[], total = items.length) => ({ items, page: 1, pageSize: 100, total })

const now = new Date()
const run = (id: string, status: string, extra: Partial<ExecutionSummaryDto> = {}): ExecutionSummaryDto => ({
  id,
  status,
  workflowKey: 'invoice-standard',
  workflowVersion: 2,
  correlationId: `corr-${id}`,
  submittedAt: now.toISOString(),
  completedAt: status === 'Succeeded' || status === 'Failed' ? new Date(now.getTime() + 2000).toISOString() : null,
  attempt: 1,
  failureCode: null,
  failureMessage: null,
  ...extra,
})

const failed = run('0193a6c2-0000-7000-8000-00000000aaaa', 'Failed', { failureMessage: 'The document store was unavailable.' })
const awaiting = run('0193a6c2-0000-7000-8000-00000000bbbb', 'AwaitingApproval')

function stubApi(executions: ExecutionSummaryDto[]) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: string) => {
      const url = new URL(input, 'http://x')
      switch (url.pathname) {
        case '/api/health':
          return json({ status: 'Healthy', totalDurationMs: 1, checks: [] })
        case '/api/v1/executions': {
          const status = url.searchParams.get('status')
          return json(page(status ? executions.filter((e) => e.status === status) : executions))
        }
        case '/api/v1/templates':
          return json(page([{ id: 't1', key: 'invoice', name: 'Standard invoice', documentType: 'invoice', latestPublishedVersion: 3, hasDraft: true, updatedAt: '2026-11-15T08:00:00Z' }]))
        case '/api/v1/workflows':
          return json(page([], 2))
        default:
          return json({}, 404)
      }
    }),
  )
}

const renderOverview = () => render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/'] })} />)

describe('Overview dashboard', () => {
  beforeEach(() => hub.reset())

  it("shows today's numbers, what needs attention, recent templates and runs per workflow", async () => {
    stubApi([run('0193a6c2-0000-7000-8000-00000000cccc', 'Succeeded'), run('0193a6c2-0000-7000-8000-00000000dddd', 'Succeeded'), failed, awaiting])
    renderOverview()

    expect(await screen.findByText('Runs submitted today')).toBeInTheDocument()
    const output = screen.getByRole('region', { name: 'Runs today' })
    expect(within(output).getByText('Runs submitted today').parentElement).toHaveTextContent(/^4Runs submitted today/)
    expect(within(output).getByText('1 still open')).toBeInTheDocument()
    expect(screen.getByRole('img', { name: /Runs submitted per hour today/ })).toBeInTheDocument()

    const attention = screen.getByRole('region', { name: 'Needs attention' })
    expect(within(attention).getByText('2 items')).toBeInTheDocument()
    expect(within(attention).getByText('The document store was unavailable.', { exact: false })).toBeInTheDocument()
    expect(within(attention).getByRole('link', { name: 'Review run' })).toHaveAttribute('href', `/executions/${failed.id}`)
    expect(within(attention).getByRole('link', { name: 'Open run' })).toHaveAttribute('href', `/executions/${awaiting.id}`)

    const templates = screen.getByRole('region', { name: 'Recently edited templates' })
    expect(within(templates).getByRole('link', { name: /Standard invoice/ })).toHaveAttribute('href', '/templates/t1')

    const byWorkflow = screen.getByRole('region', { name: 'Runs by workflow, last 7 days' })
    expect(within(byWorkflow).getByText('invoice-standard')).toBeInTheDocument()
    expect(within(byWorkflow).getByText('2 active workflows')).toBeInTheDocument()
  })

  it('says all is clear when no run needs a decision', async () => {
    stubApi([run('0193a6c2-0000-7000-8000-00000000cccc', 'Succeeded')])
    renderOverview()

    expect(await screen.findByText('All clear')).toBeInTheDocument()
  })

  it('explains an empty system', async () => {
    stubApi([])
    renderOverview()

    expect(await screen.findByText('Nothing has been submitted yet today.', { exact: false })).toBeInTheDocument()
    expect(screen.getByText('No runs in the last 7 days.')).toBeInTheDocument()
  })
})
