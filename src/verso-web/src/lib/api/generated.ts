// GENERATED from docs/openapi/v1.json by scripts/generate-api-types.mjs. Do not edit; run `npm run gen:api`.
/* eslint-disable */

export interface ArchiveDto {
  archivedAt: string
  folderPath: string
  retention: string
  retentionUntil: string | null
  metadata: Record<string, string>
}

export interface ArchivedDocumentDto {
  id: string
  executionId: string
  nodeId: string
  templateVersionId: string
  fileName: string
  contentType: string
  sizeBytes: number
  sha256: string
  status: string
  createdAt: string
  archivedAt: string
  folderPath: string
  retention: string
  retentionUntil: string | null
  metadata: Record<string, string>
}

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

export interface AuthConfigDto {
  mode: string
  entra: null | EntraConfigDto
  devUsers: DevUserDto[] | null
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

export interface DevUserDto {
  id: string
  name: string
  roles: string[]
  token: string
}

export interface EntraConfigDto {
  authority: string
  clientId: string
  scope: string
}

export interface ExecutionDocumentDto {
  id: string
  nodeId: string
  templateVersionId: string
  fileName: string
  contentType: string
  sizeBytes: number
  sha256: string
  status: string
  createdAt: string
  archive: null | ArchiveDto
}

export interface ExecutionDto {
  id: string
  status: string
  workflow: ExecutionWorkflowDto
  templateVersions: Record<string, string>
  idempotencyKey: string
  correlationId: string
  inputHash: string
  submittedAt: string
  submittedBy: string
  startedAt: string | null
  completedAt: string | null
  attempt: number
  failureCode: string | null
  failureMessage: string | null
  steps: ExecutionStepDto[]
  documents: ExecutionDocumentDto[]
}

export interface ExecutionEventDto {
  id: number
  executionId: string
  nodeId: string | null
  type: string
  occurredAt: string
  data: null | JsonElement
}

export interface ExecutionInputDto {
  executionId: string
  inputHash: string
  data: JsonElement
}

export interface ExecutionStepDto {
  nodeId: string
  type: string
  title: string
  sequence: number
  status: string
  outputPort: string | null
  attempt: number
  startedAt: string | null
  completedAt: string | null
  durationMs: number | null
  error: string | null
  result: null | JsonElement
}

export interface ExecutionSummaryDto {
  id: string
  status: string
  workflowKey: string
  workflowVersion: number
  correlationId: string
  submittedAt: string
  completedAt: string | null
  attempt: number
  failureCode: string | null
  failureMessage: string | null
}

export interface ExecutionWorkflowDto {
  id: string
  key: string
  version: number
  versionId: string
}

export interface FolderAccessResultDto {
  role: string
  location: string
  subfolder: string
  ok: boolean
  checks: FolderCheckDto[]
}

export interface FolderAccessTargetDto {
  role: string
  location: string
  subfolder: string
}

export interface FolderAccessTargetRequest {
  role: string | null
  location: string | null
  subfolder: string | null
  path?: string | null
}

export interface FolderAccessTestDto {
  id: string
  status: string
  targets: FolderAccessTargetDto[]
  results: FolderAccessResultDto[]
  identity: string | null
  machine: string | null
  error: string | null
  requestedAt: string
  completedAt: string | null
}

export interface FolderAccessTestRequest {
  targets: FolderAccessTargetRequest[] | null
}

export interface FolderCheckDto {
  name: string
  ok: boolean
  message: string
}

export interface FolderFileDto {
  id: string
  fileName: string
  relativePath: string
  sizeBytes: number
  status: string
  outcome: string | null
  errorCode: string | null
  errorMessage: string | null
  executionId: string | null
  deliveredName: string | null
  deliveryError: string | null
  claimedAt: string
  completedAt: string | null
}

export interface FolderInputStatusDto {
  watch: null | FolderWatchDto
  files: FolderFileDto[]
}

export interface FolderLocationDto {
  key: string
  name: string
  input: boolean
  output: boolean
  enabled: boolean
  problem: string | null
  updatedAt: string
}

export interface FolderWatchDto {
  nodeId: string
  lastScanAt: string | null
  lastStatus: string | null
  lastError: string | null
  lastFilesFound: number
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

export interface MeDto {
  id: string
  name: string
  roles: string[]
  permissions: PermissionsDto
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

export interface PageResultOfArchivedDocumentDto {
  items: ArchivedDocumentDto[]
  page: number
  pageSize: number
  total: number
}

export interface PageResultOfAuditEntryDto {
  items: AuditEntryDto[]
  page: number
  pageSize: number
  total: number
}

export interface PageResultOfExecutionSummaryDto {
  items: ExecutionSummaryDto[]
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

export interface PermissionsDto {
  read: boolean
  templateAuthoring: boolean
  workflowAuthoring: boolean
  operate: boolean
  readPayload: boolean
  readAudit: boolean
  administer: boolean
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

export interface SubmissionDto {
  executionId: string
  status: string
  workflowKey: string
  workflowVersion: number
  correlationId: string
}

export interface SubmitExecutionRequest {
  data: null | JsonElement
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
