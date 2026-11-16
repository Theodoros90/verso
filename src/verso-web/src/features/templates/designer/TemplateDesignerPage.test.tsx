import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { routes } from '@/app/routes'
import { setSession } from '@/lib/auth'
import { testAdmin } from '@/test/setup'
import type { TemplateDto, TemplateVersionDto } from '@/lib/api/generated'

import layoutRaw from '../../../../../../tests/fixtures/invoice/layout.json?raw'
import sampleRaw from '../../../../../../tests/fixtures/invoice/sample.json?raw'
import { inferSchema } from '../model/schema'

const TEMPLATE_ID = '11111111-1111-1111-1111-111111111111'
const VERSION_ID = '22222222-2222-2222-2222-222222222222'

const template = (over: Partial<TemplateDto> = {}): TemplateDto => ({
  id: TEMPLATE_ID,
  key: 'INV-STD',
  name: 'Commercial invoice',
  description: null,
  documentType: 'Invoice',
  createdAt: '2026-11-09T08:00:00+00:00',
  createdBy: 'dev.user',
  latestPublishedVersion: null,
  draftVersionId: VERSION_ID,
  categoryId: null,
  categoryName: null,
  ...over,
})

const version = (over: Partial<TemplateVersionDto> = {}): TemplateVersionDto => ({
  versionId: VERSION_ID,
  templateId: TEMPLATE_ID,
  versionNumber: null,
  status: 'Draft',
  pageSettings: { size: 'A4', orientation: 'portrait', marginsMm: { top: 18, right: 16, bottom: 18, left: 16 } },
  layout: { layoutSchemaVersion: 1 },
  dataSchema: { type: 'object' },
  sampleData: {},
  contentHash: null,
  createdAt: '2026-11-09T08:00:00+00:00',
  createdBy: 'dev.user',
  updatedAt: '2026-11-09T08:00:00+00:00',
  updatedBy: 'dev.user',
  publishedAt: null,
  publishedBy: null,
  ...over,
})

interface Call {
  method: string
  url: string
  headers: Record<string, string>
  body: unknown
}

/** A tiny in-memory API: GET returns the stored draft, PUT stores it (like the real server). */
function mockApi(initial: TemplateVersionDto, opts: { template?: TemplateDto; putStatus?: number; categoryStatus?: number } = {}) {
  let stored = initial
  let etag = 1
  const calls: Call[] = []
  const json = (body: unknown, status = 200, headers: Record<string, string> = {}) =>
    new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } })

  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init: RequestInit = {}) => {
      const method = init.method ?? 'GET'
      const headers = Object.fromEntries(new Headers(init.headers).entries()) // lower-case names
      const body = init.body ? JSON.parse(String(init.body)) : undefined
      calls.push({ method, url, headers, body })
      if (url === '/api/health') return json({ status: 'Healthy', totalDurationMs: 1, checks: [] })
      if (url.startsWith('/api/v1/document-categories'))
        return json([
          { id: 'c1', name: 'Invoices', isActive: true, templateCount: 1 },
          { id: 'c2', name: 'Statements', isActive: true, templateCount: 0 },
          { id: 'c3', name: 'Retired', isActive: false, templateCount: 1 },
          { id: 'c4', name: 'Also retired', isActive: false, templateCount: 0 },
        ])
      if (url === `/api/v1/templates/${TEMPLATE_ID}/category` && method === 'PUT') {
        if (opts.categoryStatus) return json({ title: 'Category is not available', code: 'category.inactive' }, opts.categoryStatus)
        const id = (body as { categoryId: string | null }).categoryId
        return json({ ...(opts.template ?? template()), categoryId: id, categoryName: id === 'c2' ? 'Statements' : null })
      }
      if (url === `/api/v1/templates/${TEMPLATE_ID}`) return json(opts.template ?? template())
      if (url === `/api/v1/templates/${TEMPLATE_ID}/draft` && method === 'GET') return json(stored, 200, { ETag: `"e${etag}"` })
      if (url === `/api/v1/templates/${TEMPLATE_ID}/draft` && method === 'PUT') {
        if (opts.putStatus) return json({ title: 'Precondition failed', code: 'precondition.failed' }, opts.putStatus)
        stored = { ...stored, ...(body as object) }
        etag++
        return json(stored, 200, { ETag: `"e${etag}"` })
      }
      if (url === `/api/v1/templates/${TEMPLATE_ID}/versions`)
        return json([{ versionId: VERSION_ID, versionNumber: 3, status: 'Published', contentHash: 'abc', updatedAt: '', updatedBy: '', publishedAt: '', publishedBy: '' }])
      if (url === `/api/v1/templates/${TEMPLATE_ID}/versions/${VERSION_ID}`) return json(stored, 200, { ETag: '"p"' })
      return json({ title: 'Not found' }, 404)
    }),
  )
  return { calls, get stored() { return stored } }
}

