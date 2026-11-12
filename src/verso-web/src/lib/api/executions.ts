/** Execution and document endpoints (docs/api.md#executions, #documents). Types come from the generated OpenAPI file. */
import { request } from './client'
import type {
  ArchivedDocumentDto,
  ExecutionDto,
  ExecutionEventDto,
  ExecutionInputDto,
  PageResultOfArchivedDocumentDto,
  PageResultOfExecutionSummaryDto,
} from './generated'

export interface ExecutionFilter {
  workflow?: string
  status?: string
  /** ISO 8601 instants; `from` is inclusive, `to` exclusive. */
  from?: string
  to?: string
  correlationId?: string
}

function query(params: Record<string, string | number | undefined>): string {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) if (value !== undefined && value !== '') search.set(key, String(value))
  const text = search.toString()
  return text ? `?${text}` : ''
}

export const executionsApi = {
  list: (filter: ExecutionFilter, page = 1, pageSize = 25, signal?: AbortSignal) =>
    request<PageResultOfExecutionSummaryDto>(`/api/v1/executions${query({ ...filter, page, pageSize })}`, { signal }).then((r) => r.body),

  get: (id: string, signal?: AbortSignal) => request<ExecutionDto>(`/api/v1/executions/${id}`, { signal }).then((r) => r.body),

  /** Events after the given id (0 = all), oldest first: the catch-up read for live progress. */
  events: (id: string, after = 0, signal?: AbortSignal) =>
    request<ExecutionEventDto[]>(`/api/v1/executions/${id}/events${query({ after })}`, { signal }).then((r) => r.body),

  /** The submitted payload. 403 `forbidden` unless the user is an Auditor or Admin; every read is audited. */
  input: (id: string, signal?: AbortSignal) => request<ExecutionInputDto>(`/api/v1/executions/${id}/input`, { signal }).then((r) => r.body),

  retry: (id: string) => request<ExecutionDto>(`/api/v1/executions/${id}/retry`, { method: 'POST' }).then((r) => r.body),

  /** Approves or declines the Approval step waiting in a run. 403 unless the user holds the step's approver role (or is an administrator). */
  decide: (id: string, nodeId: string, decision: 'approved' | 'declined', comment: string, iteration = 0) =>
    request<ExecutionDto>(`/api/v1/executions/${id}/approvals/${encodeURIComponent(nodeId)}`, { method: 'POST', body: { decision, comment: comment.trim() || undefined, iteration } }).then((r) => r.body),
}

export const documentsApi = {
  search: (text: string, from: string | undefined, to: string | undefined, page = 1, pageSize = 25, signal?: AbortSignal) =>
    request<PageResultOfArchivedDocumentDto>(`/api/v1/documents${query({ q: text.trim(), from, to, page, pageSize })}`, { signal }).then((r) => r.body),

  get: (id: string, signal?: AbortSignal) => request<ArchivedDocumentDto>(`/api/v1/documents/${id}`, { signal }).then((r) => r.body),

  /** A plain link: the browser downloads it (the API verifies the SHA-256 and audits the download). */
  contentUrl: (id: string) => `/api/v1/documents/${id}/content`,
}
