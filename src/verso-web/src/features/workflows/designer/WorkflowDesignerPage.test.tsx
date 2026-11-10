import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it, vi } from 'vitest'

import { routes } from '@/app/routes'
import type { NodeTypeDto, WorkflowIssueDto } from '@/lib/api/generated'

const WF = '11111111-1111-1111-1111-111111111111'
const TPL = '22222222-2222-2222-2222-222222222222'

const nodeTypes: NodeTypeDto[] = [
  {
    type: 'api-trigger', label: 'API / webhook', category: 'input', description: 'Starts a run when a system posts JSON to a URL.',
    inputs: [], outputs: [{ id: 'out', label: 'Out', required: true }],
    configSchema: {
      type: 'object',
      properties: {
        path: { type: 'string', title: 'Path', 'x-widget': 'endpoint-path' },
        auth: { type: 'string', title: 'Authentication method', enum: ['none', 'hmac-sha256'], 'x-enumLabels': ['None', 'HMAC SHA-256 signature'] },
        expectedSchema: { type: 'string', title: 'Expected JSON schema', 'x-widget': 'json' },
        samplePayload: { type: 'string', title: 'Sample JSON payload', 'x-widget': 'json' },
      },
      required: ['path', 'auth'],
    },
    defaults: { path: '/hooks/invoices', auth: 'hmac-sha256', expectedSchema: '', samplePayload: '' },
  },
  {
    type: 'erp-connector', label: 'ERP connector', category: 'input', description: 'Reads records from SAP, Dynamics or Oracle.',
    inputs: [], outputs: [{ id: 'out', label: 'Out', required: true }],
    configSchema: {
      type: 'object',
      properties: {
        system: { type: 'string', title: 'System', enum: ['dynamics-365', 'oracle-netsuite'], 'x-enumLabels': ['Microsoft Dynamics 365', 'Oracle NetSuite'] },
        entity: { type: 'string', title: 'Record type' },
        mode: { type: 'string', title: 'Start a run', enum: ['on-post', 'nightly'], 'x-enumLabels': ['When a record is posted', 'Nightly batch'] },
      },
      required: ['system', 'entity', 'mode'],
    },
    defaults: { system: 'dynamics-365', entity: 'Invoices', mode: 'on-post' },
  },
  {
    type: 'validate', label: 'Validate data', category: 'process', description: 'Checks required fields.',
    inputs: [{ id: 'in', label: 'In', required: true }],
    outputs: [{ id: 'valid', label: 'Valid', required: true }, { id: 'invalid', label: 'Invalid', required: false }],
    configSchema: {
      type: 'object',
      properties: {
        required: { type: 'array', title: 'Required fields', items: { type: 'string', title: 'Field', 'x-widget': 'data-path' } },
        types: { type: 'array', title: 'Field type checks', items: { type: 'object', properties: { field: { type: 'string', title: 'Field', 'x-widget': 'data-path' }, type: { type: 'string', title: 'Expected type', enum: ['text', 'date'], 'x-enumLabels': ['Text', 'Date (YYYY-MM-DD)'] } } } },
      },
      required: ['required', 'types'],
    },
    defaults: { required: [], types: [] },
  },
  {
    type: 'generate-pdf', label: 'Generate PDF', category: 'doc', description: 'Renders a template.',
    inputs: [{ id: 'in', label: 'In', required: true }],
    outputs: [{ id: 'success', label: 'Success', required: true }, { id: 'failed', label: 'Failed', required: false }],
    configSchema: {
      type: 'object',
      properties: {
        templateId: { type: 'string', title: 'Template', 'x-widget': 'template' },
        templateVersion: { type: 'string', title: 'Template version', 'x-widget': 'template-version' },
        fileName: { type: 'string', title: 'Output filename pattern', 'x-widget': 'pattern' },
      },
      required: ['templateId', 'templateVersion', 'fileName'],
    },
    defaults: { templateId: '', templateVersion: 'latest-published', fileName: '{{invoice.number}}.pdf' },
  },
]

