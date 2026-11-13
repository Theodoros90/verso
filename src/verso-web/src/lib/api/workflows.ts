/** Workflow endpoints (docs/api.md#workflows). Types come from the generated OpenAPI file. */
import { request, type Tagged } from './client'
import type {
  CreateWorkflowRequest,
  DuplicateWorkflowRequest,
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

  /** `inactive: true` lists the deactivated workflows (so one can be activated again) instead of the active ones. */
  list: (page = 1, pageSize = 100, signal?: AbortSignal, inactive = false) =>
    request<PageResultOfWorkflowSummaryDto>(`${base}?page=${page}&pageSize=${pageSize}${inactive ? '&inactive=true' : ''}`, { signal }).then((r) => r.body),

  /** Stops new runs and the folder trigger's scanning, and moves the workflow to the deactivated list. Nothing is deleted. */
  deactivate: (id: string) => request<WorkflowDto>(`${base}/${id}/deactivate`, { method: 'POST' }).then((r) => r.body),

  activate: (id: string) => request<WorkflowDto>(`${base}/${id}/activate`, { method: 'POST' }).then((r) => r.body),

  /** Copies a workflow into a new one (draft with the same graph) under the given key and name. */
  duplicate: (id: string, body: DuplicateWorkflowRequest) => request<WorkflowDto>(`${base}/${id}/duplicate`, { method: 'POST', body }).then((r) => r.body),

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
