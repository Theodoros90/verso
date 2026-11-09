/** Workflow endpoints (docs/api.md#workflows). Types come from the generated OpenAPI file. */
import { request, type Tagged } from './client'
import type {
  CreateWorkflowRequest,
  NodeTypeDto,
  PageResultOfWorkflowSummaryDto,
  WorkflowDto,
  WorkflowValidationDto,
  WorkflowVersionDto,
  WorkflowVersionSummaryDto,
} from './generated'

const base = '/api/v1/workflows'

export const workflowsApi = {
  nodeTypes: (signal?: AbortSignal) => request<NodeTypeDto[]>('/api/v1/node-types', { signal }).then((r) => r.body),

  list: (page = 1, pageSize = 100, signal?: AbortSignal) =>
    request<PageResultOfWorkflowSummaryDto>(`${base}?page=${page}&pageSize=${pageSize}`, { signal }).then((r) => r.body),

  create: (body: CreateWorkflowRequest) => request<WorkflowDto>(base, { method: 'POST', body }).then((r) => r.body),

  get: (id: string, signal?: AbortSignal) => request<WorkflowDto>(`${base}/${id}`, { signal }).then((r) => r.body),

  versions: (id: string, signal?: AbortSignal) =>
    request<WorkflowVersionSummaryDto[]>(`${base}/${id}/versions`, { signal }).then((r) => r.body),

  version: (id: string, versionId: string, signal?: AbortSignal): Promise<Tagged<WorkflowVersionDto>> =>
    request<WorkflowVersionDto>(`${base}/${id}/versions/${versionId}`, { signal }),

  draft: (id: string, signal?: AbortSignal): Promise<Tagged<WorkflowVersionDto>> => request<WorkflowVersionDto>(`${base}/${id}/draft`, { signal }),

  startDraft: (id: string): Promise<Tagged<WorkflowVersionDto>> => request<WorkflowVersionDto>(`${base}/${id}/draft`, { method: 'POST' }),

  saveDraft: (id: string, definition: unknown, etag: string): Promise<Tagged<WorkflowVersionDto>> =>
    request<WorkflowVersionDto>(`${base}/${id}/draft`, { method: 'PUT', body: { definition }, ifMatch: etag }),

  /** Validates the given (possibly unsaved) definition. */
  validate: (id: string, definition: unknown, signal?: AbortSignal) =>
    request<WorkflowValidationDto>(`${base}/${id}/draft/validate`, { method: 'POST', body: { definition }, signal }).then((r) => r.body),

  publish: (id: string, etag: string): Promise<Tagged<WorkflowVersionDto>> =>
    request<WorkflowVersionDto>(`${base}/${id}/draft/publish`, { method: 'POST', ifMatch: etag }),
}
