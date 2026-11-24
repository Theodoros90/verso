import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { UploadFileDialog } from './UploadFileDialog'

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

class FakeXhr {
  static last: FakeXhr | null = null
  static count = 0
  method = ''
  url = ''
  headers: Record<string, string> = {}
  body: unknown
  status = 0
  responseText = ''
  upload: { onprogress: ((e: { lengthComputable: boolean; loaded: number; total: number }) => void) | null } = { onprogress: null }
  onload: (() => void) | null = null
  onerror: (() => void) | null = null
  onabort: (() => void) | null = null
  ontimeout: (() => void) | null = null
  open(method: string, url: string) {
    this.method = method
    this.url = url
  }
  setRequestHeader(k: string, v: string) {
    this.headers[k] = v
  }
  send(body: unknown) {
    this.body = body
    FakeXhr.last = this
    FakeXhr.count++
  }
  aborted = false
  abort() {
    this.aborted = true
    this.onabort?.()
  }
  finish(status: number, body: unknown) {
    this.status = status
    this.responseText = JSON.stringify(body)
    this.onload?.()
  }
}

const settings = { accept: ['.pdf', '.xml'], maxBytes: 1_048_576 }
const submission = { executionId: 'e-123', status: 'Queued', workflowKey: 'invoice-standard', workflowVersion: 1, correlationId: 'c' }

const closed = vi.fn()
function openDialog() {
  render(
    <MemoryRouter>
      <UploadFileDialog workflowKey="invoice-standard" workflowName="Invoice standard" open onOpenChange={closed} />
    </MemoryRouter>,
  )
}

const file = (name: string, size = 10) => new File([new Uint8Array(size)], name, { type: 'application/octet-stream' })
const setup = () => userEvent.setup({ applyAccept: false })

/** Opens the dialog, picks a file and clicks Upload; resolves once the request has been sent. */
async function sendFile(name = 'a.pdf') {
  openDialog()
  const user = setup()
  await screen.findByText(/PDF, XML/)
  await user.upload(screen.getByLabelText('Choose a file'), file(name))
  await user.click(screen.getByRole('button', { name: 'Upload' }))
  await waitFor(() => expect(FakeXhr.last).not.toBeNull())
  return { user, xhr: FakeXhr.last! }
}

beforeEach(() => {
  FakeXhr.last = null
  FakeXhr.count = 0
  vi.stubGlobal('XMLHttpRequest', FakeXhr)
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string) => (url === '/api/v1/workflows/invoice-standard/upload-settings' ? json(settings) : json({ title: 'Not found' }, 404))),
  )
})
afterEach(() => vi.unstubAllGlobals())

describe('UploadFileDialog', () => {
  it('shows_allowed_types_and_size_from_settings', async () => {
    openDialog()
    expect(await screen.findByText(/PDF, XML/)).toBeInTheDocument()
    expect(screen.getByText(/up to 1 MB/)).toBeInTheDocument()
  })

  it('refuses_wrong_extension_without_calling_the_api', async () => {
    openDialog()
    const user = setup()
    await screen.findByText(/PDF, XML/)
    await user.upload(screen.getByLabelText('Choose a file'), file('notes.exe'))
    expect(await screen.findByRole('alert')).toHaveTextContent('.exe')
    expect(screen.getByRole('button', { name: 'Upload' })).toBeDisabled()
    expect(FakeXhr.count).toBe(0)
  })

  it('refuses_file_over_max_without_calling_the_api', async () => {
    openDialog()
    const user = setup()
    await screen.findByText(/PDF, XML/)
    await user.upload(screen.getByLabelText('Choose a file'), file('big.pdf', 1_048_577))
    expect(await screen.findByRole('alert')).toHaveTextContent(/too large/i)
    expect(screen.getByRole('button', { name: 'Upload' })).toBeDisabled()
    expect(FakeXhr.count).toBe(0)
  })

  it('posts_the_file_and_links_to_the_new_run_on_202', async () => {
    const { xhr } = await sendFile()
    expect(xhr.method).toBe('POST')
    expect(xhr.url).toBe('/api/v1/workflows/invoice-standard/uploads')
    expect(xhr.headers.Authorization).toBe('Bearer dev:dev.admin')
    expect((xhr.body as FormData).get('file')).toBeInstanceOf(File)
    xhr.finish(202, submission)
    const link = await screen.findByRole('link', { name: /Open the run/ })
    expect(link).toHaveAttribute('href', '/executions/e-123')
    expect(screen.queryByText(/already uploaded/)).not.toBeInTheDocument()
  })

  it('says_already_uploaded_on_200_and_links_to_first_run', async () => {
    const { xhr } = await sendFile()
    xhr.finish(200, submission)
    expect(await screen.findByText('This file was already uploaded; showing the first run')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Open the run/ })).toHaveAttribute('href', '/executions/e-123')
  })

  it('shows_the_problem_detail_on_400', async () => {
    const { xhr } = await sendFile()
    xhr.finish(400, { title: 'Bad', status: 400, code: 'upload.content_mismatch', detail: 'The file content does not look like a PDF.' })
    expect(await screen.findByRole('alert')).toHaveTextContent('The file content does not look like a PDF.')
    expect(screen.getByRole('button', { name: 'Upload' })).toBeEnabled()
  })

  it('shows_a_friendly_message_when_the_connection_is_closed', async () => {
    const { xhr } = await sendFile()
    xhr.onerror?.()
    expect(await screen.findByRole('alert')).toHaveTextContent('The upload was refused or interrupted: check the file size limit and try again')
  })

  it('button_is_disabled_while_sending', async () => {
    const { xhr } = await sendFile()
    expect(screen.getByRole('button', { name: /Uploading/ })).toBeDisabled()
    xhr.upload.onprogress?.({ lengthComputable: true, loaded: 5, total: 10 })
    expect(await screen.findByRole('progressbar')).toHaveAttribute('aria-valuenow', '50')
    expect(FakeXhr.count).toBe(1)
  })

  it('cancel_while_sending_aborts_the_request_without_an_error_and_the_dialog_can_be_used_again', async () => {
    closed.mockClear()
    const { user, xhr } = await sendFile()
    await user.click(screen.getByRole('button', { name: 'Cancel' }))
    expect(xhr.aborted).toBe(true)
    expect(closed).toHaveBeenCalledWith(false)
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
    await user.upload(screen.getByLabelText('Choose a file'), file('b.pdf'))
    expect(screen.getByRole('button', { name: 'Upload' })).toBeEnabled()
  })

  it('unmounting_while_sending_aborts_the_request', async () => {
    openDialog()
    const user = setup()
    await screen.findByText(/PDF, XML/)
    await user.upload(screen.getByLabelText('Choose a file'), file('a.pdf'))
    await user.click(screen.getByRole('button', { name: 'Upload' }))
    await waitFor(() => expect(FakeXhr.last).not.toBeNull())
    const xhr = FakeXhr.last!
    cleanup()
    expect(xhr.aborted).toBe(true)
  })
})
