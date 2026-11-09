// GENERATED from docs/openapi/v1.json by scripts/generate-api-types.mjs. Do not edit; run `npm run gen:api`.
/* eslint-disable */

export interface AuditEntryDto {
  id: string
  sequence: number
  occurredAt: string
  actorId: string
  actorName: string
  action: string
  entityType: string
  entityId: string
  details: null | JsonElement
  traceId: string | null
}

export interface CreateTemplateRequest {
  key: string | null
  name: string | null
  description: string | null
  documentType: string | null
}

export interface CreateWorkflowRequest {
  key: string | null
  name: string | null
  description: string | null
}

export interface HealthCheckEntry {
  name: string
  status: string
  description: string | null
  durationMs: number
  data: Record<string, unknown> | null
}

export interface HealthResponse {
  status: string
  totalDurationMs: number
  checks: HealthCheckEntry[]
}

export interface HtmlPreviewDto {
  html: string
  issues: RenderIssueDto[]
}

export interface HttpValidationProblemDetails {
  type?: string | null
  title?: string | null
  status?: number | null
  detail?: string | null
  instance?: string | null
  errors?: Record<string, string[]>
}

export type JsonElement = unknown

export interface MarginsDto {
  top: number
  right: number
  bottom: number
  left: number
}

export interface NodeTypeDto {
  type: string
  label: string
  category: string
  description: string
  inputs: PortDto[]
  outputs: PortDto[]
  configSchema: JsonElement
  defaults: JsonElement
}

export interface PageResultOfAuditEntryDto {
  items: AuditEntryDto[]
  page: number
  pageSize: number
  total: number
}

export interface PageResultOfTemplateSummaryDto {
  items: TemplateSummaryDto[]
  page: number
  pageSize: number
  total: number
}

export interface PageResultOfWorkflowSummaryDto {
  items: WorkflowSummaryDto[]
  page: number
  pageSize: number
  total: number
}

export interface PageSettingsDto {
  size: string | null
  orientation: string | null
  marginsMm: null | MarginsDto
}

export interface PdfPreviewDto {
  previewId: string
  templateId: string
  status: string
  issues: RenderIssueDto[]
  error: string | null
  sizeBytes: number | null
  createdAt: string
  completedAt: string | null
}

export interface PortDto {
  id: string
  label: string
  required: boolean
}

export interface PreviewRequest {
  data: null | JsonElement
}

export interface ProblemDetails {
  type?: string | null
  title?: string | null
  status?: number | null
  detail?: string | null
  instance?: string | null
}

export interface RenderIssueDto {
  severity: string
  code: string
  message: string
  elementId: string
  path: string | null
}

export interface SaveDraftRequest {
  pageSettings: null | PageSettingsDto
  layout: null | JsonElement
  dataSchema: null | JsonElement
  sampleData: null | JsonElement
}

export interface SaveWorkflowDraftRequest {
  definition: null | JsonElement
}

export interface TemplateDto {
  id: string
  key: string
  name: string
  description: string | null
  documentType: string
  createdAt: string
  createdBy: string
  latestPublishedVersion: number | null
  draftVersionId: string | null
}

export interface TemplateSummaryDto {
  id: string
  key: string
  name: string
  documentType: string
  latestPublishedVersion: number | null
  hasDraft: boolean
  updatedAt: string
}

export interface TemplateVersionDto {
  versionId: string
  templateId: string
  versionNumber: number | null
  status: string
  pageSettings: PageSettingsDto
  layout: JsonElement
  dataSchema: JsonElement
  sampleData: JsonElement
  contentHash: string | null
  createdAt: string
  createdBy: string
  updatedAt: string
  updatedBy: string
  publishedAt: string | null
  publishedBy: string | null
}

export interface TemplateVersionSummaryDto {
  versionId: string
  versionNumber: number | null
  status: string
  contentHash: string | null
  updatedAt: string
  updatedBy: string
  publishedAt: string | null
  publishedBy: string | null
}

export interface ValidateWorkflowRequest {
  definition: null | JsonElement
}

export interface WorkflowDto {
  id: string
  key: string
  name: string
  description: string | null
  createdAt: string
  createdBy: string
  latestPublishedVersion: number | null
  draftVersionId: string | null
}

export interface WorkflowIssueDto {
  severity: string
  code: string
  message: string
  nodeId?: string | null
  edgeId?: string | null
  port?: string | null
  field?: string | null
}

export interface WorkflowSummaryDto {
  id: string
  key: string
  name: string
  latestPublishedVersion: number | null
  hasDraft: boolean
  updatedAt: string
}

export interface WorkflowValidationDto {
  valid: boolean
  issues: WorkflowIssueDto[]
}

export interface WorkflowVersionDto {
  versionId: string
  workflowId: string
  versionNumber: number | null
  status: string
  definition: JsonElement
  contentHash: string | null
  createdAt: string
  createdBy: string
  updatedAt: string
  updatedBy: string
  publishedAt: string | null
  publishedBy: string | null
}

export interface WorkflowVersionSummaryDto {
  versionId: string
  versionNumber: number | null
  status: string
  contentHash: string | null
  updatedAt: string
  updatedBy: string
  publishedAt: string | null
  publishedBy: string | null
}
