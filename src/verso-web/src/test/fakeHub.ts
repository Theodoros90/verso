/**
 * A fake SignalR connection for component tests. Use it from a test file with:
 *   vi.mock('@microsoft/signalr', async () => (await import('@/test/fakeHub')).fakeSignalR)
 * Tests push notifications through `hub.handlers` and read the calls in `hub.invoked`.
 */
export const hub = {
  handlers: new Map<string, (msg: unknown) => void>(),
  invoked: [] as unknown[][],
  reset() {
    hub.handlers.clear()
    hub.invoked = []
  },
}

class FakeConnection {
  state = 'Disconnected'
  async start() {
    this.state = 'Connected'
  }
  on(name: string, fn: (msg: unknown) => void) {
    hub.handlers.set(name, fn)
  }
  off(name: string) {
    hub.handlers.delete(name)
  }
  onreconnected() {
    // not needed by these tests
  }
  async invoke(...args: unknown[]) {
    hub.invoked.push(args)
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

export const fakeSignalR = { HubConnectionBuilder, HubConnectionState: { Connected: 'Connected', Disconnected: 'Disconnected' }, LogLevel: { Warning: 3 } }
