import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { PreviewButton } from './PreviewButton'

const pdf = () => new Response('%PDF-1.4', { status: 200, headers: { 'Content-Type': 'application/pdf' } })

describe('PreviewButton', () => {
  beforeEach(() => {
    vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: () => 'blob:doc-1', revokeObjectURL: vi.fn() }))
  })
  afterEach(() => vi.unstubAllGlobals())

  it('fetches the document with the API and shows it in a frame', async () => {
    const fetchMock = vi.fn(async () => pdf())
    vi.stubGlobal('fetch', fetchMock)
    render(<PreviewButton documentId="d1" fileName="C001.pdf" />)

    await userEvent.click(screen.getByRole('button', { name: 'Preview C001.pdf' }))

    const frame = await screen.findByTitle('Preview of C001.pdf')
    expect(frame).toHaveAttribute('src', 'blob:doc-1')
    expect(fetchMock).toHaveBeenCalledWith('/api/v1/documents/d1/content', expect.anything())
    expect(screen.getByRole('button', { name: 'Download C001.pdf' })).toBeInTheDocument()
  })

  it('shows the API problem when the document cannot be read', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(JSON.stringify({ code: 'document.integrity_failed', detail: 'The archived file does not match its recorded SHA-256.' }), { status: 409 })),
    )
    render(<PreviewButton documentId="d1" fileName="C001.pdf" />)

    await userEvent.click(screen.getByRole('button', { name: 'Preview C001.pdf' }))

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('does not match its recorded SHA-256'))
    expect(screen.queryByTitle('Preview of C001.pdf')).not.toBeInTheDocument()
  })
})
