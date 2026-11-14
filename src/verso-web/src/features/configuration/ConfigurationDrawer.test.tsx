import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import { setSession } from '@/lib/auth'
import type { ConfigurationPackage } from '@/lib/api/generated'
import { json, openConfiguration } from '@/test/configuration'

import { buildFile, PACKAGE_PRODUCT } from './package'
import { seedConnections } from '../connections/connections'
import { seedVariables } from '../variables/variables'

const TEMPLATE_ID = '11111111-1111-1111-1111-111111111111'
const WORKFLOW_ID = '22222222-2222-2222-2222-222222222222'

const serverPackage = ({
  product: PACKAGE_PRODUCT,
  schemaVersion: 1,
  environment: 'Test',
  exportedAt: '2026-11-14T10:00:00Z',
  templates: [{ key: 'INV-STD', name: 'Commercial invoice', published: true }],
  workflows: [{ key: 'invoice-standard', name: 'Invoice standard', definition: { nodes: [], edges: [] } }],
} as unknown) as ConfigurationPackage

interface Call {
  url: string
  body: unknown
}

/** The API as the drawer sees it: the lists, export, and an import that reports `outcomes`. */
function api(calls: Call[], outcomes = [{ kind: 'template', key: 'INV-STD', name: 'Commercial invoice', outcome: 'created', message: 'Published as version 1.' }]) {
  return (url: string, init?: RequestInit) => {
    const body = init?.body ? JSON.parse(String(init.body)) : undefined
    if (url.includes('/api/v1/templates')) return json({ items: [{ id: TEMPLATE_ID, key: 'INV-STD', name: 'Commercial invoice', documentType: 'Invoice', latestPublishedVersion: 2, hasDraft: false, updatedAt: '2026-11-01T00:00:00Z' }], page: 1, pageSize: 100, total: 1 })
    if (url.includes('/api/v1/workflows')) {
      const inactive = url.includes('inactive=true')
      return json({ items: inactive ? [] : [{ id: WORKFLOW_ID, key: 'invoice-standard', name: 'Invoice standard', latestPublishedVersion: null, hasDraft: true, updatedAt: '2026-11-01T00:00:00Z', isActive: true }], page: 1, pageSize: 100, total: inactive ? 0 : 1 })
    }
    if (url.includes('/api/v1/configuration/export')) {
      calls.push({ url, body })
      return json(serverPackage)
    }
    if (url.includes('/api/v1/configuration/import')) {
      calls.push({ url, body })
      return json({ dryRun: url.includes('dryRun=true'), items: outcomes, warnings: [] })
    }
    return undefined
  }
}

function captureDownload() {
  const captured: { blob?: Blob } = {}
  URL.createObjectURL = (b: Blob | MediaSource) => {
    captured.blob = b as Blob
    return 'blob:test'
  }
  URL.revokeObjectURL = () => undefined
  const read = () =>
    new Promise<string>((resolve) => {
      const reader = new FileReader()
      reader.onload = () => resolve(String(reader.result))
      reader.readAsText(captured.blob!)
    })
  return read
}

