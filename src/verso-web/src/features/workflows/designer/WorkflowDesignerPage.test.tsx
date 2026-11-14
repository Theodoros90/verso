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
    type: 'file-upload', label: 'File upload', category: 'input', description: 'Someone uploads a file in Verso or the portal.',
    inputs: [], outputs: [{ id: 'out', label: 'Out', required: true }],
    configSchema: {
      type: 'object',
      properties: {
        accept: { type: 'string', title: 'Accepted file types' },
        maxSize: { type: 'string', title: 'Largest file', enum: ['10mb', '25mb'], 'x-enumLabels': ['10 MB', '25 MB'] },
        who: { type: 'string', title: 'Who can upload', enum: ['staff'], 'x-enumLabels': ['Signed-in staff'] },
      },
      required: ['accept', 'maxSize', 'who'],
    },
    defaults: { accept: 'PDF, XML, CSV', maxSize: '25mb', who: 'staff' },
  },
  {
    type: 'sftp-folder', label: 'SFTP folder', category: 'input', description: 'Picks up files from a watched folder.',
    inputs: [], outputs: [{ id: 'out', label: 'Out', required: true }],
    configSchema: {
      type: 'object',
      properties: {
        host: { type: 'string', title: 'Host', 'x-widget': 'mono' },
        folder: { type: 'string', title: 'Folder', 'x-widget': 'mono' },
        pattern: { type: 'string', title: 'File pattern', 'x-widget': 'mono' },
        poll: { type: 'string', title: 'Check for files', enum: ['every-minute', 'every-5-minutes'], 'x-enumLabels': ['Every minute', 'Every 5 minutes'] },
      },
      required: ['host', 'folder', 'poll'],
    },
    defaults: { host: '', folder: '/inbound', pattern: '*.xml', poll: 'every-5-minutes' },
  },
  {
    type: 'email-inbox', label: 'Email inbox', category: 'input', description: 'Starts a run from an incoming email (simulated).',
    inputs: [], outputs: [{ id: 'out', label: 'Out', required: true }],
    configSchema: {
      type: 'object',
      properties: {
        mailbox: { type: 'string', title: 'Mailbox', 'x-widget': 'mono' },
        folder: { type: 'string', title: 'Folder' },
        attachments: { type: 'boolean', title: 'Include attachments' },
      },
      required: ['mailbox', 'folder'],
    },
    defaults: { mailbox: 'accounts@example.com', folder: 'Inbox', attachments: true },
  },
  {
    type: 'database-query', label: 'Database query', category: 'input', description: 'Starts a run from a read-only query (simulated).',
    inputs: [], outputs: [{ id: 'out', label: 'Out', required: true }],
    configSchema: {
      type: 'object',
      properties: {
        conn: { type: 'string', title: 'Connection' },
        query: { type: 'string', title: 'Read-only query', 'x-widget': 'multiline' },
        params: { type: 'string', title: 'Parameters (JSON)', 'x-widget': 'json' },
      },
      required: ['conn', 'query', 'params'],
    },
    defaults: { conn: 'Core banking', query: 'SELECT * FROM customers WHERE customer_id = :customerId', params: '{"customerId":"C-001"}' },
  },
  {
    type: 'form-submission', label: 'Form submission', category: 'input', description: 'Starts a run from data entered in a form (simulated).',
    inputs: [], outputs: [{ id: 'out', label: 'Out', required: true }],
    configSchema: {
      type: 'object',
      properties: {
        name: { type: 'string', title: 'Form name' },
        access: { type: 'string', title: 'Access', enum: ['staff', 'portal-customers'], 'x-enumLabels': ['Signed-in staff', 'Portal customers'] },
        sample: { type: 'string', title: 'Sample submission (JSON)', 'x-widget': 'json' },
      },
      required: ['name', 'access', 'sample'],
    },
    defaults: { name: 'Agreement request', access: 'staff', sample: '{"customerId":"C-001"}' },
  },
  {
    type: 'lookup-enrich', label: 'Lookup / enrich data', category: 'process', description: 'Looks a record up by a key (simulated).',
    inputs: [{ id: 'in', label: 'In', required: true }],
    outputs: [{ id: 'found', label: 'Found', required: true }, { id: 'missing', label: 'Not found', required: false }, { id: 'failed', label: 'Failed', required: false }],
    configSchema: {
      type: 'object',
      properties: {
        conn: { type: 'string', title: 'Connection' },
        key: { type: 'string', title: 'Lookup key field', 'x-widget': 'data-path' },
        destination: { type: 'string', title: 'Result field', 'x-widget': 'data-path' },
        missing: { type: 'string', title: 'If no match', enum: ['continue', 'fail'], 'x-enumLabels': ['Continue with empty result', 'Fail the run'] },
      },
      required: ['conn', 'key', 'destination', 'missing'],
    },
    defaults: { conn: 'Microsoft Dynamics 365', key: 'customer.id', destination: 'customer.profile', missing: 'continue' },
  },
  {
    type: 'deduplicate', label: 'Deduplicate', category: 'process', description: 'Detects duplicate records (simulated).',
    inputs: [{ id: 'in', label: 'In', required: true }],
    outputs: [{ id: 'unique', label: 'Unique', required: true }, { id: 'duplicate', label: 'Duplicate', required: false }],
    configSchema: {
      type: 'object',
      properties: {
        key: { type: 'string', title: 'Business key field', 'x-widget': 'data-path' },
        scope: { type: 'string', title: 'Scope', enum: ['current-batch', 'across-runs'], 'x-enumLabels': ['Current batch', 'Across runs'] },
        retention: { type: 'string', title: 'Across-run retention', enum: ['1d', '7d', '30d', '90d'], 'x-enumLabels': ['1 day', '7 days', '30 days', '90 days'] },
        preview: { type: 'string', title: 'Preview outcome', enum: ['unique', 'duplicate'], 'x-enumLabels': ['Unique', 'Duplicate'] },
      },
      required: ['key', 'scope', 'retention', 'preview'],
    },
    defaults: { key: 'invoice.number', scope: 'current-batch', retention: '30d', preview: 'unique' },
  },
  {
    type: 'parallel-branches', label: 'Parallel branches', category: 'logic', description: 'Fans out to two independent paths.',
    inputs: [{ id: 'in', label: 'In', required: true }],
    outputs: [{ id: 'branch-a', label: 'Branch A', required: true }, { id: 'branch-b', label: 'Branch B', required: true }],
    configSchema: { type: 'object', properties: { name: { type: 'string', title: 'Branch group name' } }, required: ['name'] },
    defaults: { name: 'Independent tasks' },
  },
  {
    type: 'scheduled-trigger', label: 'Scheduled trigger', category: 'input', description: 'Runs at fixed times.',
    inputs: [], outputs: [{ id: 'out', label: 'Out', required: true }],
    configSchema: {
      type: 'object',
      properties: {
        cron: { type: 'string', title: 'Cron expression', 'x-widget': 'mono' },
        tz: { type: 'string', title: 'Time zone', enum: ['Europe/Nicosia', 'UTC'], 'x-enumLabels': ['Europe/Nicosia', 'UTC'] },
      },
      required: ['cron', 'tz'],
    },
    defaults: { cron: '0 1 * * *', tz: 'Europe/Nicosia' },
  },
  {
    type: 'manual-trigger', label: 'Manual trigger', category: 'input', description: 'Someone starts a run from Verso.',
    inputs: [], outputs: [{ id: 'out', label: 'Out', required: true }],
    configSchema: {
      type: 'object',
      properties: {
        who: { type: 'string', title: 'Who can start it', enum: ['template-leads', 'workspace'], 'x-enumLabels': ['Template leads', 'Anyone in the workspace'] },
        ask: { type: 'string', title: 'Ask for' },
      },
      required: ['who'],
    },
    defaults: { who: 'template-leads', ask: 'Invoice number' },
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
  {
    type: 'generate-docx', label: 'Generate DOCX', category: 'doc', description: 'Renders an editable Word document.',
    inputs: [{ id: 'in', label: 'In', required: true }],
    outputs: [{ id: 'success', label: 'Success', required: true }, { id: 'failed', label: 'Failed', required: false }],
    configSchema: {
      type: 'object',
      properties: {
        templateId: { type: 'string', title: 'Template', 'x-widget': 'template' },
        templateVersion: { type: 'string', title: 'Template version', 'x-widget': 'template-version' },
        fileName: { type: 'string', title: 'Output filename pattern', 'x-widget': 'pattern' },
        protect: { type: 'string', title: 'Editing', enum: ['allow', 'trackedChanges', 'readOnly'], 'x-enumLabels': ['Allow edits', 'Tracked changes only', 'Read only'] },
      },
      required: ['templateId', 'templateVersion', 'fileName', 'protect'],
    },
    defaults: { templateId: '', templateVersion: 'latest-published', fileName: '{{invoice.number}}.docx', protect: 'allow' },
  },
  {
    type: 'generate-email', label: 'Generate HTML email', category: 'doc', description: 'Builds an HTML email body.',
    inputs: [{ id: 'in', label: 'In', required: true }],
    outputs: [{ id: 'success', label: 'Success', required: true }, { id: 'failed', label: 'Failed', required: false }],
    configSchema: {
      type: 'object',
      properties: {
        layout: { type: 'string', title: 'Email layout', enum: ['invoice', 'statement', 'reminder'], 'x-enumLabels': ['Invoice email', 'Statement ready', 'Payment reminder'] },
        subject: { type: 'string', title: 'Subject', 'x-widget': 'pattern' },
        body: { type: 'string', title: 'Body', 'x-widget': 'multiline' },
        darkMode: { type: 'boolean', title: 'Support dark mode' },
      },
      required: ['layout', 'subject', 'body', 'darkMode'],
    },
    defaults: { layout: 'invoice', subject: 'Invoice {{invoice.number}}', body: 'Dear {{customer.name}},', darkMode: true },
  },
  {
    type: 'combine-pdfs', label: 'Combine PDFs', category: 'doc', description: 'Joins the PDFs this run generated into one.',
    inputs: [{ id: 'in', label: 'In', required: true }],
    outputs: [{ id: 'success', label: 'Success', required: true }, { id: 'failed', label: 'Failed', required: false }],
    configSchema: {
      type: 'object',
      properties: {
        order: { type: 'string', title: 'Page order', enum: ['generated', 'reverse'], 'x-enumLabels': ['In the order generated', 'Last generated first'] },
        bookmarks: { type: 'boolean', title: 'Add bookmarks' },
        fileName: { type: 'string', title: 'Output filename pattern', 'x-widget': 'pattern' },
      },
      required: ['order', 'bookmarks', 'fileName'],
    },
    defaults: { order: 'generated', bookmarks: true, fileName: '{{invoice.number}}-pack.pdf' },
  },
  {
    type: 'send-email', label: 'Send email', category: 'output', description: 'Emails a message through the bank relay.',
    inputs: [{ id: 'in', label: 'In', required: true }],
    outputs: [{ id: 'sent', label: 'Sent', required: true }, { id: 'failed', label: 'Failed', required: false }],
    configSchema: {
      type: 'object',
      properties: {
        to: { type: 'string', title: 'To', 'x-widget': 'pattern' },
        subject: { type: 'string', title: 'Subject', 'x-widget': 'pattern' },
        body: { type: 'string', title: 'Message', 'x-widget': 'multiline' },
        attach: { type: 'string', title: 'Attach', enum: ['none', 'document'], 'x-enumLabels': ['Nothing', 'The generated document'] },
      },
      required: ['to', 'subject', 'attach'],
    },
    defaults: { to: '{{customer.email}}', subject: 'Invoice {{invoice.number}}', body: 'Dear {{customer.name}},', attach: 'document' },
  },
  {
    type: 'print-queue', label: 'Print queue', category: 'output', description: 'Sends the PDF for printing and posting.',
    inputs: [{ id: 'in', label: 'In', required: true }],
    outputs: [{ id: 'queued', label: 'Queued', required: true }],
    configSchema: {
      type: 'object',
      properties: {
        queue: { type: 'string', title: 'Print queue', 'x-widget': 'mono' },
        envelope: { type: 'string', title: 'Envelope', enum: ['dl-window', 'c4'], 'x-enumLabels': ['DL window', 'C4 large'] },
        duplex: { type: 'boolean', title: 'Print double-sided' },
      },
      required: ['queue', 'envelope'],
    },
    defaults: { queue: '', envelope: 'dl-window', duplex: true },
  },
  {
    type: 'upload-sftp', label: 'Upload to SFTP', category: 'output', description: 'Puts the document on a partner’s SFTP server (simulated).',
    inputs: [{ id: 'in', label: 'In', required: true }],
    outputs: [{ id: 'success', label: 'Success', required: true }, { id: 'failed', label: 'Failed', required: false }],
    configSchema: {
      type: 'object',
      properties: {
        host: { type: 'string', title: 'Host', 'x-widget': 'mono' },
        folder: { type: 'string', title: 'Folder', 'x-widget': 'mono' },
      },
      required: ['host', 'folder'],
    },
    defaults: { host: '', folder: '/in' },
  },
  {
    type: 'deliver-api', label: 'Deliver via API', category: 'output', description: 'Posts the document to another system (simulated).',
    inputs: [{ id: 'in', label: 'In', required: true }],
    outputs: [{ id: 'delivered', label: 'Delivered', required: true }, { id: 'failed', label: 'Failed', required: false }],
    configSchema: {
      type: 'object',
      properties: { url: { type: 'string', title: 'Endpoint URL', 'x-widget': 'mono' } },
      required: ['url'],
    },
    defaults: { url: '' },
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

function mockApi(opts: { issues?: WorkflowIssueDto[]; publish?: 'ok' | 'invalid'; inactive?: boolean } = {}) {
  const calls: Call[] = []
  let active = !opts.inactive
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
      if (url === `/api/v1/workflows/${WF}`) return json({ id: WF, key: 'invoice-standard', name: 'Invoice standard', description: null, createdAt: '', createdBy: '', latestPublishedVersion: null, draftVersionId: 'v1', isActive: active })
      if (url === `/api/v1/workflows/${WF}/deactivate` && method === 'POST') {
        active = false
        return json({ id: WF, isActive: false })
      }
      if (url === `/api/v1/workflows/${WF}/activate` && method === 'POST') {
        active = true
        return json({ id: WF, isActive: true })
      }
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

  it('filters the palette by search and collapses categories', async () => {
    mockApi()
    await open()
    const user = userEvent.setup()

    await user.type(screen.getByRole('searchbox', { name: 'Search steps' }), 'pdf')
    expect(screen.getByRole('button', { name: 'Add Generate PDF' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Add Validate data' })).not.toBeInTheDocument()
    await user.clear(screen.getByRole('searchbox', { name: 'Search steps' }))
    await user.type(screen.getByRole('searchbox', { name: 'Search steps' }), 'zzzz')
    expect(screen.getByText(/No steps match/)).toBeInTheDocument()
    await user.clear(screen.getByRole('searchbox', { name: 'Search steps' }))

    // Every category starts collapsed.
    expect(screen.queryByRole('button', { name: 'Add Validate data' })).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: /^Processing/ }))
    expect(screen.getByRole('button', { name: 'Add Validate data' })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: /^Processing/ }))
    expect(screen.queryByRole('button', { name: 'Add Validate data' })).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Expand all' }))
    expect(screen.getByRole('button', { name: 'Add Generate PDF' })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Collapse all' }))
    expect(screen.queryByRole('button', { name: 'Add Generate PDF' })).not.toBeInTheDocument()
  })

  it('lists the AI assistance steps as disabled previews that cannot be added', async () => {
    mockApi()
    await open()
    const user = userEvent.setup()

    expect(screen.queryByRole('button', { name: /Extract fields/ })).not.toBeInTheDocument() // starts collapsed
    await user.click(screen.getByRole('button', { name: /^AI assistance/ }))
    for (const label of ['Extract fields', 'Suggest mappings', 'Classify document', 'Draft text', 'Human review']) {
      expect(screen.getByRole('button', { name: `${label} (coming soon)` })).toBeDisabled()
    }
    expect(screen.queryByRole('button', { name: /^Add (Extract fields|Human review)/ })).not.toBeInTheDocument()

    await user.type(screen.getByRole('searchbox', { name: 'Search steps' }), 'classify')
    expect(screen.getByRole('button', { name: 'Classify document (coming soon)' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Draft text/ })).not.toBeInTheDocument()
  })

  it('adds steps from the palette, and allows only one trigger', async () => {
    const calls = mockApi()
    await open()
    const user = userEvent.setup()
    await user.click(screen.getByRole('button', { name: 'Expand all' }))

    expect(screen.getByRole('button', { name: 'Add API / webhook' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Add ERP connector' })).toBeDisabled() // inputs share the one-trigger rule
    expect(screen.getByRole('button', { name: 'Add File upload' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Add SFTP folder' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Add Email inbox' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Add Database query' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Add Form submission' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Add Scheduled trigger' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Add Manual trigger' })).toBeDisabled()
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

  it('adds a Generate DOCX step with its Word defaults and an Editing choice', async () => {
    const calls = mockApi()
    await open()
    const user = userEvent.setup()
    await user.click(screen.getByRole('button', { name: 'Expand all' }))

    await user.click(screen.getByRole('button', { name: 'Add Generate DOCX' }))

    expect(await screen.findByRole('heading', { name: 'Generate DOCX', level: 3 })).toBeInTheDocument()
    expect(screen.getByLabelText(/Editing/)).toHaveDisplayValue('Allow edits')
    expect(screen.getByRole('option', { name: 'Read only' })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: /Save draft/ }))
    await waitFor(() => expect(calls.some((c) => c.method === 'PUT')).toBe(true))
    const saved = (calls.find((c) => c.method === 'PUT')!.body as { definition: typeof definition }).definition
    expect(saved.nodes.at(-1)).toMatchObject({ type: 'generate-docx', config: { fileName: '{{invoice.number}}.docx', protect: 'allow' } })
  })

  it('adds a Generate HTML email step with a layout choice and a multi-line body', async () => {
    const calls = mockApi()
    await open()
    const user = userEvent.setup()
    await user.click(screen.getByRole('button', { name: 'Expand all' }))

    await user.click(screen.getByRole('button', { name: 'Add Generate HTML email' }))

    expect(await screen.findByRole('heading', { name: 'Generate HTML email', level: 3 })).toBeInTheDocument()
    expect(screen.getByLabelText(/Email layout/)).toHaveDisplayValue('Invoice email')
    const body = screen.getByLabelText(/Body/)
    expect(body.tagName).toBe('TEXTAREA')
    await user.clear(body)
    await user.type(body, 'Line one{Enter}{Enter}Line two')
    await user.click(screen.getByRole('button', { name: /Save draft/ }))
    await waitFor(() => expect(calls.some((c) => c.method === 'PUT')).toBe(true))
    const saved = (calls.find((c) => c.method === 'PUT')!.body as { definition: typeof definition }).definition
    expect(saved.nodes.at(-1)).toMatchObject({ type: 'generate-email', config: { layout: 'invoice', body: 'Line one\n\nLine two', darkMode: true } })
  })

  it('adds a Send email step with its defaults and an Attach choice', async () => {
    const calls = mockApi()
    await open()
    const user = userEvent.setup()
    await user.click(screen.getByRole('button', { name: 'Expand all' }))

    await user.click(screen.getByRole('button', { name: 'Add Send email' }))

    expect(await screen.findByRole('heading', { name: 'Send email', level: 3 })).toBeInTheDocument()
    expect(screen.getByLabelText(/Attach/)).toHaveDisplayValue('The generated document')
    await user.selectOptions(screen.getByLabelText(/Attach/), 'none')
    await user.click(screen.getByRole('button', { name: /Save draft/ }))
    await waitFor(() => expect(calls.some((c) => c.method === 'PUT')).toBe(true))
    const saved = (calls.find((c) => c.method === 'PUT')!.body as { definition: typeof definition }).definition
    expect(saved.nodes.at(-1)).toMatchObject({ type: 'send-email', config: { to: '{{customer.email}}', attach: 'none' } })
  })

  it('adds a Print queue step with its defaults, an Envelope choice and the double-sided switch', async () => {
    const calls = mockApi()
    await open()
    const user = userEvent.setup()
    await user.click(screen.getByRole('button', { name: 'Expand all' }))

    await user.click(screen.getByRole('button', { name: 'Add Print queue' }))

    expect(await screen.findByRole('heading', { name: 'Print queue', level: 3 })).toBeInTheDocument()
    expect(screen.getByLabelText(/Envelope/)).toHaveDisplayValue('DL window')
    await user.type(screen.getByRole('textbox', { name: /Print queue/ }), 'cyprus-print-nightly')
    await user.selectOptions(screen.getByLabelText(/Envelope/), 'c4')
    await user.click(screen.getByRole('button', { name: /Save draft/ }))
    await waitFor(() => expect(calls.some((c) => c.method === 'PUT')).toBe(true))
    const saved = (calls.find((c) => c.method === 'PUT')!.body as { definition: typeof definition }).definition
    expect(saved.nodes.at(-1)).toMatchObject({ type: 'print-queue', config: { queue: 'cyprus-print-nightly', envelope: 'c4', duplex: true } })
  })

  it('adds an Upload to SFTP step with a Host and a Folder', async () => {
    const calls = mockApi()
    await open()
    const user = userEvent.setup()
    await user.click(screen.getByRole('button', { name: 'Expand all' }))

    await user.click(screen.getByRole('button', { name: 'Add Upload to SFTP' }))

    expect(await screen.findByRole('heading', { name: 'Upload to SFTP', level: 3 })).toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: /Folder/ })).toHaveValue('/in')
    await user.type(screen.getByRole('textbox', { name: /Host/ }), 'sftp.example.com')
    await user.click(screen.getByRole('button', { name: /Save draft/ }))
    await waitFor(() => expect(calls.some((c) => c.method === 'PUT')).toBe(true))
    const saved = (calls.find((c) => c.method === 'PUT')!.body as { definition: typeof definition }).definition
    expect(saved.nodes.at(-1)).toMatchObject({ type: 'upload-sftp', config: { host: 'sftp.example.com', folder: '/in' } })
  })

  it('adds a Deliver via API step with an Endpoint URL', async () => {
    const calls = mockApi()
    await open()
    const user = userEvent.setup()
    await user.click(screen.getByRole('button', { name: 'Expand all' }))

    await user.click(screen.getByRole('button', { name: 'Add Deliver via API' }))

    expect(await screen.findByRole('heading', { name: 'Deliver via API', level: 3 })).toBeInTheDocument()
    await user.type(screen.getByRole('textbox', { name: /Endpoint URL/ }), 'https://api.partner.com/documents')
    await user.click(screen.getByRole('button', { name: /Save draft/ }))
    await waitFor(() => expect(calls.some((c) => c.method === 'PUT')).toBe(true))
    const saved = (calls.find((c) => c.method === 'PUT')!.body as { definition: typeof definition }).definition
    expect(saved.nodes.at(-1)).toMatchObject({ type: 'deliver-api', config: { url: 'https://api.partner.com/documents' } })
  })

  it('adds a Lookup / enrich data step with its defaults and saves them', async () => {
    const calls = mockApi()
    await open()
    const user = userEvent.setup()
    await user.click(screen.getByRole('button', { name: 'Expand all' }))

    await user.click(screen.getByRole('button', { name: 'Add Lookup / enrich data' }))

    expect(await screen.findByRole('heading', { name: 'Lookup / enrich data', level: 3 })).toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: /Connection/ })).toHaveValue('Microsoft Dynamics 365')
    await user.click(screen.getByRole('button', { name: /Save draft/ }))
    await waitFor(() => expect(calls.some((c) => c.method === 'PUT')).toBe(true))
    const saved = (calls.find((c) => c.method === 'PUT')!.body as { definition: typeof definition }).definition
    expect(saved.nodes.at(-1)).toMatchObject({ type: 'lookup-enrich', config: { key: 'customer.id', destination: 'customer.profile', missing: 'continue' } })
  })

  it('adds a Deduplicate step with its defaults and saves them', async () => {
    const calls = mockApi()
    await open()
    const user = userEvent.setup()
    await user.click(screen.getByRole('button', { name: 'Expand all' }))

    await user.click(screen.getByRole('button', { name: 'Add Deduplicate' }))

    expect(await screen.findByRole('heading', { name: 'Deduplicate', level: 3 })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: /Save draft/ }))
    await waitFor(() => expect(calls.some((c) => c.method === 'PUT')).toBe(true))
    const saved = (calls.find((c) => c.method === 'PUT')!.body as { definition: typeof definition }).definition
    expect(saved.nodes.at(-1)).toMatchObject({ type: 'deduplicate', config: { key: 'invoice.number', scope: 'current-batch', retention: '30d', preview: 'unique' } })
  })

  it('adds a Parallel branches step with its two required outputs', async () => {
    const calls = mockApi()
    await open()
    const user = userEvent.setup()
    await user.click(screen.getByRole('button', { name: 'Expand all' }))

    await user.click(screen.getByRole('button', { name: 'Add Parallel branches' }))

    expect(await screen.findByRole('heading', { name: 'Parallel branches', level: 3 })).toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: /Branch group name/ })).toHaveValue('Independent tasks')
    await user.click(screen.getByRole('button', { name: /Save draft/ }))
    await waitFor(() => expect(calls.some((c) => c.method === 'PUT')).toBe(true))
    const saved = (calls.find((c) => c.method === 'PUT')!.body as { definition: typeof definition }).definition
    expect(saved.nodes.at(-1)).toMatchObject({ type: 'parallel-branches', config: { name: 'Independent tasks' } })
  })

  it('adds a Combine PDFs step with its defaults and a page order choice', async () => {
    const calls = mockApi()
    await open()
    const user = userEvent.setup()
    await user.click(screen.getByRole('button', { name: 'Expand all' }))

    await user.click(screen.getByRole('button', { name: 'Add Combine PDFs' }))

    expect(await screen.findByRole('heading', { name: 'Combine PDFs', level: 3 })).toBeInTheDocument()
    expect(screen.getByLabelText(/Page order/)).toHaveDisplayValue('In the order generated')
    expect(screen.getByRole('option', { name: 'Last generated first' })).toBeInTheDocument()
    await user.selectOptions(screen.getByLabelText(/Page order/), 'reverse')
    await user.click(screen.getByRole('button', { name: /Save draft/ }))
    await waitFor(() => expect(calls.some((c) => c.method === 'PUT')).toBe(true))
    const saved = (calls.find((c) => c.method === 'PUT')!.body as { definition: typeof definition }).definition
    expect(saved.nodes.at(-1)).toMatchObject({ type: 'combine-pdfs', config: { order: 'reverse', bookmarks: true, fileName: '{{invoice.number}}-pack.pdf' } })
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

  it('deactivates the workflow only after confirming, then returns to the list', async () => {
    const calls = mockApi()
    const router = createMemoryRouter(routes, { initialEntries: [`/workflows/${WF}`] })
    render(<RouterProvider router={router} />)
    const toolbar = await screen.findByRole('toolbar', { name: 'Workflow designer' })
    const user = userEvent.setup()
    expect(within(toolbar).queryByText('Inactive')).not.toBeInTheDocument()

    await user.click(within(toolbar).getByRole('button', { name: /Deactivate/ }))
    const dialog = await screen.findByRole('dialog', { name: /Deactivate "Invoice standard"/ })
    expect(calls.some((c) => c.url.endsWith('/deactivate'))).toBe(false) // nothing until confirmed

    await user.click(within(dialog).getByRole('button', { name: 'Deactivate workflow' }))

    await waitFor(() => expect(router.state.location.pathname).toBe('/workflows'))
    expect(calls.some((c) => c.method === 'POST' && c.url === `/api/v1/workflows/${WF}/deactivate`)).toBe(true)
  })

  it('shows an inactive workflow as Inactive and activates it from the toolbar', async () => {
    const calls = mockApi({ inactive: true })
    await open()
    const user = userEvent.setup()
    const toolbar = screen.getByRole('toolbar', { name: 'Workflow designer' })
    expect(within(toolbar).getByText('Inactive')).toBeInTheDocument()
    expect(within(toolbar).queryByRole('button', { name: /Deactivate/ })).not.toBeInTheDocument()

    await user.click(within(toolbar).getByRole('button', { name: /Activate/ }))

    await waitFor(() => expect(within(screen.getByRole('toolbar', { name: 'Workflow designer' })).queryByText('Inactive')).not.toBeInTheDocument())
    expect(calls.some((c) => c.method === 'POST' && c.url === `/api/v1/workflows/${WF}/activate`)).toBe(true)
    expect(screen.getByRole('status')).toHaveTextContent("'invoice-standard' accepts runs again")
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
