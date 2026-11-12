/**
 * Minimal JSON client for /api/v1. Sends the signed-in user's bearer token. Errors become ApiError with the
 * RFC 9457 problem body (docs/api.md).
 */
import { getAccessToken, UNAUTHORIZED_EVENT } from '../auth'

export interface Problem {
  title?: string | null
  detail?: string | null
  status?: number | null
  code?: string
  traceId?: string
  errors?: Record<string, string[]>
}

export class ApiError extends Error {
  readonly status: number
  readonly problem: Problem

  constructor(status: number, problem: Problem) {
    super(problem.detail ?? problem.title ?? `Request failed (HTTP ${status}).`)
    this.name = 'ApiError'
    this.status = status
    this.problem = problem
  }

  get code(): string | undefined {
    return this.problem.code
  }
}

export interface Tagged<T> {
  body: T
  etag: string | null
}

interface RequestOptions {
  method?: string
  body?: unknown
  ifMatch?: string
  signal?: AbortSignal
}

async function send(url: string, init: RequestInit, accept: string): Promise<Response> {
  const headers = new Headers(init.headers)
  headers.set('Accept', accept)
  const token = await getAccessToken()
  if (token) headers.set('Authorization', `Bearer ${token}`)
  let response: Response
  try {
    response = await fetch(url, { ...init, headers })
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error
    throw new ApiError(0, { title: 'The API did not respond. Is Verso.Api running?' })
  }
  if (response.status === 401 && token) window.dispatchEvent(new Event(UNAUTHORIZED_EVENT))
  return response
}

async function problemOf(response: Response): Promise<ApiError> {
  let problem: Problem = { title: `Request failed (HTTP ${response.status}).` }
  try {
    const text = await response.text()
    if (text) problem = JSON.parse(text) as Problem
  } catch {
    // keep the generic problem
  }
  if (response.status === 502 || response.status === 504) {
    problem = { title: 'The API did not respond. Is Verso.Api running?' }
  }
  return new ApiError(response.status, problem)
}

/** Binary content (a PDF or Word file), fetched with the user's token. */
export async function requestBlob(url: string, signal?: AbortSignal): Promise<Blob> {
  const response = await send(url, { signal }, 'application/pdf, application/vnd.openxmlformats-officedocument.wordprocessingml.document, application/problem+json')
  if (!response.ok) throw await problemOf(response)
  return response.blob()
}

/**
 * Downloads an authenticated file: a plain link can't carry the bearer token, so the bytes are fetched and handed
 * to the browser as a download.
 */
export async function downloadFile(url: string, fileName: string): Promise<void> {
  const blob = await requestBlob(url)
  const objectUrl = URL.createObjectURL(blob)
  try {
    const link = document.createElement('a')
    link.href = objectUrl
    link.download = fileName
    document.body.append(link)
    link.click()
    link.remove()
  } finally {
    setTimeout(() => URL.revokeObjectURL(objectUrl), 10_000)
  }
}

export async function request<T>(url: string, options: RequestOptions = {}): Promise<Tagged<T>> {
  const headers: Record<string, string> = {}
  if (options.body !== undefined) headers['Content-Type'] = 'application/json'
  if (options.ifMatch) headers['If-Match'] = options.ifMatch

  const response = await send(
    url,
    {
      method: options.method ?? 'GET',
      headers,
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      signal: options.signal,
    },
    'application/json',
  )

  if (!response.ok) throw await problemOf(response)

  const text = await response.text()
  return { body: (text ? JSON.parse(text) : undefined) as T, etag: response.headers.get('ETag') }
}
