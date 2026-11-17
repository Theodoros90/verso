/** Database connection endpoints (docs/api.md#database-connections, ADR 0016). Administrators only. The password is write-only: no response has it. */
import { request } from './client'
import type { DatabaseConnectionDto, DatabaseConnectionTestDto, SaveDatabaseConnectionRequest } from './generated'

const base = '/api/v1/database-connections'

export type DatabaseConnectionInput = Partial<SaveDatabaseConnectionRequest>

export const databaseConnectionsApi = {
  list: (includeInactive = true, signal?: AbortSignal) =>
    request<DatabaseConnectionDto[]>(`${base}?includeInactive=${includeInactive}`, { signal }).then((r) => r.body),

  create: (body: DatabaseConnectionInput) => request<DatabaseConnectionDto>(base, { method: 'POST', body }).then((r) => r.body),

  update: (id: string, body: DatabaseConnectionInput) => request<DatabaseConnectionDto>(`${base}/${id}`, { method: 'PUT', body }).then((r) => r.body),

  deactivate: (id: string) => request<DatabaseConnectionDto>(`${base}/${id}/deactivate`, { method: 'POST' }).then((r) => r.body),

  activate: (id: string) => request<DatabaseConnectionDto>(`${base}/${id}/activate`, { method: 'POST' }).then((r) => r.body),

  /** Queues "Test connection": the Worker connects and runs SELECT 1 (nothing else). */
  requestTest: (id: string) => request<DatabaseConnectionTestDto>(`${base}/${id}/tests`, { method: 'POST' }).then((r) => r.body),

  getTest: (testId: string, signal?: AbortSignal) => request<DatabaseConnectionTestDto>(`${base}/tests/${testId}`, { signal }).then((r) => r.body),
}
