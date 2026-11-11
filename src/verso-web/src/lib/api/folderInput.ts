/** Folder input endpoints (docs/api.md#folder-input). Folders are only ever read by the Worker; the API reports. */
import { request } from './client'
import type { FolderAccessTargetRequest, FolderAccessTestDto, FolderInputStatusDto, FolderLocationDto } from './generated'

export const folderInputApi = {
  /** Approved locations as the Worker reported them (key, name, purpose; never a path). */
  locations: (signal?: AbortSignal) => request<FolderLocationDto[]>('/api/v1/folder-locations', { signal }).then((r) => r.body),

  /** Queues "Test access"; the Worker checks the folders as its own service identity. */
  requestTest: (targets: FolderAccessTargetRequest[]) =>
    request<FolderAccessTestDto>('/api/v1/folder-access-tests', { method: 'POST', body: { targets } }).then((r) => r.body),

  test: (id: string, signal?: AbortSignal) => request<FolderAccessTestDto>(`/api/v1/folder-access-tests/${id}`, { signal }).then((r) => r.body),

  /** Last scan (errors such as an unreachable folder) and recent files of a workflow's folder trigger. */
  status: (workflowId: string, signal?: AbortSignal) =>
    request<FolderInputStatusDto>(`/api/v1/workflows/${workflowId}/folder-input`, { signal }).then((r) => r.body),
}
