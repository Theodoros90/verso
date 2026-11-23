/** SMS connection endpoints (docs/api.md#sms-connections, ADR 0018). Administrators only. The API key is write-only: no response has it. */
import { request } from './client'
import type { SaveSmsConnectionRequest, SmsConnectionDto, SmsConnectionOptionDto, SmsConnectionTestDto } from './generated'

const base = '/api/v1/sms-connections'

export type SmsConnectionInput = Partial<SaveSmsConnectionRequest>

export const smsConnectionsApi = {
  list: (includeInactive = true, signal?: AbortSignal) =>
    request<SmsConnectionDto[]>(`${base}?includeInactive=${includeInactive}`, { signal }).then((r) => r.body),

  /** Key, name, type and sending switch of the active connections, for the Send SMS step (workflow authors and administrators). */
  listOptions: (signal?: AbortSignal) => request<SmsConnectionOptionDto[]>('/api/v1/sms-connection-options', { signal }).then((r) => r.body),

  create: (body: SmsConnectionInput) => request<SmsConnectionDto>(base, { method: 'POST', body }).then((r) => r.body),

  update: (id: string, body: SmsConnectionInput) => request<SmsConnectionDto>(`${base}/${id}`, { method: 'PUT', body }).then((r) => r.body),

  deactivate: (id: string) => request<SmsConnectionDto>(`${base}/${id}/deactivate`, { method: 'POST' }).then((r) => r.body),

  activate: (id: string) => request<SmsConnectionDto>(`${base}/${id}/activate`, { method: 'POST' }).then((r) => r.body),

  /** Queues "Test connection": the Worker sends ONE real SMS to `recipient` (international format). */
  requestTest: (id: string, recipient: string) =>
    request<SmsConnectionTestDto>(`${base}/${id}/tests`, { method: 'POST', body: { recipient } }).then((r) => r.body),

  getTest: (testId: string, signal?: AbortSignal) => request<SmsConnectionTestDto>(`${base}/tests/${testId}`, { signal }).then((r) => r.body),
}
