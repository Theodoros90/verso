import { beforeEach, describe, expect, it, vi } from 'vitest'

// A fake hub: tests can make it fail to connect, and can push 'PreviewReady' to the registered handler.
const handlers = new Map<string, (msg: unknown) => void>()
const hubState = { failStart: false, invoked: [] as unknown[][] }

vi.mock('@microsoft/signalr', () => {
  class FakeConnection {
    state = 'Disconnected'
    async start() {
      if (hubState.failStart) throw new Error('no websocket')
      this.state = 'Connected'
    }
    on(name: string, fn: (msg: unknown) => void) {
      handlers.set(name, fn)
    }
    off(name: string) {
      handlers.delete(name)
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

const preview = (status: string) => ({
  previewId: 'p1',
  templateId: 't1',
  status,
  issues: [],
  error: null,
  sizeBytes: status === 'Succeeded' ? 1000 : null,
  createdAt: '2026-11-09T10:00:00Z',
  completedAt: null,
})
const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })

describe('waitForPreview', () => {
  beforeEach(() => {
    vi.resetModules()
    handlers.clear()
    hubState.failStart = false
    hubState.invoked = []
  })

  it('subscribes, and a PreviewReady notification wakes it to read the finished state', async () => {
    const statuses = ['Running', 'Succeeded']
    vi.stubGlobal('fetch', vi.fn(async () => json(preview(statuses.shift() ?? 'Succeeded'))))
    const { waitForPreview } = await import('./realtime')

    const done = waitForPreview('p1', new AbortController().signal)
    await vi.waitFor(() => expect(handlers.has('PreviewReady')).toBe(true))
    await vi.waitFor(() => expect(fetch).toHaveBeenCalledTimes(1))
    handlers.get('PreviewReady')!({ previewId: 'p1', status: 'Succeeded' })

    await expect(done).resolves.toMatchObject({ status: 'Succeeded' })
    expect(hubState.invoked).toContainEqual(['SubscribePreview', 'p1'])
    expect(hubState.invoked).toContainEqual(['UnsubscribePreview', 'p1'])
  })

  it('returns at once when the preview already finished before subscribing', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json(preview('Failed'))))
    const { waitForPreview } = await import('./realtime')

    await expect(waitForPreview('p1', new AbortController().signal)).resolves.toMatchObject({ status: 'Failed' })
  })

  it('falls back to polling when the hub cannot connect', async () => {
    hubState.failStart = true
    vi.useFakeTimers({ shouldAdvanceTime: true })
    const statuses = ['Queued', 'Succeeded']
    vi.stubGlobal('fetch', vi.fn(async () => json(preview(statuses.shift() ?? 'Succeeded'))))
    const { waitForPreview, POLL_INTERVAL_MS } = await import('./realtime')

    const done = waitForPreview('p1', new AbortController().signal)
    await vi.waitFor(() => expect(fetch).toHaveBeenCalledTimes(1))
    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)

    await expect(done).resolves.toMatchObject({ status: 'Succeeded' })
    vi.useRealTimers()
  })

  it('rejects with AbortError when cancelled while waiting', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json(preview('Running'))))
    const { waitForPreview } = await import('./realtime')
    const controller = new AbortController()

    const done = waitForPreview('p1', controller.signal)
    await vi.waitFor(() => expect(fetch).toHaveBeenCalledTimes(1))
    controller.abort()

    await expect(done).rejects.toThrow('Aborted')
  })
})
