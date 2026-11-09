import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it, vi } from 'vitest'

import { routes } from '@/app/routes'

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

function stubApi(onCreate: (body: unknown) => Response) {
  const fetchMock = vi.fn(async (url: string, init: RequestInit = {}) => {
    if (url === '/api/health') return json({ status: 'Healthy', totalDurationMs: 1, checks: [] })
    if (url.startsWith('/api/v1/templates?'))
      return json({
        items: [{ id: 'a1', key: 'INV-STD', name: 'Commercial invoice', documentType: 'Invoice', latestPublishedVersion: 2, hasDraft: true, updatedAt: '2026-11-09T08:00:00+00:00' }],
        page: 1,
        pageSize: 100,
        total: 1,
      })
    if (url === '/api/v1/templates' && init.method === 'POST') return onCreate(JSON.parse(String(init.body)))
    return json({ title: 'Not found' }, 404)
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

describe('Template list', () => {
  it('lists templates with their published version and draft status', async () => {
    stubApi(() => json({}, 500))
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/templates'] })} />)

    const list = await screen.findByRole('list', { name: 'Templates' })
    const link = within(list).getByRole('link', { name: /Commercial invoice/ })
    expect(link).toHaveAttribute('href', '/templates/a1')
    expect(within(link).getByText('v2')).toBeInTheDocument()
    expect(within(link).getByText('Draft')).toBeInTheDocument()
  })

  it('creates a template and opens it in the designer; a taken key is shown on the field', async () => {
    let attempt = 0
    const fetchMock = stubApi(() => (++attempt === 1 ? json({ code: 'template.key_taken', title: 'Key taken' }, 409) : json({ id: 'new-id' }, 201)))
    const router = createMemoryRouter(routes, { initialEntries: ['/templates'] })
    render(<RouterProvider router={router} />)
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: /New template/ }))
    await user.type(screen.getByLabelText('Key'), 'inv-new')
    await user.type(screen.getByLabelText('Name'), 'New invoice')
    await user.click(screen.getByRole('button', { name: 'Create' }))
    expect(await screen.findByText('A template with this key already exists.')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Create' }))
    await waitFor(() => expect(router.state.location.pathname).toBe('/templates/new-id'))
    const post = fetchMock.mock.calls.find(([, init]) => (init as RequestInit | undefined)?.method === 'POST')!
    expect(JSON.parse(String((post[1] as RequestInit).body))).toEqual({ key: 'INV-NEW', name: 'New invoice', description: null, documentType: 'Invoice' })
  })
})
