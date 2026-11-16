/** Document category endpoints (docs/api.md#document-categories). Types come from the generated OpenAPI file. */
import { request } from './client'
import type { DocumentCategoryDto } from './generated'

const base = '/api/v1/document-categories'

export const documentCategoriesApi = {
  list: (includeInactive = false, signal?: AbortSignal) =>
    request<DocumentCategoryDto[]>(`${base}?includeInactive=${includeInactive}`, { signal }).then((r) => r.body),

  create: (name: string) => request<DocumentCategoryDto>(base, { method: 'POST', body: { name } }).then((r) => r.body),

  rename: (id: string, name: string) => request<DocumentCategoryDto>(`${base}/${id}`, { method: 'PUT', body: { name } }).then((r) => r.body),

  deactivate: (id: string) => request<DocumentCategoryDto>(`${base}/${id}/deactivate`, { method: 'POST' }).then((r) => r.body),

  activate: (id: string) => request<DocumentCategoryDto>(`${base}/${id}/activate`, { method: 'POST' }).then((r) => r.body),
}
