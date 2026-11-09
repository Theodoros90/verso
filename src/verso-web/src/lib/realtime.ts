/**
 * SignalR connection to /hubs/executions (plan §5.5). Messages are notifications only: after one arrives, the
 * caller reads the state from the REST API. Waiting also polls slowly, so a missed or blocked notification
 * (proxy without WebSockets, reconnect) only makes things slower, never stuck.
 */
import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'

import type { PdfPreviewDto } from './api/generated'
import { previewsApi } from './api/templates'

let connection: HubConnection | null = null
let starting: Promise<HubConnection | null> | null = null

/** The shared hub connection, started on first use; null when it can't connect (the caller falls back to polling). */
async function hub(): Promise<HubConnection | null> {
  if (connection?.state === HubConnectionState.Connected) return connection
  if (starting) return starting
  starting = (async () => {
    try {
      connection ??= new HubConnectionBuilder().withUrl('/hubs/executions').withAutomaticReconnect().configureLogging(LogLevel.Warning).build()
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
