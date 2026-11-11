import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi } from 'vitest'

import type { FolderLocationDto } from '@/lib/api/generated'

import { accessTargets, FolderInputSettings } from './FolderInputSettings'
import type { FieldSchema } from './SchemaForm'

const WF = '11111111-1111-1111-1111-111111111111'
const RUN = '33333333-3333-3333-3333-333333333333'

const schema: FieldSchema = {
  type: 'object',
  properties: {
    sourceLocation: { type: 'string', title: 'Source folder', 'x-widget': 'folder-location-input' },
    sourceSubfolder: { type: 'string', title: 'Source subfolder', 'x-widget': 'mono' },
    pattern: { type: 'string', title: 'File pattern', 'x-widget': 'mono' },
    includeSubfolders: { type: 'boolean', title: 'Include subfolders' },
    interval: { type: 'string', title: 'Scan interval', enum: ['every-minute', 'hourly'], 'x-enumLabels': ['Every minute', 'Hourly'] },
    processedLocation: { type: 'string', title: 'Processed files: location', 'x-widget': 'folder-location-output' },
    processedSubfolder: { type: 'string', title: 'Processed files: subfolder', 'x-widget': 'mono' },
    failedLocation: { type: 'string', title: 'Failed files: location', 'x-widget': 'folder-location-output' },
    failedSubfolder: { type: 'string', title: 'Failed files: subfolder', 'x-widget': 'mono' },
  },
  required: ['sourceLocation', 'pattern', 'includeSubfolders', 'interval', 'processedLocation', 'failedLocation'],
}

const location = (key: string, name: string, input: boolean, output: boolean, enabled = true): FolderLocationDto =>
  ({ key, name, input, output, enabled, problem: enabled ? null : 'Not configured on the Worker any more.', updatedAt: '' })

const locations = [
  location('ap-inbound', 'AP inbound share', true, false),
  location('ap-archive', 'AP archive share', false, true),
  location('old-share', 'Old share', true, true, false),
]

const config = {
  sourceLocation: 'ap-inbound', sourceSubfolder: 'incoming', pattern: '*.json', includeSubfolders: false, interval: 'every-minute',
  processedLocation: 'ap-archive', processedSubfolder: 'processed', failedLocation: 'ap-archive', failedSubfolder: 'failed',
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
            { role: 'source', location: 'ap-inbound', subfolder: 'incoming', ok: true, checks: [{ name: 'approved', ok: true, message: 'Configured.' }] },
            { role: 'processed', location: 'ap-archive', subfolder: 'processed', ok: false,
              checks: [{ name: 'write', ok: false, message: "Access denied to 'ap-archive/processed' for CORP\\svc-verso-worker$." }] },
          ],
        })
      }
      if (url === `/api/v1/workflows/${WF}/folder-input`) {
        return json({
          watch: { nodeId: 'trigger', lastScanAt: '2026-11-11T08:00:00Z', lastStatus: 'Unavailable', lastError: "Folder 'ap-inbound/incoming' doesn't exist or isn't reachable from VERSO-WRK01.", lastFilesFound: 0 },
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
        locations={locations} workflowId={WF} published={false} {...over}
      />
    </MemoryRouter>,
  )
  return onChange
}

describe('Folder input settings', () => {
  it('offers only approved locations for each purpose, never a path field', () => {
    mockApi()
    const onChange = renderSettings()

    const source = screen.getByLabelText(/^Source folder/)
    const sourceOptions = within(source).getAllByRole('option').map((o) => o.textContent)
    expect(sourceOptions).toEqual(['Choose an approved location…', 'AP inbound share (ap-inbound)', 'Old share (old-share), unavailable'])
    expect(within(source).getByRole('option', { name: /Old share/ })).toBeDisabled()
    const processed = within(screen.getByLabelText(/^Processed files: location/)).getAllByRole('option').map((o) => o.textContent)
    expect(processed).toContain('AP archive share (ap-archive)')
    expect(processed).not.toContain('AP inbound share (ap-inbound)')

    fireEvent.change(source, { target: { value: '' } })
    expect(onChange).toHaveBeenLastCalledWith({ ...config, sourceLocation: '' }, 'sourceLocation')
    fireEvent.click(screen.getByRole('switch', { name: 'Include subfolders' }))
    expect(onChange).toHaveBeenLastCalledWith({ ...config, includeSubfolders: true }, 'includeSubfolders')
  })

  it('shows a saved location that is no longer approved', () => {
    mockApi()
    renderSettings({ value: { ...config, sourceLocation: 'c-drive' } })
    expect(screen.getByLabelText(/^Source folder/)).toHaveDisplayValue('Unknown location (c-drive)')
  })

  it('explains when no folders are approved yet', () => {
    mockApi()
    renderSettings({ locations: [] })
    expect(screen.getByText(/No folders are approved yet/)).toBeInTheDocument()
  })

  it('tests access through the Worker and shows each folder with the identity used', async () => {
    const calls = mockApi()
    renderSettings()

    fireEvent.click(screen.getByRole('button', { name: 'Test access' }))

    expect(await screen.findByText('Processed files: no access', {}, { timeout: 4000 })).toBeInTheDocument()
    expect(screen.getByText('Source folder: access OK')).toBeInTheDocument()
    expect(screen.getByText(/Access denied to 'ap-archive\/processed'/)).toBeInTheDocument()
    expect(screen.getByText('CORP\\svc-verso-worker$')).toBeInTheDocument()
    expect(calls.find((c) => c.method === 'POST')!.body).toEqual({
      targets: [
        { role: 'source', location: 'ap-inbound', subfolder: 'incoming' },
        { role: 'processed', location: 'ap-archive', subfolder: 'processed' },
        { role: 'failed', location: 'ap-archive', subfolder: 'failed' },
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
    expect(activity).toHaveTextContent("doesn't exist or isn't reachable from VERSO-WRK01")
    const files = within(activity).getByRole('list', { name: 'Recent files' })
    expect(files).toHaveTextContent('inv-1.json')
    expect(files).toHaveTextContent("bad.jsonFailedThe file isn't a JSON object.")
    expect(within(files).getByRole('link', { name: 'View run' })).toHaveAttribute('href', `/executions/${RUN}`)
  })

  it('builds access test targets only for chosen locations', () => {
    expect(accessTargets({ sourceLocation: 'in', sourceSubfolder: 'a', processedLocation: '', failedLocation: 'out' })).toEqual([
      { role: 'source', location: 'in', subfolder: 'a' },
      { role: 'failed', location: 'out', subfolder: '' },
    ])
  })
})
