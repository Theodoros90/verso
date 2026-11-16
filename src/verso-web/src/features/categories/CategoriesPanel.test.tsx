import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { setSession } from '@/lib/auth'
import { json, openConfiguration } from '@/test/configuration'

interface Cat {
  id: string
  name: string
  isActive: boolean
  templateCount: number
}

/** A tiny in-memory categories API. */
function categoriesApi(initial: Cat[], onCreate?: (name: string) => Response | undefined) {
  const cats = [...initial]
  const calls: string[] = []
  const handler = (url: string, init?: RequestInit) => {
    if (!url.startsWith('/api/v1/document-categories')) return undefined
    const method = init?.method ?? 'GET'
    calls.push(`${method} ${url}`)
    if (method === 'GET') {
      const all = url.includes('includeInactive=true')
      return json(cats.filter((c) => all || c.isActive))
    }
    if (method === 'POST' && url.endsWith('/deactivate')) {
      const c = cats.find((x) => url.includes(x.id))!
      c.isActive = false
      return json(c)
    }
    if (method === 'POST' && url.endsWith('/activate')) {
      const c = cats.find((x) => url.includes(x.id))!
      c.isActive = true
      return json(c)
    }
    if (method === 'POST') {
      const name = JSON.parse(String(init!.body)).name as string
      const custom = onCreate?.(name)
      if (custom) return custom
      const c = { id: `c-${cats.length + 1}`, name, isActive: true, templateCount: 0 }
      cats.push(c)
      return json(c, 201)
    }
    if (method === 'PUT') {
      const c = cats.find((x) => url.includes(x.id))!
      c.name = JSON.parse(String(init!.body)).name
      return json(c)
    }
    return undefined
  }
  return { handler, calls }
}

const seed = (): Cat[] => [
  { id: 'c-1', name: 'Invoices', isActive: true, templateCount: 3 },
  { id: 'c-2', name: 'Statements', isActive: true, templateCount: 1 },
  { id: 'c-3', name: 'Legacy', isActive: false, templateCount: 0 },
]

describe('Document categories tab', () => {
  it('lists categories with template counts', async () => {
    const drawer = await openConfiguration('Document categories', categoriesApi(seed()).handler)

    const row = (await within(drawer).findByText('Invoices')).closest('li')!
    expect(within(row).getByText('3 templates')).toBeInTheDocument()
    expect(within(drawer).getByText('1 template')).toBeInTheDocument()
    expect(within(drawer).queryByText('Legacy')).not.toBeInTheDocument()

    await userEvent.click(within(drawer).getByRole('checkbox', { name: 'Show inactive' }))
    const legacy = (await within(drawer).findByText('Legacy')).closest('li')!
    expect(within(legacy).getByText('Inactive')).toBeInTheDocument()
    expect(within(legacy).getByRole('button', { name: 'Activate Legacy' })).toBeInTheDocument()
  })

  it('adds a category and shows it', async () => {
    const api = categoriesApi(seed())
    const drawer = await openConfiguration('Document categories', api.handler)
    await within(drawer).findByText('Invoices')

    await userEvent.type(within(drawer).getByLabelText('New category name'), 'Credit notes')
    await userEvent.click(within(drawer).getByRole('button', { name: 'Add category' }))

    expect(await within(drawer).findByText('Credit notes')).toBeInTheDocument()
    expect(within(drawer).getByLabelText('New category name')).toHaveValue('')
    expect(api.calls).toContain('POST /api/v1/document-categories')
  })

  it('shows the name-taken error under the field', async () => {
    const api = categoriesApi(seed(), () => json({ code: 'category.name_taken', title: 'Name taken' }, 409))
    const drawer = await openConfiguration('Document categories', api.handler)
    await within(drawer).findByText('Invoices')

    await userEvent.type(within(drawer).getByLabelText('New category name'), 'Invoices')
    await userEvent.click(within(drawer).getByRole('button', { name: 'Add category' }))

    expect(await within(drawer).findByText('A category with this name already exists.')).toBeInTheDocument()
    expect(within(drawer).getByLabelText('New category name')).toHaveAttribute('aria-invalid', 'true')
  })

  it('renames a category', async () => {
    const drawer = await openConfiguration('Document categories', categoriesApi(seed()).handler)
    await within(drawer).findByText('Invoices')

    await userEvent.click(within(drawer).getByRole('button', { name: 'Rename Invoices' }))
    const dialog = screen.getByRole('dialog', { name: 'Rename category' })
    const input = within(dialog).getByLabelText('Name')
    await userEvent.clear(input)
    await userEvent.type(input, 'Sales invoices')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))

    expect(await within(drawer).findByText('Sales invoices')).toBeInTheDocument()
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Rename category' })).not.toBeInTheDocument())
  })

  it('deactivate asks first, then moves it to the inactive list', async () => {
    const api = categoriesApi(seed())
    const drawer = await openConfiguration('Document categories', api.handler)
    await within(drawer).findByText('Statements')

    await userEvent.click(within(drawer).getByRole('button', { name: 'Deactivate Statements' }))
    const dialog = screen.getByRole('dialog', { name: 'Deactivate category' })
    expect(api.calls.some((c) => c.endsWith('/deactivate'))).toBe(false) // nothing sent before the confirmation
    await userEvent.click(within(dialog).getByRole('button', { name: 'Deactivate' }))

    await waitFor(() => expect(within(drawer).queryByText('Statements')).not.toBeInTheDocument())
    expect(api.calls.some((c) => c.endsWith('/c-2/deactivate'))).toBe(true)
    await userEvent.click(within(drawer).getByRole('checkbox', { name: 'Show inactive' }))
    expect(await within(drawer).findByText('Statements')).toBeInTheDocument()
  })

  it('hides write controls without template authoring', async () => {
    setSession({
      mode: 'Development',
      me: { id: 'dev.viewer', name: 'Vera Viewer', roles: ['Viewer'], permissions: { read: true, templateAuthoring: false, workflowAuthoring: false, operate: false, readPayload: false, readAudit: false, administer: false } },
      getToken: async () => 'dev:dev.viewer',
      signOut: () => undefined,
    })
    const drawer = await openConfiguration('Document categories', categoriesApi(seed()).handler)

    expect(await within(drawer).findByText('Invoices')).toBeInTheDocument()
    expect(within(drawer).queryByLabelText('New category name')).not.toBeInTheDocument()
    expect(within(drawer).queryByRole('button', { name: /^(Add|Rename|Deactivate|Activate)/ })).not.toBeInTheDocument()
  })

  it('renders a name with <script> as text', async () => {
    const name = '<script>alert(1)</script>'
    const drawer = await openConfiguration('Document categories', categoriesApi([{ id: 'c-9', name, isActive: true, templateCount: 0 }]).handler)

    expect(await within(drawer).findByText(name)).toBeInTheDocument()
    expect(drawer.querySelector('script')).toBeNull()
  })
})
