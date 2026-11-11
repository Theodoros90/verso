/** Administration of approved folder locations (docs/api.md#folder-locations-administration). Admin role only. */
import { request } from './client'
import type { AdminFolderLocationDto, SaveFolderLocationRequest } from './generated'

const base = '/api/v1/admin/folder-locations'

export const adminFoldersApi = {
  list: (signal?: AbortSignal) => request<AdminFolderLocationDto[]>(base, { signal }).then((r) => r.body),

  save: (key: string, body: SaveFolderLocationRequest) =>
    request<AdminFolderLocationDto>(`${base}/${encodeURIComponent(key)}`, { method: 'PUT', body }).then((r) => r.body),

  remove: (key: string) => request<unknown>(`${base}/${encodeURIComponent(key)}`, { method: 'DELETE' }).then(() => undefined),
}
