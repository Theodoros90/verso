/** Minimal JSON client for /api/v1. Errors become ApiError with the RFC 9457 problem body (docs/api.md). */

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

export async function request<T>(url: string, options: RequestOptions = {}): Promise<Tagged<T>> {
  const headers: Record<string, string> = { Accept: 'application/json' }
  if (options.body !== undefined) headers['Content-Type'] = 'application/json'
  if (options.ifMatch) headers['If-Match'] = options.ifMatch

  let response: Response
  try {
    response = await fetch(url, {
      method: options.method ?? 'GET',
      headers,
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      signal: options.signal,
    })
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error
    throw new ApiError(0, { title: 'The API did not respond. Is Verso.Api running?' })
  }

  if (!response.ok) {
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
    throw new ApiError(response.status, problem)
  }

  const text = await response.text()
  return { body: (text ? JSON.parse(text) : undefined) as T, etag: response.headers.get('ETag') }
}
