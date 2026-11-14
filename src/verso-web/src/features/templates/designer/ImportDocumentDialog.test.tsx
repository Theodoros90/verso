import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it, vi } from 'vitest'

import { routes } from '@/app/routes'
import { setSession } from '@/lib/auth'

import { asFile, docx, para, table } from '../import/testFiles'

const ID = '11111111-1111-1111-1111-111111111111'
const VERSION = '22222222-2222-2222-2222-222222222222'

/** A draft the API serves and stores, so a save can be checked end to end. */
function api(layout: unknown = { layoutSchemaVersion: 1 }) {
  let stored: Record<string, unknown> = {
    versionId: VERSION, templateId: ID, versionNumber: null, status: 'Draft',
    pageSettings: { size: 'A4', orientation: 'portrait', marginsMm: { top: 18, right: 16, bottom: 18, left: 16 } },
    layout, dataSchema: { type: 'object' }, sampleData: {}, contentHash: null,
    createdAt: '', createdBy: '', updatedAt: '', updatedBy: '', publishedAt: null, publishedBy: null,
  }
  const puts: unknown[] = []
  const json = (body: unknown, headers: Record<string, string> = {}) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json', ...headers } })
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init: RequestInit = {}) => {
      if (url === '/api/health') return json({ status: 'Healthy', totalDurationMs: 1, checks: [] })
      if (url === `/api/v1/templates/${ID}`) return json({ id: ID, key: 'INV', name: 'Invoice', description: null, documentType: 'Invoice', createdAt: '', createdBy: '', latestPublishedVersion: null, draftVersionId: VERSION })
      if (url === `/api/v1/templates/${ID}/draft` && (init.method ?? 'GET') === 'GET') return json(stored, { ETag: '"e1"' })
      if (url === `/api/v1/templates/${ID}/draft` && init.method === 'PUT') {
        puts.push(JSON.parse(String(init.body)))
        stored = { ...stored, ...JSON.parse(String(init.body)) }
        return json(stored, { ETag: '"e2"' })
      }
      return new Response('{}', { status: 404 })
    }),
  )
  return { puts }
}

async function openDesigner() {
  render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: [`/templates/${ID}`] })} />)
  return screen.findByRole('toolbar', { name: 'Designer' })
}

describe('Import document in the template designer', () => {
  it('imports a DOCX into an empty draft, then saves it with the page margins from the file', async () => {
    const { puts } = api()
    const toolbar = await openDesigner()

    await userEvent.click(within(toolbar).getByRole('button', { name: 'Import document' }))
    const dialog = screen.getByRole('dialog', { name: 'Import a document' })
    expect(within(dialog).getByRole('button', { name: 'Use this design' })).toBeDisabled()

    const file = asFile(
      docx({
        body: para('Statement', { bold: true, size: 18 }) + table([['Date', 'Amount'], ['{{tx.date|date}}', '10.00']]),
        page: { w: 11906, h: 16838, top: 1134, right: 1134, bottom: 1134, left: 1134 },
      }),
      'statement.docx',
    )
    await userEvent.upload(within(dialog).getByLabelText('Document to import'), file)

    expect(await within(dialog).findByText(/1 text block, 1 table, 0 images/)).toHaveTextContent('statement.docx: 1 text block, 1 table, 0 images')
    expect(within(dialog).getByText(/map every column/)).toBeInTheDocument()
    expect(within(dialog).queryByText(/replaces what is on the page/)).not.toBeInTheDocument() // the draft was empty

    await userEvent.click(within(dialog).getByRole('button', { name: 'Use this design' }))
    expect(screen.queryByRole('dialog', { name: 'Import a document' })).not.toBeInTheDocument()
    expect(await screen.findByText(/Imported statement\.docx/)).toBeInTheDocument()

    await userEvent.click(within(toolbar).getByRole('button', { name: /Save draft/ }))
    await waitFor(() => expect(puts).toHaveLength(1))
    const saved = puts[0] as { layout: { body: { blocks: { type: string }[] } }; pageSettings: { marginsMm: unknown } }
    expect(saved.layout.body.blocks.map((b) => b.type)).toEqual(['text', 'table'])
    expect(saved.pageSettings.marginsMm).toEqual({ top: 20, right: 20, bottom: 20, left: 20 })
  })

  it('warns that an existing design is replaced, and Undo brings it back', async () => {
    api({ layoutSchemaVersion: 1, body: { blocks: [{ id: 'old', type: 'text', props: { text: 'Old design' } }] } })
    const toolbar = await openDesigner()

    await userEvent.click(within(toolbar).getByRole('button', { name: 'Import document' }))
    const dialog = screen.getByRole('dialog', { name: 'Import a document' })
    await userEvent.upload(within(dialog).getByLabelText('Document to import'), asFile('{\\rtf1\\ansi New design\\par}', 'new.rtf'))
    expect(await within(dialog).findByText(/replaces what is on the page/)).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Replace the current design' }))

    expect(await screen.findByText(/Imported new\.rtf/)).toBeInTheDocument()
    await userEvent.click(within(toolbar).getByRole('button', { name: 'Undo' }))
    expect(within(toolbar).getByRole('button', { name: 'Undo' })).toBeDisabled() // back to the loaded design
  })

  it('explains an old .doc file instead of importing it', async () => {
    api()
    const toolbar = await openDesigner()
    await userEvent.click(within(toolbar).getByRole('button', { name: 'Import document' }))
    const dialog = screen.getByRole('dialog', { name: 'Import a document' })

    const ole = new Uint8Array([0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1, 0, 0])
    await userEvent.upload(within(dialog).getByLabelText('Document to import'), asFile(ole.buffer as ArrayBuffer, 'old.doc'), { applyAccept: false })

    expect(await within(dialog).findByText(/Save As → Word Document \(\.docx\)/)).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Use this design' })).toBeDisabled()
  })

  it('is not offered to someone who can only view', async () => {
    api()
    setSession({
      mode: 'Development',
      me: { id: 'dev.viewer', name: 'Vera', roles: ['Viewer'], permissions: { read: true, templateAuthoring: false, workflowAuthoring: false, operate: false, readPayload: false, readAudit: false, administer: false } },
      getToken: async () => 'dev:dev.viewer',
      signOut: () => undefined,
    })
    const toolbar = await openDesigner()
    expect(within(toolbar).queryByRole('button', { name: 'Import document' })).not.toBeInTheDocument()
  })
})