describe('Configuration drawer (the gear)', () => {
  it('opens from the gear with its three tabs, on Connections first', async () => {
    const drawer = await openConfiguration('Connections')

    expect(within(drawer).getAllByRole('tab').map((t) => t.textContent)).toEqual(['Connections', 'Global variables', 'Export / import'])
    expect(within(drawer).getByRole('tab', { name: 'Connections' })).toHaveAttribute('aria-selected', 'true')
    expect(await within(drawer).findByRole('heading', { name: 'SMTP relay' })).toBeInTheDocument()

    await userEvent.click(within(drawer).getByRole('button', { name: 'Close configuration' }))
    expect(screen.queryByRole('dialog', { name: 'Configuration' })).not.toBeInTheDocument()
  })

  it('exports the selection: real templates and workflows from the API, connections and variables from here, no secrets', async () => {
    const calls: Call[] = []
    const read = captureDownload()
    const drawer = await openConfiguration('Export / import', api(calls))
    await within(drawer).findByText('Commercial invoice')

    await userEvent.selectOptions(within(drawer).getByLabelText('Current environment'), 'Production')
    await userEvent.click(within(drawer).getByRole('button', { name: /^Export \d+ items$/ }))

    await waitFor(() => expect(within(drawer).getByRole('status', { name: 'Export result' })).toHaveTextContent(/Exported \d+ connections, 6 variables, 1 template and 1 workflow\. Secrets are never included\./))
    expect(calls).toEqual([{ url: expect.stringContaining('/api/v1/configuration/export'), body: { templateIds: [TEMPLATE_ID], workflowIds: [WORKFLOW_ID], environment: 'Production' } }])
    const file = JSON.parse(await read())
    expect(file.product).toBe(PACKAGE_PRODUCT)
    expect(file.templates[0].key).toBe('INV-STD')
    expect(file.variables.find((v: { name: string }) => v.name === 'portalApiKey')).toMatchObject({ sensitive: true, value: '' })
    expect(JSON.stringify(file)).not.toMatch(/password|"secret":/i)
  })

  it('exports only what stays ticked, and skips the server when no template or workflow is ticked', async () => {
    const calls: Call[] = []
    const read = captureDownload()
    const drawer = await openConfiguration('Export / import', api(calls))
    await within(drawer).findByText('Commercial invoice')

    await userEvent.click(within(drawer).getByRole('button', { name: 'Select nothing' }))
    expect(within(drawer).getByRole('button', { name: 'Export 0 items' })).toBeDisabled()
    await userEvent.click(within(drawer).getByRole('checkbox', { name: 'Select all global variables' }))
    await userEvent.click(within(drawer).getByRole('button', { name: 'Export 6 items' }))

    await waitFor(() => expect(within(drawer).getByRole('status', { name: 'Export result' })).toHaveTextContent('Exported 0 connections, 6 variables, 0 templates and 0 workflows.'))
    expect(calls).toEqual([])
    expect(JSON.parse(await read()).connections).toEqual([])
  })

  it('imports a package: checks it with the server first, then applies it', async () => {
    const calls: Call[] = []
    const drawer = await openConfiguration('Export / import', api(calls))
    const file = buildFile(serverPackage, seedConnections().slice(0, 1), [
      ...seedVariables().slice(0, 1).map((v) => ({ ...v, value: 'Changed Ltd' })),
      { ...seedVariables()[0]!, name: 'brandNew', value: 'x' },
    ])

    await userEvent.upload(within(drawer).getByLabelText('Import configuration file'), new File([JSON.stringify(file)], 'cfg.json', { type: 'application/json' }))

    expect(await within(drawer).findByText('created')).toBeInTheDocument() // the server's dry run
    expect(within(drawer).getByText('Published as version 1.')).toBeInTheDocument()
    expect(within(drawer).getByText(/1 connection and 1 variable with the same name/)).toBeInTheDocument()
    expect(calls).toHaveLength(1)
    expect(calls[0]!.url).toContain('dryRun=true')

    await userEvent.click(within(drawer).getByRole('button', { name: 'Apply package' }))

    await waitFor(() => expect(within(drawer).getByRole('status', { name: 'Import result' })).toHaveTextContent('Imported 1 connection, 2 variables, 1 template and 0 workflows.'))
    expect(calls).toHaveLength(2)
    expect(calls[1]!.url).toContain('dryRun=false')
    expect(calls[1]!.body).toMatchObject({ includeWorkflows: true, package: { templates: [{ key: 'INV-STD' }] } })

    await userEvent.click(within(drawer).getByRole('tab', { name: 'Global variables' }))
    expect(within(drawer).getByText('{{brandNew}}')).toBeInTheDocument()
    expect(within(drawer).getByText('Changed Ltd')).toBeInTheDocument()
  })

  it('leaves everything unchanged when the server refuses the package', async () => {
    const drawer = await openConfiguration('Export / import', (url) => {
      if (url.includes('/api/v1/configuration/import')) {
        return json({ title: 'One or more fields are invalid.', code: 'validation', errors: { 'package.templates[0].key': ['Use 2–32 characters.'] } }, 400)
      }
      return api([])(url)
    })
    const file = buildFile(serverPackage, [], [])

    await userEvent.upload(within(drawer).getByLabelText('Import configuration file'), new File([JSON.stringify(file)], 'cfg.json'))

    expect(await within(drawer).findByText(/package\.templates\[0\]\.key: Use 2–32 characters\./)).toBeInTheDocument()
    expect(within(drawer).queryByRole('button', { name: 'Apply package' })).not.toBeInTheDocument()
  })

  it('rejects a file that is not a configuration package', async () => {
    const drawer = await openConfiguration('Export / import', api([]))
    await userEvent.upload(within(drawer).getByLabelText('Import configuration file'), new File(['{"hello":1}'], 'x.json'))

    expect(await within(drawer).findByText('That file is not a Verso configuration package.')).toBeInTheDocument()
  })

  it('limits import to administrators but still lets anyone export', async () => {
    setSession({
      mode: 'Development',
      me: { id: 'dev.viewer', name: 'Vera Viewer', roles: ['Viewer'], permissions: { read: true, templateAuthoring: false, workflowAuthoring: false, operate: false, readPayload: false, readAudit: false, administer: false } },
      getToken: async () => 'dev:dev.viewer',
      signOut: () => undefined,
    })
    const drawer = await openConfiguration('Export / import', api([]))

    expect(within(drawer).getByText(/limited to administrators/)).toBeInTheDocument()
    expect(within(drawer).queryByLabelText('Import configuration file')).not.toBeInTheDocument()
    expect(within(drawer).getByRole('button', { name: /^Export \d+ items$/ })).toBeEnabled()
  })

  it('remembers the environment label', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json({ status: 'Healthy', totalDurationMs: 1, checks: [] })))
    const drawer = await openConfiguration('Export / import', api([]))
    await userEvent.selectOptions(within(drawer).getByLabelText('Current environment'), 'Development')
    await userEvent.click(within(drawer).getByRole('tab', { name: 'Connections' }))
    await userEvent.click(within(drawer).getByRole('tab', { name: 'Export / import' }))

    expect(within(drawer).getByLabelText('Current environment')).toHaveValue('Development')
  })
})
