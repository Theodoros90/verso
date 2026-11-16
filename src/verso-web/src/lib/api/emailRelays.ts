/** Email relay endpoints (docs/api.md#email-relays, ADR 0015). Administrators only. The password is write-only: no response has it. */
import { request } from './client'
import type { EmailRelayDto, EmailRelayOptionDto, EmailRelayTestDto, SaveEmailRelayRequest } from './generated'

const base = '/api/v1/email-relays'

export type EmailRelayInput = Partial<SaveEmailRelayRequest>

export const emailRelaysApi = {
  list: (includeInactive = true, signal?: AbortSignal) =>
    request<EmailRelayDto[]>(`${base}?includeInactive=${includeInactive}`, { signal }).then((r) => r.body),

  create: (body: EmailRelayInput) => request<EmailRelayDto>(base, { method: 'POST', body }).then((r) => r.body),

  update: (id: string, body: EmailRelayInput) => request<EmailRelayDto>(`${base}/${id}`, { method: 'PUT', body }).then((r) => r.body),

  deactivate: (id: string) => request<EmailRelayDto>(`${base}/${id}/deactivate`, { method: 'POST' }).then((r) => r.body),

  /** Key and name of every relay, for the Send email step's dropdown (any signed-in role). */
  listOptions: (signal?: AbortSignal) => request<EmailRelayOptionDto[]>('/api/v1/email-relay-options', { signal }).then((r) => r.body),

  /** Queues "Test connection": the Worker sends one test message to `toAddress` through the saved relay. */
  requestTest: (id: string, toAddress: string) => request<EmailRelayTestDto>(`${base}/${id}/tests`, { method: 'POST', body: { toAddress } }).then((r) => r.body),

  getTest: (testId: string, signal?: AbortSignal) => request<EmailRelayTestDto>(`${base}/tests/${testId}`, { signal }).then((r) => r.body),

  activate: (id: string) => request<EmailRelayDto>(`${base}/${id}/activate`, { method: 'POST' }).then((r) => r.body),
}
