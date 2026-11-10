/**
 * SignalR connection to /hubs/executions (plan §5.5). Messages are notifications only: after one arrives, the
 * caller reads the state from the REST API. Waiting also polls slowly, so a missed or blocked notification
 * (proxy without WebSockets, reconnect) only makes things slower, never stuck.
 */
import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'

import type { PdfPreviewDto } from './api/generated'
import { getAccessToken } from './auth'
import { previewsApi } from './api/templates'

let connection: HubConnection | null = null
let starting: Promise<HubConnection | null> | null = null

/** Called after the connection was re-established: group memberships are gone, so watchers join again and catch up. */
const reconnectHandlers = new Set<() => void>()

/** The shared hub connection, started on first use; null when it can't connect (the caller falls back to polling). */
async function hub(): Promise<HubConnection | null> {
  if (connection?.state === HubConnectionState.Connected) return connection
  if (starting) return starting
  starting = (async () => {
    try {
      if (!connection) {
        connection = new HubConnectionBuilder()
          // The hub needs the same sign-in as the API; browsers can't set headers on WebSockets, so the client
          // sends the token as ?access_token= (the API accepts that on the hub path only).
          .withUrl('/hubs/executions', { accessTokenFactory: async () => (await getAccessToken()) ?? '' })
          .withAutomaticReconnect()
          .configureLogging(LogLevel.Warning)
          .build()
        connection.onreconnected(() => reconnectHandlers.forEach((handler) => handler()))
      }
      if (connection.state === HubConnectionState.Disconnected) await connection.start()
      return connection
    } catch {
      return null
    } finally {
      starting = null
    }
  })()
  return starting
}

export type ExecutionTarget = { kind: 'execution'; id: string } | { kind: 'workflow'; id: string } | { kind: 'all' }

const SUBSCRIBE: Record<ExecutionTarget['kind'], [string, string]> = {
  execution: ['SubscribeExecution', 'UnsubscribeExecution'],
  workflow: ['SubscribeWorkflow', 'UnsubscribeWorkflow'],
  all: ['SubscribeExecutions', 'UnsubscribeExecutions'],
}

/**
 * Calls `onChange` whenever the API says something changed for the target (ExecutionUpdated / StepUpdated), and again
 * after a reconnect, once the subscription is renewed. The message carries no state: the caller re-reads the REST API
 * (and catches up with `GET /executions/{id}/events?after=`). Returns the unsubscribe function. When the hub can't
 * connect it returns a no-op and the caller's own polling keeps the view current.
 */
export async function watchExecutions(target: ExecutionTarget, onChange: () => void): Promise<() => void> {
  const conn = await hub()
  if (!conn) return () => undefined

  let active = true
  const [subscribeMethod, unsubscribeMethod] = SUBSCRIBE[target.kind]
  const args = target.kind === 'all' ? [] : [target.id]
  const onMessage = (message: { executionId?: string }) => {
    if (!active) return
    if (target.kind === 'execution' && message?.executionId !== target.id) return
    onChange()
  }
  const rejoin = () => {
    if (!active) return
    void conn
      .invoke(subscribeMethod, ...args)
      .catch(() => undefined)
      .then(onChange)
  }

  conn.on('ExecutionUpdated', onMessage)
  conn.on('StepUpdated', onMessage)
  reconnectHandlers.add(rejoin)
  await conn.invoke(subscribeMethod, ...args).catch(() => undefined)

  return () => {
    active = false
    reconnectHandlers.delete(rejoin)
    conn.off('ExecutionUpdated', onMessage)
    conn.off('StepUpdated', onMessage)
    conn.invoke(unsubscribeMethod, ...args).catch(() => undefined)
  }
}

const FINISHED = new Set(['Succeeded', 'Failed'])
export const POLL_INTERVAL_MS = 3000

/** Resolves with the preview once it has succeeded or failed. Rejects with an AbortError when aborted. */
export async function waitForPreview(previewId: string, signal: AbortSignal): Promise<PdfPreviewDto> {
  const conn = await hub()
  let wake: (() => void) | null = null
  const onReady = (msg: { previewId?: string }) => {
    if (msg?.previewId === previewId) wake?.()
  }
  conn?.on('PreviewReady', onReady)
  try {
    await conn?.invoke('SubscribePreview', previewId).catch(() => undefined)
    for (;;) {
      // Read first: the preview may have finished before we subscribed.
      const preview = await previewsApi.get(previewId, signal)
      if (FINISHED.has(preview.status)) return preview
      await new Promise<void>((resolve, reject) => {
        const timer = setTimeout(resolve, POLL_INTERVAL_MS)
        wake = () => {
          clearTimeout(timer)
          resolve()
        }
        signal.addEventListener(
          'abort',
          () => {
            clearTimeout(timer)
            reject(new DOMException('Aborted', 'AbortError'))
          },
          { once: true },
        )
      })
    }
  } finally {
    conn?.off('PreviewReady', onReady)
    conn?.invoke('UnsubscribePreview', previewId).catch(() => undefined)
  }
}
