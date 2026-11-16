import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it, vi } from 'vitest'

import { routes } from '@/app/routes'

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

const cats = [
  { id: 'c1', name: 'Invoices', isActive: true, templateCount: 1 },
  { id: 'c2', name: 'Old <b>stuff</b>', isActive: false, templateCount: 1 },
]
const item = (over: object = {}) => ({ id: 'a1', key: 'INV-STD', name: 'Commercial invoice', documentType: 'Invoice', latestPublishedVersion: 2, hasDraft: true, updatedAt: '2026-11-09T08:00:00+00:00', categoryId: null, categoryName: null, ...over })

function stubApi(onCreate: (body: unknown) => Response, opts: { categoriesFail?: boolean; items?: object[] } = {}) {
  const fetchMock = vi.fn(async (url: string, init: RequestInit = {}) => {
    if (url === '/api/health') return json({ status: 'Healthy', totalDurationMs: 1, checks: [] })
    if (url.startsWith('/api/v1/document-categories')) {
      if (opts.categoriesFail) return json({ title: 'Boom' }, 500)
      return json(url.includes('includeInactive=true') ? cats : cats.filter((c) => c.isActive))
    }
    if (url.startsWith('/api/v1/templates?'))
      return json({
        items: opts.items ?? [item()],
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
  it('new template dialog has an optional Category select defaulting to None', async () => {
    stubApi(() => json({}, 500))
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/templates'] })} />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: /New template/ }))
    const select = screen.getByLabelText('Category (optional)') as HTMLSelectElement
    expect(select.value).toBe('')
    expect(select.selectedOptions[0]).toHaveTextContent('None')
    await waitFor(() => expect(within(select).getByRole('option', { name: 'Invoices' })).toBeInTheDocument())
  })

  it('deactivated categories are not offered', async () => {
    stubApi(() => json({}, 500))
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/templates'] })} />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: /New template/ }))
    const select = screen.getByLabelText('Category (optional)')
    await waitFor(() => expect(within(select).getByRole('option', { name: 'Invoices' })).toBeInTheDocument())
    expect(within(select).queryByRole('option', { name: /Old/ })).toBeNull()
    expect(within(screen.getByLabelText('Filter by category')).queryByRole('option', { name: /Old/ })).toBeNull()
  })

  it('creates with the chosen category', async () => {
    const fetchMock = stubApi(() => json({ id: 'new-id' }, 201))
    const router = createMemoryRouter(routes, { initialEntries: ['/templates'] })
    render(<RouterProvider router={router} />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: /New template/ }))
    await user.type(screen.getByLabelText('Key'), 'inv-new')
    await user.type(screen.getByLabelText('Name'), 'New invoice')
    const select = screen.getByLabelText('Category (optional)')
    await waitFor(() => expect(within(select).getByRole('option', { name: 'Invoices' })).toBeInTheDocument())
    await user.selectOptions(select, 'Invoices')
    await user.click(screen.getByRole('button', { name: 'Create' }))
    await waitFor(() => expect(router.state.location.pathname).toBe('/templates/new-id'))
    const post = fetchMock.mock.calls.find(([, init]) => (init as RequestInit | undefined)?.method === 'POST')!
    expect(JSON.parse(String((post[1] as RequestInit).body))).toMatchObject({ key: 'INV-NEW', categoryId: 'c1' })
  })

  it('shows a category chip on the card, as text', async () => {
    stubApi(() => json({}, 500), { items: [item({ categoryId: 'c2', categoryName: 'Old <b>stuff</b>' })] })
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/templates'] })} />)
    const link = within(await screen.findByRole('list', { name: 'Templates' })).getByRole('link', { name: /Commercial invoice/ })
    expect(within(link).getByText('Old <b>stuff</b>')).toBeInTheDocument()
    expect(link.querySelector('b')).toBeNull()
  })

  it('filter by category and Uncategorized', async () => {
    const fetchMock = stubApi(() => json({}, 500))
    const router = createMemoryRouter(routes, { initialEntries: ['/templates'] })
    render(<RouterProvider router={router} />)
    const user = userEvent.setup()
    const filter = await screen.findByLabelText('Filter by category')
    await waitFor(() => expect(within(filter).getByRole('option', { name: 'Invoices' })).toBeInTheDocument())
    await user.selectOptions(filter, 'Invoices')
    await waitFor(() => expect(fetchMock.mock.calls.some(([u]) => String(u).includes('categoryId=c1'))).toBe(true))
    expect(router.state.location.search).toBe('?category=c1')
    await user.selectOptions(screen.getByLabelText('Filter by category'), 'Uncategorized')
    await waitFor(() => expect(fetchMock.mock.calls.some(([u]) => String(u).includes('categoryId=none'))).toBe(true))
  })

  it('still lists templates and creates without a category when the categories request fails', async () => {
    stubApi(() => json({}, 500), { categoriesFail: true })
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/templates'] })} />)
    const user = userEvent.setup()
    expect(await screen.findByRole('link', { name: /Commercial invoice/ })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: /New template/ }))
    expect(screen.getByLabelText('Category (optional)')).toBeInTheDocument()
  })
})