async function openDesigner() {
  const router = createMemoryRouter(routes, { initialEntries: [`/templates/${TEMPLATE_ID}`] })
  render(<RouterProvider router={router} />)
  await screen.findByRole('toolbar', { name: 'Designer' })
  return router
}

const invoiceLayout = JSON.parse(layoutRaw) as Record<string, unknown>
const sample = JSON.parse(sampleRaw) as Record<string, unknown>

// Full-page integration tests: many user-event steps over a large DOM; slow in jsdom when the whole suite runs in parallel.
describe('Template designer', { timeout: 20_000 }, () => {
  beforeEach(() => {
    vi.spyOn(window, 'confirm').mockReturnValue(true)
  })

  it('round-trips an unchanged invoice exactly: what it saves is what it loaded', async () => {
    const api = mockApi(version({ layout: invoiceLayout, dataSchema: inferSchema(sample), sampleData: sample }))
    const user = userEvent.setup()
    await openDesigner()

    // Make a change and undo it, so there is something to save only after a real edit.
    await user.click(screen.getByRole('treeitem', { name: /Invoice no\.|inv-number|invoice\.number/ }))
    await user.click(screen.getByRole('button', { name: 'Bold' }))
    await user.click(screen.getByRole('button', { name: 'Undo' }))
    expect(screen.getByRole('button', { name: /Save draft/ })).toBeDisabled()

    await user.click(screen.getByRole('button', { name: 'Bold' }))
    await user.click(screen.getByRole('button', { name: 'Bold' }))
    await user.click(screen.getByRole('button', { name: /Save draft/ }))

    const put = await waitFor(() => {
      const c = api.calls.find((x) => x.method === 'PUT')
      if (!c) throw new Error('no PUT yet')
      return c
    })
    expect(put.headers['if-match']).toBe('"e1"')
    expect(put.body).toEqual({
      pageSettings: { size: 'A4', orientation: 'portrait', marginsMm: { top: 18, right: 16, bottom: 18, left: 16 } },
      layout: invoiceLayout,
      dataSchema: inferSchema(sample),
      sampleData: sample,
    })
  })

  it('builds from an empty template: import sample data, add fields and a table, publish only when there are no errors', async () => {
    const api = mockApi(version())
    const user = userEvent.setup()
    await openDesigner()

    // An unmapped field blocks publishing.
    await user.click(screen.getByRole('button', { name: 'Add Data field' }))
    expect(screen.getByText('1 unmapped field')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Publish/ })).toBeDisabled()
    await user.keyboard('{Escape}')
    const tree = screen.getByRole('tree', { name: 'Elements' })
    await user.click(within(tree).getByRole('treeitem', { name: /unmapped field/ }))
    await user.click(screen.getByRole('button', { name: /Delete/ }))

    // Import synthetic sample data; the data model is derived from it.
    const file = new File([sampleRaw], 'sample.json', { type: 'application/json' })
    await user.upload(screen.getByLabelText('Sample data file'), file)
    await screen.findByRole('button', { name: 'Add field invoice.number' })

    await user.click(screen.getByRole('button', { name: 'Add field invoice.number' }))
    await user.click(screen.getByRole('button', { name: 'Add table for lines' }))
    expect(within(tree).getByRole('treeitem', { name: /invoice\.number/ })).toBeInTheDocument()
    expect(within(tree).getByRole('treeitem', { name: /lines\[ \]/ })).toBeInTheDocument()
    expect(screen.getByText('All fields mapped')).toBeInTheDocument()

    // Add a header title with the palette while the header is selected.
    await user.click(screen.getByRole('button', { name: 'Add header' }))
    await user.click(screen.getByRole('button', { name: 'Add Text' }))
    const text = screen.getByLabelText('Text')
    await user.clear(text)
    await user.type(text, 'Invoice')

    const publish = screen.getByRole('button', { name: /Publish/ })
    expect(publish).toBeEnabled()
    await user.click(publish)
    await waitFor(() => expect(api.calls.some((c) => c.url.endsWith('/draft/publish'))).toBe(true))

    const put = api.calls.find((c) => c.method === 'PUT')!
    const layout = (put.body as { layout: { header: { elements: { props: unknown }[] }; body: { blocks: { id: string; type: string; props: Record<string, unknown> }[] } } }).layout
    expect(layout.header.elements[0]!.props).toEqual({ text: 'Invoice' })
    expect(layout.body.blocks.map((b) => [b.id, b.type])).toEqual([
      ['number', 'field'],
      ['lines', 'table'],
    ])
    expect(layout.body.blocks[0]!.props).toEqual({ binding: 'invoice.number', label: 'Number' })
    expect((layout.body.blocks[1]!.props.columns as unknown[]).length).toBe(6)
    const publishCall = api.calls.find((c) => c.url.endsWith('/draft/publish'))!
    expect(publishCall.headers['if-match']).toBe('"e2"')
  })

  it('selects elements with the keyboard in the element tree and deletes / undoes', async () => {
    mockApi(version({ layout: invoiceLayout, dataSchema: inferSchema(sample), sampleData: sample }))
    const user = userEvent.setup()
    await openDesigner()

    const tree = screen.getByRole('tree', { name: 'Elements' })
    const header = within(tree).getByRole('treeitem', { name: /^Header/ })
    header.focus()
    await user.keyboard('{ArrowDown}')
    await waitFor(() => expect(within(tree).getByRole('treeitem', { selected: true })).toHaveTextContent('Image'))
    await user.keyboard('{ArrowDown}{ArrowDown}')
    await waitFor(() => expect(within(tree).getByRole('treeitem', { selected: true })).toHaveTextContent('Invoice'))
    expect(screen.getByRole('heading', { name: 'Text', level: 3 })).toBeInTheDocument()

    await user.keyboard('{Delete}')
    expect(within(tree).queryByRole('treeitem', { name: /^Invoice/ })).not.toBeInTheDocument()
    await user.keyboard('{Control>}z{/Control}')
    expect(within(tree).getByRole('treeitem', { name: /^Invoice/ })).toBeInTheDocument()
  })

  it('moves a header element with arrow keys on the canvas', async () => {
    const api = mockApi(version({ layout: invoiceLayout, dataSchema: inferSchema(sample), sampleData: sample }))
    const user = userEvent.setup()
    await openDesigner()

    const title = screen.getByRole('button', { name: 'Text: Invoice' })
    title.focus()
    await user.keyboard('{ArrowDown}{ArrowDown}{Shift>}{ArrowLeft}{/Shift}')
    expect(screen.getByLabelText('Y (mm)')).toHaveValue(2)
    expect(screen.getByLabelText('X (mm)')).toHaveValue(113)
    await user.click(screen.getByRole('button', { name: /Save draft/ }))
    await waitFor(() => expect(api.calls.some((c) => c.method === 'PUT')).toBe(true))
    const saved = api.stored.layout as { header: { elements: { id: string; xMm: number; yMm: number }[] } }
    expect(saved.header.elements.find((e) => e.id === 'title')).toMatchObject({ xMm: 113, yMm: 2 })
  })

  it('sets which pages the header and footer print on, stores it, and drops it again for "every page"', async () => {
    const api = mockApi(version({ layout: invoiceLayout, dataSchema: inferSchema(sample), sampleData: sample }))
    const user = userEvent.setup()
    await openDesigner()

    const [headerRule, footerRule] = screen.getAllByLabelText('Show on')
    expect(headerRule).toHaveValue('all')
    await user.selectOptions(headerRule!, 'first')
    await user.selectOptions(footerRule!, 'last')
    await user.click(screen.getByRole('button', { name: /Save draft/ }))
    await waitFor(() => expect(api.calls.some((c) => c.method === 'PUT')).toBe(true))
    const saved = (api.calls.find((c) => c.method === 'PUT')!.body as { layout: { header: { showOn?: string }; footer: { showOn?: string } } }).layout
    expect([saved.header.showOn, saved.footer.showOn]).toEqual(['first', 'last'])

    await user.selectOptions(screen.getAllByLabelText('Show on')[0]!, 'all')
    await user.click(screen.getByRole('button', { name: /Save draft/ }))
    await waitFor(() => expect(api.calls.filter((c) => c.method === 'PUT')).toHaveLength(2))
    const again = (api.calls.filter((c) => c.method === 'PUT')[1]!.body as { layout: { header: object } }).layout
    expect(again.header).not.toHaveProperty('showOn') // "every page" is the default and is not written
  })

  it('shows a conflict message when someone else saved first (412)', async () => {
    mockApi(version({ layout: invoiceLayout, dataSchema: inferSchema(sample), sampleData: sample }), { putStatus: 412 })
    const user = userEvent.setup()
    await openDesigner()
    screen.getByRole('button', { name: 'Text: Invoice' }).focus()
    await user.keyboard('{ArrowRight}')
    await user.click(screen.getByRole('button', { name: /Save draft/ }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Someone else saved this draft')
    expect(screen.getByRole('button', { name: 'Reload' })).toBeInTheDocument()
  })

  it('opens the latest published version read-only when there is no draft', async () => {
    mockApi(version({ status: 'Published', versionNumber: 3, layout: invoiceLayout, dataSchema: inferSchema(sample) }), {
      template: template({ draftVersionId: null, latestPublishedVersion: 3 }),
    })
    await openDesigner()
    expect(screen.getByText('Published v3')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Start new draft/ })).toBeEnabled()
    expect(screen.queryByRole('button', { name: /Save draft/ })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Add Text' })).toBeDisabled()
  })
  it('shows the current category and changes it', async () => {
    const api = mockApi(version(), { template: template({ categoryId: 'c1', categoryName: 'Invoices' }) })
    const user = userEvent.setup()
    await openDesigner()
    const select = (await screen.findByLabelText('Category')) as HTMLSelectElement
    expect(select.value).toBe('c1')
    await waitFor(() => expect(within(select).getByRole('option', { name: 'Statements' })).toBeInTheDocument())
    expect(within(select).queryByRole('option', { name: /Also retired/ })).toBeNull()
    await user.selectOptions(select, 'Statements')
    await waitFor(() => expect(api.calls.find((c) => c.method === 'PUT' && c.url.endsWith('/category'))?.body).toEqual({ categoryId: 'c2' }))
    await waitFor(() => expect((screen.getByLabelText('Category') as HTMLSelectElement).value).toBe('c2'))
    await user.selectOptions(screen.getByLabelText('Category'), 'No category')
    await waitFor(() => expect(api.calls.filter((c) => c.url.endsWith('/category')).at(-1)?.body).toEqual({ categoryId: null }))
  })

  it('a template with a deactivated category still shows it', async () => {
    mockApi(version(), { template: template({ categoryId: 'c3', categoryName: 'Retired' }) })
    await openDesigner()
    const select = (await screen.findByLabelText('Category')) as HTMLSelectElement
    expect(select.selectedOptions[0]).toHaveTextContent('Retired (inactive)')
    await waitFor(() => expect(within(select).getByRole('option', { name: 'Statements' })).toBeInTheDocument())
    expect(select.value).toBe('c3')
    expect(within(select).queryByRole('option', { name: /Also retired/ })).toBeNull()
  })
  it('shows an error and keeps the previous category when changing it fails', async () => {
    mockApi(version(), { template: template({ categoryId: 'c1', categoryName: 'Invoices' }), categoryStatus: 422 })
    const user = userEvent.setup()
    await openDesigner()
    const select = (await screen.findByLabelText('Category')) as HTMLSelectElement
    await waitFor(() => expect(within(select).getByRole('option', { name: 'Statements' })).toBeInTheDocument())
    await user.selectOptions(select, 'Statements')
    expect(await screen.findByRole('alert')).toHaveTextContent('Category is not available')
    expect((screen.getByLabelText('Category') as HTMLSelectElement).value).toBe('c1')
  })

  it('shows the category as plain text, without a select, to a user without template authoring', async () => {
    setSession({
      mode: 'Development',
      me: { ...testAdmin, id: 'dev.viewer', roles: ['Viewer'], permissions: { ...testAdmin.permissions, templateAuthoring: false } },
      getToken: async () => 'dev:dev.viewer',
      signOut: () => undefined,
    })
    mockApi(version(), { template: template({ categoryId: 'c1', categoryName: 'Invoices' }) })
    await openDesigner()
    expect(await screen.findByText('Invoices')).toBeInTheDocument()
    expect(screen.queryByRole('combobox', { name: 'Category' })).toBeNull()
  })
})
