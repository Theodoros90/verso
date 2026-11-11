import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import type { MeDto } from '@/lib/api/generated'
import { setSession } from '@/lib/auth'

import { routes } from './routes'

vi.mock('@microsoft/signalr', async () => (await import('@/test/fakeHub')).fakeSignalR)

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

const viewer: MeDto = {
  id: 'dev.viewer',
  name: 'Vera Viewer',
  roles: ['Viewer'],
  permissions: { read: true, templateAuthoring: false, workflowAuthoring: false, operate: false, readPayload: false, readAudit: false, administer: false },
}

const failedRun = {
  id: 'x1',
  status: 'Failed',
  workflow: { id: 'w1', key: 'invoice-standard', version: 1, versionId: 'wv1' },
  templateVersions: {},
  idempotencyKey: 'k',
  correlationId: 'c',
  inputHash: 'a'.repeat(64),
  submittedAt: '2026-11-10T09:00:00Z',
  submittedBy: 'dev.operator',
  startedAt: '2026-11-10T09:00:01Z',
  completedAt: '2026-11-10T09:00:05Z',
  attempt: 1,
  failureCode: 'step.technical_error',
  failureMessage: 'Step failed',
  steps: [],
  documents: [],
}

/**
 * The UI hides what a role can't do (the API refuses it anyway; see RoleMatrixTests on the server). Signed in as a
 * Viewer: read-only everywhere.
 */
describe('A Viewer', () => {
  let requested: string[]

  beforeEach(() => {
    requested = []
    setSession({ mode: 'Development', me: viewer, getToken: async () => 'dev:dev.viewer', signOut: () => undefined })
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) => {
        requested.push(url)
        if (url === '/api/health') return json({ status: 'Healthy', totalDurationMs: 1, checks: [] })
        if (url.startsWith('/api/v1/templates?') || url.startsWith('/api/v1/workflows?')) return json({ items: [], page: 1, pageSize: 100, total: 0 })
        if (url === '/api/v1/executions/x1') return json(failedRun)
        if (url.startsWith('/api/v1/executions/x1/events')) return json([])
        return json({ title: 'Not found' }, 404)
      }),
    )
  })

  it("doesn't see the audit trail in the navigation", async () => {
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/'] })} />)

    const nav = await screen.findByRole('navigation', { name: 'Main' })
    expect(within(nav).queryByRole('link', { name: 'Audit trail' })).not.toBeInTheDocument()
    expect(within(nav).getByRole('link', { name: 'Executions' })).toBeInTheDocument()
  })

  it('gets an explanation instead of the audit trail when opening it directly, and nothing is requested', async () => {
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/audit'] })} />)

    expect(await screen.findByText('The audit trail is available to the Auditor and Admin roles.')).toBeInTheDocument()
    expect(requested.some((u) => u.startsWith('/api/v1/audit'))).toBe(false)
  })

  it("can't create templates or workflows", async () => {
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/templates'] })} />)
    expect(await screen.findByText('No templates yet', { exact: false })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /New template/ })).not.toBeInTheDocument()

    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/workflows'] })} />)
    expect(await screen.findByText('No workflows yet')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /New workflow/ })).not.toBeInTheDocument()
  })

  it("can't retry a failed run or open its submitted data", async () => {
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/executions/x1'] })} />)

    expect(await screen.findByText(/Failed: a technical problem/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Retry/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Show submitted data' })).not.toBeInTheDocument()
    expect(screen.getByText('Submitted data is restricted to the Auditor and Admin roles.')).toBeInTheDocument()
    expect(requested).not.toContain('/api/v1/executions/x1/input')
  })

  it('sees its own name and roles, and can sign out', async () => {
    const signOut = vi.fn()
    setSession({ mode: 'Development', me: viewer, getToken: async () => 'dev:dev.viewer', signOut })
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/'] })} />)

    expect(await screen.findByText('Vera Viewer')).toBeInTheDocument()
    expect(screen.getByText(/Viewer · development sign-in/)).toBeInTheDocument()
    await userEvent.setup().click(screen.getByRole('button', { name: 'Sign out' }))
    expect(signOut).toHaveBeenCalled()
  })
})
