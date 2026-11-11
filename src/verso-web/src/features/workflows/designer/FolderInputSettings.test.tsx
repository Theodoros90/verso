import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi } from 'vitest'

import { accessTargets, FolderInputSettings, usesApprovedLocations } from './FolderInputSettings'
import type { FieldSchema } from './SchemaForm'

const WF = '11111111-1111-1111-1111-111111111111'
const RUN = '33333333-3333-3333-3333-333333333333'

const schema: FieldSchema = {
  type: 'object',
  properties: {
    sourcePath: { type: 'string', title: 'Source folder', 'x-widget': 'folder-path' },
    pattern: { type: 'string', title: 'File pattern', 'x-widget': 'mono' },
    includeSubfolders: { type: 'boolean', title: 'Include subfolders' },
    interval: { type: 'string', title: 'Scan interval', enum: ['every-minute', 'hourly'], 'x-enumLabels': ['Every minute', 'Hourly'] },
    processedPath: { type: 'string', title: 'Processed files go to', 'x-widget': 'folder-path' },
    failedPath: { type: 'string', title: 'Failed files go to', 'x-widget': 'folder-path' },
    sourceLocation: { type: 'string', 'x-widget': 'legacy' },
    sourceSubfolder: { type: 'string', 'x-widget': 'legacy' },
  },
  required: ['pattern', 'includeSubfolders', 'interval'],
}

const config = {
  sourcePath: 'D:\\Invoices\\in', pattern: '*.json', includeSubfolders: false, interval: 'every-minute',
  processedPath: 'D:\\Invoices\\done', failedPath: 'D:\\Invoices\\failed',
}

function mockApi() {
  const calls: { method: string; url: string; body: unknown }[] = []
  const json = (b: unknown, status = 200) => new Response(JSON.stringify(b), { status, headers: { 'Content-Type': 'application/json' } })
  const test = { id: 't1', targets: [], identity: null, machine: null, error: null, requestedAt: '', completedAt: null }
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init: RequestInit = {}) => {
      const method = init.method ?? 'GET'
      calls.push({ method, url, body: init.body ? JSON.parse(String(init.body)) : undefined })
      if (url === '/api/v1/folder-access-tests' && method === 'POST') return json({ ...test, status: 'Queued', results: [] }, 202)
      if (url === '/api/v1/folder-access-tests/t1') {
        return json({
          ...test, status: 'Completed', identity: 'CORP\\svc-verso-worker$', machine: 'VERSO-WRK01',
          results: [
            { role: 'source', location: 'path-1', subfolder: '', ok: true, checks: [{ name: 'approved', ok: true, message: 'Allowed.' }] },
            { role: 'processed', location: 'path-2', subfolder: '', ok: false,
              checks: [{ name: 'write', ok: false, message: "Access denied to 'D:\\Invoices\\done' for CORP\\svc-verso-worker$." }] },
          ],
        })
      }
      if (url === `/api/v1/workflows/${WF}/folder-input`) {
        return json({
          watch: { nodeId: 'trigger', lastScanAt: '2026-11-11T08:00:00Z', lastStatus: 'Unavailable', lastError: "Folder 'D:\\Invoices\\in' doesn't exist or isn't reachable from VERSO-WRK01.", lastFilesFound: 0 },
          files: [
            { id: 'f1', fileName: 'inv-1.json', relativePath: 'inv-1.json', sizeBytes: 10, status: 'Completed', outcome: 'Processed', errorCode: null, errorMessage: null,
              executionId: RUN, deliveredName: 'inv-1.json', deliveryError: null, claimedAt: '', completedAt: '' },
            { id: 'f2', fileName: 'bad.json', relativePath: 'bad.json', sizeBytes: 3, status: 'Completed', outcome: 'Failed', errorCode: 'file.invalid_json',
              errorMessage: "The file isn't a JSON object.", executionId: null, deliveredName: 'bad.json', deliveryError: null, claimedAt: '', completedAt: '' },
          ],
        })
      }
      return json({ title: 'Not found' }, 404)
    }),
  )
  return calls
}

function renderSettings(over: Partial<React.ComponentProps<typeof FolderInputSettings>> = {}) {
  const onChange = vi.fn()
  render(
    <MemoryRouter>
      <FolderInputSettings
        schema={schema} value={config} onChange={onChange} errorsFor={() => []} readOnly={false} canTest
        workflowId={WF} published={false} {...over}
      />
    </MemoryRouter>,
  )
  return onChange
}