const definition = {
  nodes: [
    { id: 'trigger', type: 'api-trigger', title: 'ERP webhook', position: { x: 0, y: 80 }, config: { path: '/hooks/invoices', auth: 'hmac-sha256', expectedSchema: JSON.stringify({ type: 'object', required: ['invoice'], properties: { invoice: { type: 'object', required: ['number'] } } }), samplePayload: '' } },
    { id: 'validate', type: 'validate', title: 'Validate invoice data', position: { x: 262.5, y: 80 }, config: { required: ['invoice.number'], types: [] } },
    { id: 'pdf', type: 'generate-pdf', title: 'Generate invoice PDF', position: { x: 520, y: 96 }, config: { templateId: TPL, templateVersion: 'latest-published', fileName: '{{invoice.number}}.pdf' } },
  ],
  edges: [
    { id: 'e1', source: 'trigger', sourcePort: 'out', target: 'validate', targetPort: 'in' },
    { id: 'e2', source: 'validate', sourcePort: 'valid', target: 'pdf', targetPort: 'in' },
  ],
}

const issue = (over: Partial<WorkflowIssueDto>): WorkflowIssueDto => ({ severity: 'error', code: 'x', message: 'x', nodeId: null, edgeId: null, port: null, field: null, ...over })

interface Call {
  method: string
  url: string
  body: unknown
  ifMatch?: string
}

function mockApi(opts: { issues?: WorkflowIssueDto[]; publish?: 'ok' | 'invalid' } = {}) {
  const calls: Call[] = []
  const json = (b: unknown, status = 200, headers: Record<string, string> = {}) => new Response(JSON.stringify(b), { status, headers: { 'Content-Type': 'application/json', ...headers } })
  const version = { versionId: 'v1', workflowId: WF, versionNumber: null, status: 'Draft', definition, contentHash: null, createdAt: '', createdBy: '', updatedAt: '', updatedBy: '', publishedAt: null, publishedBy: null }
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init: RequestInit = {}) => {
      const method = init.method ?? 'GET'
      const headers = Object.fromEntries(new Headers(init.headers).entries()) // lower-case names
      calls.push({ method, url, body: init.body ? JSON.parse(String(init.body)) : undefined, ifMatch: headers['if-match'] })
      if (url === '/api/health') return json({ status: 'Healthy', totalDurationMs: 1, checks: [] })
      if (url === '/api/v1/node-types') return json(nodeTypes)
      if (url === `/api/v1/workflows/${WF}`) return json({ id: WF, key: 'invoice-standard', name: 'Invoice standard', description: null, createdAt: '', createdBy: '', latestPublishedVersion: null, draftVersionId: 'v1' })
      if (url === `/api/v1/workflows/${WF}/draft` && method === 'GET') return json(version, 200, { ETag: '"w1"' })
      if (url === `/api/v1/workflows/${WF}/draft` && method === 'PUT') return json(version, 200, { ETag: '"w2"' })
      if (url === `/api/v1/workflows/${WF}/draft/validate`) {
        const issues = opts.issues ?? []
        return json({ valid: !issues.some((i) => i.severity === 'error'), issues })
      }
      if (url === `/api/v1/workflows/${WF}/draft/publish`) {
        return opts.publish === 'invalid'
          ? json({ title: 'Conflict', code: 'workflow.invalid', detail: 'The workflow has 1 error(s) and can\'t be published.', issues: [issue({ nodeId: 'pdf', code: 'template.not_published', message: "'Commercial invoice' has no published version yet." })] }, 409)
          : json({ ...version, status: 'Published', versionNumber: 1 }, 200, { ETag: '"p"' })
      }
      if (url.startsWith('/api/v1/templates?')) return json({ items: [{ id: TPL, key: 'INV-STD', name: 'Commercial invoice', documentType: 'Invoice', latestPublishedVersion: 2, hasDraft: false, updatedAt: '' }], page: 1, pageSize: 100, total: 1 })
      if (url === `/api/v1/templates/${TPL}/versions`) return json([{ versionId: 't2', versionNumber: 2, status: 'Published' }, { versionId: 't1', versionNumber: 1, status: 'Published' }])
      if (url === `/api/v1/templates/${TPL}`) return json({ id: TPL, draftVersionId: null })
      if (url === `/api/v1/templates/${TPL}/versions/t2`) return json({ dataSchema: { type: 'object', properties: { invoice: { type: 'object', properties: { number: { type: 'string' }, date: { type: 'string', format: 'date' } } } } }, sampleData: { invoice: { number: 'INV-9', date: '2026-11-02' }, order: { ref: 'A-1' } } })
      return json({ title: 'Not found' }, 404)
    }),
  )
  return calls
}

