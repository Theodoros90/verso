import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it, vi } from 'vitest'

import { routes } from '@/app/routes'

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

const summary = (over: Record<string, unknown>) => ({
  id: 'w1', key: 'invoice-standard', name: 'Invoice standard', latestPublishedVersion: 1, hasDraft: false, updatedAt: '2026-11-09T08:00:00Z', isActive: true, ...over,
})

/** A tiny in-memory API: the lists reflect the state changes the page makes. */
function stubApi(initial: Record<string, unknown>[]) {
  let workflows = initial.map((w) => ({ ...w }))
  const posts: string[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init: RequestInit = {}) => {
      if (url === '/api/health') return json({ status: 'Healthy', totalDurationMs: 1, checks: [] })
      if (url.startsWith('/api/v1/workflows?')) {
        const inactive = url.includes('inactive=true')
        const items = workflows.filter((w) => w.isActive !== inactive)
        return json({ items, page: 1, pageSize: 100, total: items.length })
      }
      const match = /^\/api\/v1\/workflows\/([^/]+)\/(deactivate|activate)$/.exec(url)
      if (match && init.method === 'POST') {
        const [, id, action] = match
        posts.push(`${action} ${id}`)
        workflows = workflows.map((w) => (w.id === id ? { ...w, isActive: action === 'activate' } : w))
        return json({ id })
      }
      return json({ title: 'Not found' }, 404)
    }),
  )
  return posts
}

function open() {
  render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/workflows'] })} />)
}

describe('Workflow list: deactivate', () => {
  it('asks before deactivating, moves the workflow to the deactivated list, and activates it from there', async () => {
    const posts = stubApi([summary({}), summary({ id: 'w2', key: 'other', name: 'Other flow' })])
    open()
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Actions for Invoice standard' }))
    await user.click(await screen.findByRole('menuitem', { name: /Deactivate/ }))
    const dialog = await screen.findByRole('dialog', { name: /Deactivate "Invoice standard"/ })
    expect(dialog).toHaveTextContent('Nothing is deleted')
    expect(posts).toEqual([]) // nothing happens until it is confirmed

    await user.click(within(dialog).getByRole('button', { name: 'Deactivate workflow' }))
    await waitFor(() => expect(screen.queryByRole('link', { name: /Invoice standard/ })).not.toBeInTheDocument())
    expect(posts).toEqual(['deactivate w1'])
    expect(screen.getByRole('link', { name: /Other flow/ })).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('Show deactivated')

    await user.click(screen.getByRole('button', { name: 'Show deactivated' }))
    const card = await screen.findByRole('link', { name: /Invoice standard/ })
    expect(within(card).getByText('Inactive')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /New workflow/ })).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Actions for Invoice standard' }))
    await user.click(await screen.findByRole('menuitem', { name: 'Activate' }))
    await waitFor(() => expect(posts).toEqual(['deactivate w1', 'activate w1']))
    await waitFor(() => expect(screen.queryByRole('link', { name: /Invoice standard/ })).not.toBeInTheDocument()) // gone from the deactivated list

    await user.click(screen.getByRole('button', { name: 'Show active list' }))
    expect(await screen.findByRole('link', { name: /Invoice standard/ })).toBeInTheDocument()
  })

  it('does nothing when the deactivation is cancelled', async () => {
    const posts = stubApi([summary({})])
    open()
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Actions for Invoice standard' }))
    await user.click(await screen.findByRole('menuitem', { name: /Deactivate/ }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Cancel' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(posts).toEqual([])
    expect(screen.getByRole('link', { name: /Invoice standard/ })).toBeInTheDocument()
  })
})

describe('Workflow list: duplicate', () => {
  it('copies a workflow under a new key and opens the copy', async () => {
    const bodies: unknown[] = []
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string, init: RequestInit = {}) => {
        if (url === '/api/health') return json({ status: 'Healthy', totalDurationMs: 1, checks: [] })
        if (url.startsWith('/api/v1/workflows?')) return json({ items: [summary({})], page: 1, pageSize: 100, total: 1 })
        if (url === '/api/v1/workflows/w1/duplicate' && init.method === 'POST') {
          bodies.push(JSON.parse(init.body as string))
          return json({ id: 'w9' }, 201)
        }
        return json({ title: 'Not found' }, 404)
      }),
    )
    open()
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Actions for Invoice standard' }))
    await user.click(await screen.findByRole('menuitem', { name: /Duplicate/ }))
    const dialog = await screen.findByRole('dialog', { name: /Duplicate "Invoice standard"/ })
    expect(within(dialog).getByLabelText('Key')).toHaveValue('invoice-standard-copy')
    expect(within(dialog).getByLabelText('Name')).toHaveValue('Copy of Invoice standard')

    await user.clear(within(dialog).getByLabelText('Key'))
    await user.type(within(dialog).getByLabelText('Key'), 'invoice-v2')
    await user.click(within(dialog).getByRole('button', { name: 'Duplicate' }))

    await waitFor(() => expect(bodies).toEqual([{ key: 'invoice-v2', name: 'Copy of Invoice standard' }]))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('shows a taken key next to the field', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string, init: RequestInit = {}) => {
        if (url === '/api/health') return json({ status: 'Healthy', totalDurationMs: 1, checks: [] })
        if (url.startsWith('/api/v1/workflows?')) return json({ items: [summary({})], page: 1, pageSize: 100, total: 1 })
        if (url.endsWith('/duplicate') && init.method === 'POST') return json({ title: 'Conflict', status: 409, code: 'workflow.key_taken' }, 409)
        return json({ title: 'Not found' }, 404)
      }),
    )
    open()
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Actions for Invoice standard' }))
    await user.click(await screen.findByRole('menuitem', { name: /Duplicate/ }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Duplicate' }))

    expect(await screen.findByText('A workflow with this key already exists.')).toBeInTheDocument()
  })
})
