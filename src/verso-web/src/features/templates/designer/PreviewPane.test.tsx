import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import type { PdfPreviewDto } from '@/lib/api/generated'

import { PreviewPane } from './PreviewPane'

const waitForPreview = vi.fn<(id: string, signal: AbortSignal) => Promise<PdfPreviewDto>>()
vi.mock('@/lib/realtime', () => ({ waitForPreview: (id: string, signal: AbortSignal) => waitForPreview(id, signal) }))

const done = (over: Partial<PdfPreviewDto> = {}): PdfPreviewDto => ({
  previewId: 'pv1',
  templateId: 't1',
  status: 'Succeeded',
  issues: [],
  error: null,
  sizeBytes: 48_000,
  createdAt: '2026-11-09T10:00:00Z',
  completedAt: '2026-11-09T10:00:01Z',
  ...over,
})

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

function renderPane() {
  render(<PreviewPane templateId="t1" revision={0} dirty={false} onSave={() => {}} onClose={() => {}} onSelectElement={() => {}} />)
}

describe('Preview pane, PDF tab', () => {
  let posts: string[]

  beforeEach(() => {
    posts = []
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string, init: RequestInit = {}) => {
        if (url.endsWith('/draft/preview/html')) return json({ html: '<!doctype html><p>x</p>', issues: [] })
        if (url.endsWith('/draft/preview/pdf') && init.method === 'POST') {
          posts.push(url)
          return json(done({ status: 'Queued', sizeBytes: null }), 202)
        }
        return json({ title: 'Not found' }, 404)
      }),
    )
  })

  it('queues a Worker render and shows the PDF when it is ready', async () => {
    waitForPreview.mockResolvedValue(done({ issues: [{ severity: 'error', code: 'binding.missing', message: "No value for 'customer.name'.", elementId: 'billto', path: 'customer.name' }] }))
    renderPane()
    const user = userEvent.setup()

    await user.click(screen.getByRole('radio', { name: 'PDF' }))

    const frame = await screen.findByTitle('PDF preview')
    expect(frame).toHaveAttribute('src', '/api/v1/previews/pv1/content')
    expect(posts).toEqual(['/api/v1/templates/t1/draft/preview/pdf'])
    expect(waitForPreview).toHaveBeenCalledWith('pv1', expect.any(AbortSignal))
    expect(screen.getByRole('link', { name: /Open in new tab/ })).toHaveAttribute('href', '/api/v1/previews/pv1/content')
    expect(screen.getByText('47 KB')).toBeInTheDocument()
    expect(screen.getByRole('list', { name: 'Preview issues' })).toHaveTextContent("No value for 'customer.name'.")
  })

  it('shows the Worker error when rendering failed', async () => {
    waitForPreview.mockResolvedValue(done({ status: 'Failed', error: 'PDF rendering failed (attempt 3 of 3): timeout', sizeBytes: null }))
    renderPane()

    await userEvent.setup().click(screen.getByRole('radio', { name: 'PDF' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('attempt 3 of 3')
    expect(screen.queryByTitle('PDF preview')).not.toBeInTheDocument()
  })

  it('shows progress while the Worker renders', async () => {
    waitForPreview.mockReturnValue(new Promise(() => {}))
    renderPane()

    await userEvent.setup().click(screen.getByRole('radio', { name: 'PDF' }))

    expect(await screen.findByRole('status')).toHaveTextContent('Rendering PDF in the Worker')
  })
})
