import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { routes } from '@/app/routes'
import type { ExecutionDto, ExecutionEventDto, ExecutionStepDto } from '@/lib/api/generated'

// A fake hub: tests push notifications to the registered handlers and simulate a reconnect.
const handlers = new Map<string, (msg: unknown) => void>()
const hubState = { invoked: [] as unknown[][], reconnected: null as null | (() => void) }

vi.mock('@microsoft/signalr', () => {
  class FakeConnection {
    state = 'Disconnected'
    async start() {
      this.state = 'Connected'
    }
    on(name: string, fn: (msg: unknown) => void) {
      handlers.set(name, fn)
    }
    off(name: string) {
      handlers.delete(name)
    }
    onreconnected(fn: () => void) {
      hubState.reconnected = fn
    }
    async invoke(...args: unknown[]) {
      hubState.invoked.push(args)
    }
  }
  class HubConnectionBuilder {
    withUrl() {
      return this
    }
    withAutomaticReconnect() {
      return this
    }
    configureLogging() {
      return this
    }
    build() {
      return new FakeConnection()
    }
  }
  return { HubConnectionBuilder, HubConnectionState: { Connected: 'Connected', Disconnected: 'Disconnected' }, LogLevel: { Warning: 3 } }
})

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

const step = (nodeId: string, type: string, status: string, extra: Partial<ExecutionStepDto> = {}): ExecutionStepDto => ({
  nodeId,
  type,
  title: { trigger: 'API trigger', validate: 'Validate invoice data', pdf: 'Generate invoice PDF', archive: 'Archive' }[nodeId] ?? nodeId,
  sequence: 1,
  status,
  outputPort: null,
  attempt: 1,
  startedAt: null,
  completedAt: null,
  durationMs: null,
  error: null,
  result: null,
  iteration: 0,
  ...extra,
})

const event = (id: number, type: string, nodeId: string | null = null): ExecutionEventDto => ({
  id,
  executionId: 'x1',
  nodeId,
  type,
  occurredAt: '2026-11-10T09:00:00Z',
  data: null,
})

function execution(status: string, steps: ExecutionStepDto[], extra: Partial<ExecutionDto> = {}): ExecutionDto {
  return {
    id: 'x1',
    status,
    workflow: { id: 'w1', key: 'invoice-standard', version: 3, versionId: 'wv1' },
    templateVersions: {},
    idempotencyKey: 'key-1',
    correlationId: 'corr-1',
    inputHash: 'a'.repeat(64),
    submittedAt: '2026-11-10T09:00:00Z',
    submittedBy: 'dev.user',
    startedAt: '2026-11-10T09:00:01Z',
    completedAt: null,
    attempt: 1,
    failureCode: null,
    failureMessage: null,
    steps,
    documents: [],
    ...extra,
  }
}

interface Api {
  current: ExecutionDto
  events: ExecutionEventDto[]
  inputStatus: number
  requested: string[]
}

function stubApi(initial: ExecutionDto, events: ExecutionEventDto[]): Api {
  const api: Api = { current: initial, events, inputStatus: 200, requested: [] }
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string) => {
      api.requested.push(url)
      if (url === '/api/health') return json({ status: 'Healthy', totalDurationMs: 1, checks: [] })
      if (url === '/api/v1/executions/x1') return json(api.current)
      if (url.startsWith('/api/v1/executions/x1/events')) {
        const after = Number(new URL(url, 'http://x').searchParams.get('after'))
        return json(api.events.filter((e) => e.id > after))
      }
      if (url === '/api/v1/executions/x1/input') {
        return api.inputStatus === 200
          ? json({ executionId: 'x1', inputHash: 'a'.repeat(64), data: { invoice: { number: 'INV-1' } } })
          : json({ code: 'forbidden', title: 'Forbidden', detail: 'Reading submitted data requires the Auditor or Admin role.' }, api.inputStatus)
      }
      return json({ title: 'Not found' }, 404)
    }),
  )
  return api
}

const open = () => render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/executions/x1'] })} />)
const statusOf = (name: string) => within(screen.getByRole('list', { name: 'Steps' })).getByText(name).closest('li')!.getAttribute('data-status')

