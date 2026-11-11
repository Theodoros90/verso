import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { routes } from '@/app/routes'
import type { AdminFolderLocationDto, MeDto } from '@/lib/api/generated'
import { setSession } from '@/lib/auth'

vi.mock('@microsoft/signalr', async () => (await import('@/test/fakeHub')).fakeSignalR)

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

const me = (administer: boolean): MeDto => ({
  id: administer ? 'dev.admin' : 'dev.auditor',
  name: 'Someone',
  roles: [administer ? 'Admin' : 'Auditor'],
  permissions: { read: true, templateAuthoring: administer, workflowAuthoring: administer, operate: administer, readPayload: true, readAudit: true, administer },
})

const loc = (over: Partial<AdminFolderLocationDto>): AdminFolderLocationDto => ({
  key: 'ap-inbound', name: 'AP inbound', path: '\\\\fileserver\\verso\\ap-inbound', input: true, output: false,
  managed: true, enabled: true, problem: null, updatedAt: '', updatedBy: 'dev.admin', ...over,
})

interface Call {
  method: string
  url: string
  body: unknown
}

function mockApi(initial: AdminFolderLocationDto[], opts: { saveError?: boolean } = {}) {
  const calls: Call[] = []
  let items = initial
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init: RequestInit = {}) => {
      const method = init.method ?? 'GET'
      const body = init.body ? JSON.parse(String(init.body)) : undefined
      calls.push({ method, url, body })
      if (url === '/api/health') return json({ status: 'Healthy', totalDurationMs: 1, checks: [] })
      if (url === '/api/v1/admin/folder-locations' && method === 'GET') return json(items)
      if (url.startsWith('/api/v1/admin/folder-locations/') && method === 'PUT') {
        if (opts.saveError) return json({ title: 'Bad', code: 'validation', errors: { path: ['Must be absolute.'] } }, 400)
        const key = decodeURIComponent(url.split('/').pop()!)
        items = [...items.filter((l) => l.key !== key), loc({ key, name: body.name, path: body.path, input: body.input, output: body.output, enabled: false, problem: 'Waiting for the Worker to pick it up (about 15 seconds).' })]
        return json(items.find((l) => l.key === key))
      }
      if (url.startsWith('/api/v1/admin/folder-locations/') && method === 'DELETE') {
        items = items.filter((l) => l.key !== decodeURIComponent(url.split('/').pop()!))
        return new Response(null, { status: 204 })
      }
      if (url === '/api/v1/folder-access-tests' && method === 'POST') {
        return json({ id: 't1', status: 'Queued', targets: [], results: [], identity: null, machine: null, error: null, requestedAt: '', completedAt: null }, 202)
      }
      if (url === '/api/v1/folder-access-tests/t1') {
        return json({
          id: 't1', status: 'Completed', targets: [], identity: 'CORP\\svc-verso-worker$', machine: 'VERSO-WRK01', error: null, requestedAt: '', completedAt: '',
          results: [{ role: 'source', location: 'ap-inbound', subfolder: '', ok: true, checks: [{ name: 'folder', ok: true, message: 'The folder exists and can be listed.' }] }],
        })
      }
      return json({ title: 'Not found' }, 404)
    }),
  )
  return calls
}

function open() {
  render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/folder-locations'] })} />)
}

