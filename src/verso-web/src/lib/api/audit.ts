/** Audit trail endpoints (docs/api.md#audit). Auditor and Admin only. */
import { request } from './client'
import type { PageResultOfAuditEntryDto } from './generated'

export interface AuditFilter {
  action?: string
  actorId?: string
  entityType?: string
  entityId?: string
  /** ISO 8601 instants; `from` inclusive, `to` exclusive. */
  from?: string
  to?: string
}

export const auditApi = {
  list: (filter: AuditFilter, page = 1, pageSize = 50, signal?: AbortSignal) => {
    const params = new URLSearchParams({ newestFirst: 'true', page: String(page), pageSize: String(pageSize) })
    for (const [key, value] of Object.entries(filter)) if (value) params.set(key, value)
    return request<PageResultOfAuditEntryDto>(`/api/v1/audit?${params}`, { signal }).then((r) => r.body)
  },

  actions: (signal?: AbortSignal) => request<string[]>('/api/v1/audit/actions', { signal }).then((r) => r.body),
}