/**
 * Clicks a node card. A plain click event: React Flow selects nodes on click, while a full pointer sequence
 * (user.click) makes React Flow's drag handler read `event.view.document`, which jsdom's synthetic mousedown lacks.
 */
function selectNode(cardText: HTMLElement) {
  fireEvent.click(cardText)
}

async function open() {
  render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: [`/workflows/${WF}`] })} />)
  await screen.findByRole('toolbar', { name: 'Workflow designer' })
}

describe('Workflow designer', { timeout: 20_000 }, () => {
  it('shows each node with its status from server validation, and per-node issues', async () => {
    mockApi({ issues: [issue({ nodeId: 'pdf', port: 'success', code: 'port.unconnected', message: "Connect 'Success' to the next step." }), issue({ severity: 'warning', nodeId: 'validate', port: 'invalid', message: 'Invalid records will end the run as Rejected.' })] })
    await open()

    await waitFor(() => expect(screen.getByLabelText(/^Generate invoice PDF, Generate PDF: Needs attention/)).toBeInTheDocument())
    expect(screen.getByLabelText(/^Validate invoice data, Validate data: Check/)).toBeInTheDocument()
    expect(screen.getByLabelText(/^ERP webhook, API \/ webhook: Configured/)).toBeInTheDocument()
    const issues = screen.getByRole('region', { name: 'Issues' })
    expect(within(issues).getByText("Connect 'Success' to the next step.")).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Publish/ })).toBeDisabled()
  })

  it('edits a step through its generated settings and saves the definition exactly, with If-Match', async () => {
    const calls = mockApi()
    await open()
    const user = userEvent.setup()

    // Select the Validate step by clicking its card (see selectNode).
    selectNode(await screen.findByText('Validate invoice data'))
    const settings = screen.getByRole('region', { name: 'Step settings' })
    await user.click(within(settings).getAllByRole('button', { name: 'Add' })[0]!)
    // Field inputs offer suggestions (a datalist), so their role is combobox.
    const inputs = within(settings).getAllByRole('combobox', { name: /Required fields/ })
    await user.type(inputs[1]!, 'invoice.date')

    await user.click(screen.getByRole('button', { name: /Save draft/ }))
    await waitFor(() => expect(calls.some((c) => c.method === 'PUT')).toBe(true))
    const put = calls.find((c) => c.method === 'PUT')!
    expect(put.ifMatch).toBe('"w1"')
    const expected = structuredClone(definition)
    expected.nodes[1]!.config = { required: ['invoice.number', 'invoice.date'], types: [] }
    expect(put.body).toEqual({ definition: expected })

    // Edits are validated live (debounced), with the unsaved definition.
    await waitFor(() => expect(calls.filter((c) => c.url.endsWith('/draft/validate')).at(-1)?.body).toEqual({ definition: expected }))
  })

  it('offers published template versions and the template data model as field suggestions', async () => {
    mockApi()
    await open()

    selectNode(await screen.findByText('Generate invoice PDF'))
    const version = screen.getByLabelText('Template version *')
    await waitFor(() => expect(within(version).getByRole('option', { name: 'Version 1' })).toBeInTheDocument())
    expect(within(version).getByRole('option', { name: 'Latest published (currently v2)' })).toBeInTheDocument()
    expect(within(screen.getByLabelText('Template *')).getByRole('option', { name: 'Commercial invoice (INV-STD)' })).toBeInTheDocument()

    await waitFor(() => expect(document.querySelector('datalist option[value="invoice.date"]')).not.toBeNull())
  })

  it('adds steps from the palette, and allows only one trigger', async () => {
    const calls = mockApi()
    await open()
    const user = userEvent.setup()

    expect(screen.getByRole('button', { name: 'Add API / webhook' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Add ERP connector' })).toBeDisabled() // inputs share the one-trigger rule
    await user.click(screen.getByRole('button', { name: 'Add Validate data' }))
    expect(await screen.findByText('Validate data', { selector: '.truncate.text-\\[13px\\]' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Validate data', level: 3 })).toBeInTheDocument() // the new step is selected

    // Regression (seen in the browser): clicking another step while one is selected must select it, not clear the selection.
    selectNode(screen.getByText('Generate invoice PDF'))
    expect(await screen.findByRole('heading', { name: 'Generate PDF', level: 3 })).toBeInTheDocument()

    await user.keyboard('{Control>}z{/Control}')
    await user.keyboard('{Control>}y{/Control}')
    await user.click(screen.getByRole('button', { name: /Save draft/ }))
    await waitFor(() => expect(calls.some((c) => c.method === 'PUT')).toBe(true))
    const saved = (calls.find((c) => c.method === 'PUT')!.body as { definition: typeof definition }).definition
    expect(saved.nodes.map((n) => n.id)).toEqual(['trigger', 'validate', 'pdf', 'validate-2'])
    expect(saved.nodes[3]).toMatchObject({ type: 'validate', title: 'Validate data', config: { required: [], types: [] } })
  })

  it('shows the server issues when publishing is refused', async () => {
    mockApi({ publish: 'invalid' })
    await open()
    const user = userEvent.setup()

    const publish = screen.getByRole('button', { name: /Publish/ })
    await waitFor(() => expect(publish).toBeEnabled())
    await user.click(publish)

    expect(await screen.findByRole('alert')).toHaveTextContent("can't be published")
    expect(within(screen.getByRole('region', { name: 'Issues' })).getByText(/has no published version yet/)).toBeInTheDocument()
  })

  it('edits the API / webhook endpoint, authentication, schema and sample, with live checks', async () => {
    mockApi()
    await open()

    await screen.findByText('Validate invoice data')
    selectNode(screen.getByText('ERP webhook'))
    const settings = await screen.findByRole('region', { name: 'Step settings' })

    expect(within(settings).getByText(/Nothing is listening at this address/)).toHaveTextContent('POST /api/v1/workflows/invoice-standard/executions')
    fireEvent.change(within(settings).getByLabelText(/^Path/), { target: { value: '/hooks/d365/invoices' } })
    expect(within(settings).getByLabelText('Mock endpoint URL')).toHaveValue('https://mock.verso.local/hooks/d365/invoices')

    expect(within(settings).getByText(/X-Verso-Signature/)).toBeInTheDocument()
    fireEvent.change(within(settings).getByLabelText('Authentication method'), { target: { value: 'none' } })
    expect(within(settings).getByText(/Use only for local testing/)).toBeInTheDocument()

    const checks = within(settings).getByRole('list', { name: 'Checks' })
    expect(checks).toHaveTextContent('Schema is valid JSON')
    expect(checks).toHaveTextContent('No sample payload yet')

    const sample = within(settings).getByLabelText('Sample JSON payload')
    fireEvent.change(sample, { target: { value: '{ "invoice": {' } })
    expect(checks).toHaveTextContent('Sample isn’t valid JSON')
    fireEvent.change(sample, { target: { value: '{ "invoice": {} }' } })
    expect(checks).toHaveTextContent('Sample doesn’t match the schema: invoice.number is required but missing')

    // The referenced template's sample data can be loaded; it fits the schema.
    const load = within(settings).getByLabelText('Load sample data')
    await waitFor(() => expect(load).toBeEnabled())
    fireEvent.change(load, { target: { value: 'template' } })
    expect((sample as HTMLTextAreaElement).value).toContain('"INV-9"')
    expect(checks).toHaveTextContent('Sample matches the schema')

    // Field names in the sample are suggested in later steps.
    selectNode(screen.getByText('Validate invoice data'))
    await waitFor(() => expect(document.querySelector('datalist option[value="order.ref"]')).not.toBeNull())
  })
})
