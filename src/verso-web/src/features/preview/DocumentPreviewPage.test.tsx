import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it, vi } from 'vitest'

import { routes } from '@/app/routes'

import { parseData } from './DocumentPreviewPage'

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
const item = (over: object = {}) => ({ id: 'a1', key: 'INV-STD', name: 'Commercial invoice', documentType: 'Invoice', latestPublishedVersion: 2, hasDraft: true, updatedAt: '2026-11-09T08:00:00+00:00', categoryId: null, categoryName: null, ...over })
const draft = { versionId: 'v1', templateId: 'a1', status: 'Draft', sampleData: { invoice: { number: 'INV-1' } }, layout: {}, dataSchema: {}, pageSettings: {} }

function stubApi(items: object[], issues: object[] = []) {
  const posts: unknown[] = []
  const fetchMock = vi.fn(async (url: string, init: RequestInit = {}) => {
    if (url === '/api/health') return json({ status: 'Healthy', totalDurationMs: 1, checks: [] })
    if (url.startsWith('/api/v1/templates?')) return json({ items, page: 1, pageSize: 100, total: items.length })
    if (url === '/api/v1/templates/a1/draft') return json(draft)
    if (url === '/api/v1/templates/a1/draft/preview/html') {
      posts.push(init.body ? JSON.parse(String(init.body)) : null)
      return json({ html: '<p>Rendered</p>', issues })
    }
    return json({ title: 'Not found' }, 404)
  })
  vi.stubGlobal('fetch', fetchMock)
  return { fetchMock, posts }
}

const open = () => render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/preview'] })} />)

describe('Document preview', () => {
  it('renders the first template with its sample data and shows the details', async () => {
    const { posts } = stubApi([item()])
    open()

    expect(await screen.findByTitle('Document preview')).toHaveAttribute('srcdoc', '<p>Rendered</p>')
    expect(posts[0]).toBeNull() // template sample data, nothing supplied
    expect(screen.getByLabelText('Data (JSON)')).toHaveValue(JSON.stringify(draft.sampleData, null, 2))
    expect(screen.getByText('Every field in the template found a value in the data.')).toBeInTheDocument()
    expect(screen.getByRole('radio', { name: 'HTML' })).toBeChecked()
  })

  it('lists render issues', async () => {
    stubApi([item()], [{ severity: 'warning', code: 'binding.missing', message: 'No value for invoice.total', elementId: 'e1', path: null }])
    open()

    expect(await screen.findByText('No value for invoice.total')).toBeInTheDocument()
  })

  it('sends edited data, and refuses data that is not a JSON object', async () => {
    const { posts } = stubApi([item()])
    open()
    const user = userEvent.setup()
    const box = await screen.findByLabelText('Data (JSON)')
    await screen.findByTitle('Document preview')

    await user.clear(box)
    await user.click(box)
    await user.paste('[1]')
    expect(screen.getByText(/Use a JSON object/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Render/ })).toBeDisabled()

    await user.clear(box)
    await user.click(box)
    await user.paste('{"invoice":{"number":"X"}}')
    await user.click(screen.getByRole('button', { name: /Render/ }))
    await waitFor(() => expect(posts).toHaveLength(2))
    expect(posts[1]).toEqual({ data: { invoice: { number: 'X' } } })
    expect(screen.getByText('Edited here')).toBeInTheDocument()
  })

  it('says so when no template has a saved draft', async () => {
    stubApi([item({ hasDraft: false })])
    open()

    expect(await screen.findByText(/no template to preview yet/)).toBeInTheDocument()
  })
})

describe('parseData', () => {
  it('accepts objects only', () => {
    expect(parseData('{"a":1}')).toEqual({ ok: true, value: { a: 1 } })
    expect(parseData('[1]').ok).toBe(false)
    expect(parseData('null').ok).toBe(false)
    expect(parseData('{').ok).toBe(false)
  })
})