describe('Folder locations (administration)', { timeout: 20_000 }, () => {
  beforeEach(() => {
    setSession({ mode: 'Development', me: me(true), getToken: async () => 'dev:dev.admin', signOut: () => undefined })
  })

  it('lists locations with their paths, purposes and status; configuration-defined ones are read-only', async () => {
    mockApi([
      loc({}),
      loc({ key: 'ap-results', name: 'AP results', path: null, input: false, output: true, managed: false, enabled: false, problem: 'The path must be absolute.' }),
    ])
    open()

    const row = (await screen.findByText('AP inbound')).closest('tr')!
    expect(within(row).getByText('\\\\fileserver\\verso\\ap-inbound')).toBeInTheDocument()
    expect(within(row).getByText('Source')).toBeInTheDocument()
    expect(within(row).getByText('Ready')).toBeInTheDocument()
    expect(within(row).getByRole('button', { name: 'Edit AP inbound' })).toBeInTheDocument()

    const configured = screen.getByText('AP results').closest('tr')!
    expect(within(configured).getByText('Set in the Worker’s configuration')).toBeInTheDocument()
    expect(within(configured).getByText('The path must be absolute.')).toBeInTheDocument()
    expect(within(configured).queryByRole('button', { name: /Edit|Remove/ })).toBeNull()
  })

  it('adds a location by typing its path', async () => {
    const calls = mockApi([])
    open()

    fireEvent.click(await screen.findByRole('button', { name: 'Add folder location' }))
    const form = screen.getByRole('form', { name: 'Add folder location' })
    fireEvent.change(within(form).getByLabelText(/^Key/), { target: { value: 'ap-inbound' } })
    fireEvent.change(within(form).getByLabelText(/^Name/), { target: { value: 'AP inbound' } })
    fireEvent.change(within(form).getByLabelText(/^Path/), { target: { value: 'D:\\Verso\\inbound' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Save' }))

    await screen.findByText(/Saved 'ap-inbound'/)
    expect(calls.find((c) => c.method === 'PUT')).toEqual({
      method: 'PUT',
      url: '/api/v1/admin/folder-locations/ap-inbound',
      body: { name: 'AP inbound', path: 'D:\\Verso\\inbound', input: true, output: false },
    })
    expect(await screen.findByText(/Waiting for the Worker/)).toBeInTheDocument()
  })

  it('shows the server’s complaint next to the path field', async () => {
    mockApi([], { saveError: true })
    open()

    fireEvent.click(await screen.findByRole('button', { name: 'Add folder location' }))
    const form = screen.getByRole('form', { name: 'Add folder location' })
    fireEvent.change(within(form).getByLabelText(/^Key/), { target: { value: 'ap-inbound' } })
    fireEvent.change(within(form).getByLabelText(/^Name/), { target: { value: 'AP' } })
    fireEvent.change(within(form).getByLabelText(/^Path/), { target: { value: 'relative' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Save' }))

    expect(await within(form).findByText('Must be absolute.')).toBeInTheDocument()
  })

  it('edits a location: the key is fixed, the path can change', async () => {
    const calls = mockApi([loc({})])
    open()

    fireEvent.click(await screen.findByRole('button', { name: 'Edit AP inbound' }))
    const form = screen.getByRole('form', { name: 'Edit folder location' })
    expect(within(form).getByLabelText(/^Key/)).toBeDisabled()
    fireEvent.change(within(form).getByLabelText(/^Path/), { target: { value: 'E:\\Moved' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(calls.some((c) => c.method === 'PUT' && (c.body as { path: string }).path === 'E:\\Moved')).toBe(true))
  })

  it('removes a location after confirmation', async () => {
    const calls = mockApi([loc({})])
    vi.stubGlobal('confirm', vi.fn(() => true))
    open()

    fireEvent.click(await screen.findByRole('button', { name: 'Remove AP inbound' }))

    await screen.findByText("Removed 'ap-inbound'.")
    expect(calls.some((c) => c.method === 'DELETE' && c.url === '/api/v1/admin/folder-locations/ap-inbound')).toBe(true)
  })

  it('tests access through the Worker and shows the identity it used', async () => {
    const calls = mockApi([loc({})])
    open()

    fireEvent.click(await screen.findByRole('button', { name: 'Test access to AP inbound' }))

    const result = await screen.findByRole('list', { name: 'Access test of AP inbound' }, { timeout: 4000 })
    expect(result).toHaveTextContent('The folder exists and can be listed.')
    expect(result).toHaveTextContent('CORP\\svc-verso-worker$')
    expect(calls.find((c) => c.method === 'POST')!.body).toEqual({ targets: [{ role: 'source', location: 'ap-inbound', subfolder: '' }] })
  })
})

describe('Folder locations without the Admin role', () => {
  it('is hidden from the navigation, and opening it directly explains and requests nothing', async () => {
    setSession({ mode: 'Development', me: me(false), getToken: async () => 'dev:dev.auditor', signOut: () => undefined })
    const calls = mockApi([])
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/folder-locations'] })} />)

    expect(await screen.findByText('Folder locations are managed by the Admin role.')).toBeInTheDocument()
    expect(within(screen.getByRole('navigation', { name: 'Main' })).queryByRole('link', { name: 'Folder locations' })).toBeNull()
    expect(calls.some((c) => c.url.startsWith('/api/v1/admin'))).toBe(false)
  })
})