describe('Folder input settings', () => {
  it('takes the three folders as typed paths, with no location pickers', () => {
    mockApi()
    const onChange = renderSettings()

    expect(screen.queryByRole('combobox', { name: /location/i })).toBeNull()
    const source = screen.getByLabelText(/^Source folder/)
    expect(source).toHaveValue('D:\\Invoices\\in')
    expect(source).toHaveAttribute('placeholder', expect.stringContaining('\\\\fileserver\\share\\in'))
    expect(screen.getByLabelText(/^Processed files go to/)).toHaveValue('D:\\Invoices\\done')
    expect(screen.getByLabelText(/^Failed files go to/)).toHaveValue('D:\\Invoices\\failed')

    fireEvent.change(source, { target: { value: '\\\\fileserver\\verso\\ap-inbound' } })
    expect(onChange).toHaveBeenLastCalledWith({ ...config, sourcePath: '\\\\fileserver\\verso\\ap-inbound' }, 'sourcePath')
    fireEvent.click(screen.getByRole('switch', { name: 'Include subfolders' }))
    expect(onChange).toHaveBeenLastCalledWith({ ...config, includeSubfolders: true }, 'includeSubfolders')
  })

  it('shows server problems next to the path field', () => {
    mockApi()
    renderSettings({ errorsFor: (f) => (f === 'sourcePath' ? ['Must be absolute: a drive path (D:\\…)'] : []) })
    expect(screen.getByText(/^Must be absolute/)).toBeInTheDocument()
  })

  it('hides settings kept only for older workflows, and says when a step still uses approved locations', () => {
    mockApi()
    renderSettings({ value: { ...config, sourcePath: '', sourceLocation: 'ap-inbound', sourceSubfolder: 'incoming' } })

    expect(screen.queryByDisplayValue('ap-inbound')).toBeNull()
    expect(screen.getByText(/still uses folders chosen from approved locations/)).toBeInTheDocument()
    expect(usesApprovedLocations({ sourceLocation: 'ap-inbound' })).toBe(true)
    expect(usesApprovedLocations({ sourceLocation: 'ap-inbound', sourcePath: 'D:\\x' })).toBe(false)
    expect(usesApprovedLocations(config)).toBe(false)
  })

  it('tests access of the typed paths through the Worker and shows the identity used', async () => {
    const calls = mockApi()
    renderSettings()

    fireEvent.click(screen.getByRole('button', { name: 'Test access' }))

    expect(await screen.findByText('Processed files: no access', {}, { timeout: 4000 })).toBeInTheDocument()
    expect(screen.getByText('Source folder: access OK')).toBeInTheDocument()
    expect(screen.getByText(/Access denied to 'D:\\Invoices\\done'/)).toBeInTheDocument()
    expect(screen.getByText('CORP\\svc-verso-worker$')).toBeInTheDocument()
    expect(calls.find((c) => c.method === 'POST')!.body).toEqual({
      targets: [
        { role: 'source', location: null, subfolder: null, path: 'D:\\Invoices\\in' },
        { role: 'processed', location: null, subfolder: null, path: 'D:\\Invoices\\done' },
        { role: 'failed', location: null, subfolder: null, path: 'D:\\Invoices\\failed' },
      ],
    })
  })

  it('hides Test access from people who cannot author workflows', () => {
    mockApi()
    renderSettings({ canTest: false, readOnly: true })
    expect(screen.queryByRole('button', { name: 'Test access' })).toBeNull()
  })

  it('shows folder errors and recent files of a published workflow', async () => {
    mockApi()
    renderSettings({ published: true })

    const activity = screen.getByRole('region', { name: 'Folder activity' })
    await waitFor(() => expect(activity).toHaveTextContent('Folder unavailable'))
    expect(activity).toHaveTextContent("Folder 'D:\\Invoices\\in' doesn't exist")
    const files = within(activity).getByRole('list', { name: 'Recent files' })
    expect(files).toHaveTextContent('inv-1.json')
    expect(files).toHaveTextContent("bad.jsonFailedThe file isn't a JSON object.")
    expect(within(files).getByRole('link', { name: 'View run' })).toHaveAttribute('href', `/executions/${RUN}`)
  })

  it('builds access test targets only for typed paths', () => {
    expect(accessTargets({ sourcePath: ' D:\\in ', processedPath: '', failedPath: 'D:\\out' })).toEqual([
      { role: 'source', location: null, subfolder: null, path: 'D:\\in' },
      { role: 'failed', location: null, subfolder: null, path: 'D:\\out' },
    ])
  })
})
