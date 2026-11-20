/** API connection endpoints (docs/api.md#api-connections, ADR 0017). Administrators only. The secret is write-only: no response has it. */
import { request } from './client'
import type { ApiConnectionDto, ApiConnectionOptionDto, ApiConnectionTestDto, SaveApiConnectionRequest } from './generated'

const base = '/api/v1/api-connections'

export type ApiConnectionInput = Partial<SaveApiConnectionRequest>

export const apiConnectionsApi = {
  list: (includeInactive = true, signal?: AbortSignal) =>
    request<ApiConnectionDto[]>(`${base}?includeInactive=${includeInactive}`, { signal }).then((r) => r.body),

  /** Key, name and host of the active connections, for the Fetch from API step (workflow authors and administrators). */
  listOptions: (signal?: AbortSignal) => request<ApiConnectionOptionDto[]>('/api/v1/api-connection-options', { signal }).then((r) => r.body),

  create: (body: ApiConnectionInput) => request<ApiConnectionDto>(base, { method: 'POST', body }).then((r) => r.body),

  update: (id: string, body: ApiConnectionInput) => request<ApiConnectionDto>(`${base}/${id}`, { method: 'PUT', body }).then((r) => r.body),

  deactivate: (id: string) => request<ApiConnectionDto>(`${base}/${id}/deactivate`, { method: 'POST' }).then((r) => r.body),

  activate: (id: string) => request<ApiConnectionDto>(`${base}/${id}/activate`, { method: 'POST' }).then((r) => r.body),

  /** Queues "Test connection": the Worker sends one GET to the test path. */
  requestTest: (id: string) => request<ApiConnectionTestDto>(`${base}/${id}/tests`, { method: 'POST' }).then((r) => r.body),

  getTest: (testId: string, signal?: AbortSignal) => request<ApiConnectionTestDto>(`${base}/tests/${testId}`, { signal }).then((r) => r.body),
}