describe('Execution detail', () => {
  beforeEach(() => {
    handlers.clear()
    hubState.invoked = []
  })

  it('follows a run live: a notification makes it re-read, and only the events it has not seen are requested', async () => {
    const api = stubApi(
      execution('Running', [step('trigger', 'api-trigger', 'Succeeded', { outputPort: 'out' }), step('validate', 'validate', 'Running'), step('pdf', 'generate-pdf', 'Pending')]),
      [event(1, 'Submitted'), event(2, 'Started'), event(3, 'StepStarted', 'validate')],
    )
    open()

    await screen.findByRole('list', { name: 'Steps' })
    expect(statusOf('Validate invoice data')).toBe('Running')
    expect(statusOf('Generate invoice PDF')).toBe('Pending')
    expect(screen.getByText('Updating live')).toBeInTheDocument()
    await vi.waitFor(() => expect(hubState.invoked).toContainEqual(['SubscribeExecution', 'x1']))

    api.current = execution(
      'Succeeded',
      [step('trigger', 'api-trigger', 'Succeeded'), step('validate', 'validate', 'Succeeded', { outputPort: 'valid' }), step('pdf', 'generate-pdf', 'Succeeded', { outputPort: 'success' })],
      { completedAt: '2026-11-10T09:00:04Z' },
    )
    api.events.push(event(4, 'StepSucceeded', 'validate'), event(5, 'Succeeded'))
    handlers.get('ExecutionUpdated')!({ executionId: 'x1', status: 'Succeeded', eventId: 5 })

    await waitFor(() => expect(statusOf('Validate invoice data')).toBe('Succeeded'))
    expect(statusOf('Generate invoice PDF')).toBe('Succeeded')
    expect(screen.queryByText('Updating live')).not.toBeInTheDocument()
    expect(api.requested).toContain('/api/v1/executions/x1/events?after=3') // caught up from the last event seen
    const activity = within(screen.getByRole('list', { name: 'Activity' })).getAllByRole('listitem')
    expect(activity.map((li) => li.textContent)).toHaveLength(5) // each event once
  })

  it('ignores notifications about other executions', async () => {
    const api = stubApi(execution('Running', [step('trigger', 'api-trigger', 'Running')]), [event(1, 'Submitted')])
    open()
    await screen.findByRole('list', { name: 'Steps' })
    const before = api.requested.length

    handlers.get('ExecutionUpdated')!({ executionId: 'someone-else' })
    await new Promise((resolve) => setTimeout(resolve, 250))

    expect(api.requested.length).toBe(before)
  })

  it('after a reconnect it joins the group again and catches up on what it missed', async () => {
    const api = stubApi(execution('Running', [step('trigger', 'api-trigger', 'Running')]), [event(1, 'Submitted')])
    open()
    await screen.findByRole('list', { name: 'Steps' })
    await vi.waitFor(() => expect(hubState.invoked.filter((c) => c[0] === 'SubscribeExecution')).toHaveLength(1))

    // While disconnected the run finished; no notification could arrive.
    api.current = execution('Succeeded', [step('trigger', 'api-trigger', 'Succeeded')], { completedAt: '2026-11-10T09:00:04Z' })
    api.events.push(event(2, 'StepSucceeded', 'trigger'), event(3, 'Succeeded'))
    hubState.reconnected!()

    await waitFor(() => expect(statusOf('API trigger')).toBe('Succeeded'))
    expect(hubState.invoked.filter((c) => c[0] === 'SubscribeExecution')).toHaveLength(2)
    expect(within(screen.getByRole('list', { name: 'Activity' })).getAllByRole('listitem')).toHaveLength(3)
  })

  it('shows what a Rejected run found wrong, without offering a retry', async () => {
    stubApi(
      execution(
        'Rejected',
        [
          step('trigger', 'api-trigger', 'Succeeded'),
          step('validate', 'validate', 'Succeeded', {
            outputPort: 'invalid',
            result: { issues: [{ path: 'customer.name', rule: 'required', message: 'missing or empty' }, { path: 'invoice.date', rule: 'type:date', message: 'expected YYYY-MM-DD' }] } as never,
          }),
          step('pdf', 'generate-pdf', 'Skipped'),
        ],
        { failureCode: 'data.invalid', failureMessage: "The data failed 2 validation rule(s) in step 'Validate invoice data'.", completedAt: '2026-11-10T09:00:02Z' },
      ),
      [],
    )
    open()

    expect(await screen.findByText(/Rejected: the data needs correcting/)).toBeInTheDocument()
    const problems = screen.getByRole('list', { name: 'Problems found in Validate invoice data' })
    expect(within(problems).getAllByRole('listitem').map((li) => li.textContent)).toEqual([
      'customer.name [required]: missing or empty',
      'invoice.date [type:date]: expected YYYY-MM-DD',
    ])
    expect(statusOf('Generate invoice PDF')).toBe('Skipped')
    expect(screen.queryByRole('button', { name: /Retry/ })).not.toBeInTheDocument()
  })

  it('lists a step once per loop item, each marked with its item', async () => {
    stubApi(
      execution('Succeeded', [
        step('loop', 'for-each', 'Succeeded', { outputPort: 'each' }),
        step('pdf', 'generate-pdf', 'Succeeded', { iteration: 1 }),
        step('pdf', 'generate-pdf', 'Succeeded', { iteration: 2 }),
      ]),
      [],
    )
    open()

    const items = await screen.findAllByText(/Generate invoice PDF/)
    expect(items).toHaveLength(2)
    expect(screen.getByText(/· item 1/)).toBeInTheDocument()
    expect(screen.getByText(/· item 2/)).toBeInTheDocument()
  })

  it('offers a retry for a Failed run and reads again afterwards', async () => {
    const api = stubApi(execution('Failed', [step('pdf', 'generate-pdf', 'Failed', { error: 'IOException: disk full', attempt: 3 })], { failureCode: 'step.technical_error', failureMessage: 'Step failed' }), [])
    const fetchMock = vi.mocked(fetch)
    const base = fetchMock.getMockImplementation()!
    fetchMock.mockImplementation(async (input, init) => {
      if (String(input).endsWith('/retry') && init?.method === 'POST') {
        api.current = execution('Queued', [step('pdf', 'generate-pdf', 'Pending')], { attempt: 2 })
        return json(api.current, 202)
      }
      return base(input, init)
    })
    open()
    const user = userEvent.setup()

    expect(await screen.findByText('IOException: disk full')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: /Retry from the failed step/ }))

    await waitFor(() => expect(screen.getByText('Queued')).toBeInTheDocument())
    expect(screen.queryByRole('button', { name: /Retry/ })).not.toBeInTheDocument()
  })

  it('lists archived documents with a verified download link', async () => {
    stubApi(
      execution('Succeeded', [step('archive', 'archive', 'Succeeded')], {
        completedAt: '2026-11-10T09:00:04Z',
        documents: [
          {
            id: 'd1',
            nodeId: 'pdf',
            templateVersionId: 'tv1',
            fileName: 'INV-2026-003901.pdf',
            contentType: 'application/pdf',
            sizeBytes: 21284,
            sha256: 'b'.repeat(64),
            status: 'Archived',
            createdAt: '2026-11-10T09:00:03Z',
            iteration: 0,
            archive: { archivedAt: '2026-11-10T09:00:04Z', folderPath: 'invoices/2026/11', retention: '7y', retentionUntil: '2033-11-10T09:00:04Z', metadata: { Customer: 'Kestrel Logistics Ltd' } },
          },
        ],
      }),
      [],
    )
    // The download is fetched with the user's token (a plain link couldn't send it) and handed to the browser.
    const fetchMock = vi.mocked(fetch)
    const base = fetchMock.getMockImplementation()!
    let auth: string | null = null
    fetchMock.mockImplementation(async (input, init) => {
      if (String(input) === '/api/v1/documents/d1/content') {
        auth = new Headers(init?.headers).get('Authorization')
        return new Response('%PDF-1.7', { status: 200, headers: { 'Content-Type': 'application/pdf' } })
      }
      return base(input, init)
    })
    vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: () => 'blob:doc-d1', revokeObjectURL: () => undefined }))
    const saved: string[] = []
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      saved.push(`${this.download} <- ${this.getAttribute('href')}`)
    })
    open()
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Download INV-2026-003901.pdf' }))

    await waitFor(() => expect(saved).toEqual(['INV-2026-003901.pdf <- blob:doc-d1']))
    expect(auth).toBe('Bearer dev:dev.admin')
    click.mockRestore()
    expect(screen.getByText('b'.repeat(64))).toBeInTheDocument()
    expect(screen.getByText('invoices/2026/11')).toBeInTheDocument()
    expect(screen.getByText('Kestrel Logistics Ltd')).toBeInTheDocument()
  })

  describe('submitted data', () => {
    it('is not fetched until asked for, then shown to an auditor', async () => {
      const api = stubApi(execution('Succeeded', [step('trigger', 'api-trigger', 'Succeeded')]), [])
      open()
      const user = userEvent.setup()

      await screen.findByRole('button', { name: 'Show submitted data' })
      expect(api.requested).not.toContain('/api/v1/executions/x1/input')
      await user.click(screen.getByRole('button', { name: 'Show submitted data' }))

      expect(await screen.findByLabelText('Submitted JSON')).toHaveTextContent('"number": "INV-1"')
    })

    it('shows only a notice, never the data, when the user lacks the role (403)', async () => {
      const api = stubApi(execution('Succeeded', [step('trigger', 'api-trigger', 'Succeeded')]), [])
      api.inputStatus = 403
      open()
      const user = userEvent.setup()

      await user.click(await screen.findByRole('button', { name: 'Show submitted data' }))

      expect(await screen.findByText(/You don’t have access to submitted data/)).toBeInTheDocument()
      expect(screen.queryByLabelText('Submitted JSON')).not.toBeInTheDocument()
      expect(screen.queryByText(/INV-1/)).not.toBeInTheDocument()
    })
  })

  it('says so when the execution does not exist', async () => {
    vi.stubGlobal('fetch', vi.fn(async (url: string) => (url === '/api/health' ? json({ status: 'Healthy', totalDurationMs: 1, checks: [] }) : json({ code: 'execution.not_found' }, 404))))
    open()

    expect(await screen.findByText('This execution was not found.')).toBeInTheDocument()
  })
})
