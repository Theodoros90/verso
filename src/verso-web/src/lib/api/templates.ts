/** Template endpoints (docs/api.md#templates). Types come from the generated OpenAPI file. */
import { request, type Tagged } from './client'
import type {
  CreateTemplateRequest,
  HtmlPreviewDto,
  PdfPreviewDto,
  PageResultOfTemplateSummaryDto,
  PageSettingsDto,
  TemplateDto,
  TemplateVersionDto,
  TemplateVersionSummaryDto,
} from './generated'

const base = '/api/v1/templates'

export interface DraftContent {
  pageSettings: PageSettingsDto
  layout: unknown
  dataSchema: unknown
  sampleData: unknown
}

export const templatesApi = {
  list: (page = 1, pageSize = 100, signal?: AbortSignal) =>
    request<PageResultOfTemplateSummaryDto>(`${base}?page=${page}&pageSize=${pageSize}`, { signal }).then((r) => r.body),

  create: (body: CreateTemplateRequest) => request<TemplateDto>(base, { method: 'POST', body }).then((r) => r.body),

  get: (id: string, signal?: AbortSignal) => request<TemplateDto>(`${base}/${id}`, { signal }).then((r) => r.body),

  versions: (id: string, signal?: AbortSignal) =>
    request<TemplateVersionSummaryDto[]>(`${base}/${id}/versions`, { signal }).then((r) => r.body),

  version: (id: string, versionId: string, signal?: AbortSignal): Promise<Tagged<TemplateVersionDto>> =>
    request<TemplateVersionDto>(`${base}/${id}/versions/${versionId}`, { signal }),

  draft: (id: string, signal?: AbortSignal): Promise<Tagged<TemplateVersionDto>> =>
    request<TemplateVersionDto>(`${base}/${id}/draft`, { signal }),

  startDraft: (id: string): Promise<Tagged<TemplateVersionDto>> =>
    request<TemplateVersionDto>(`${base}/${id}/draft`, { method: 'POST' }),

  saveDraft: (id: string, content: DraftContent, etag: string): Promise<Tagged<TemplateVersionDto>> =>
    request<TemplateVersionDto>(`${base}/${id}/draft`, { method: 'PUT', body: content, ifMatch: etag }),

  publish: (id: string, etag: string): Promise<Tagged<TemplateVersionDto>> =>
    request<TemplateVersionDto>(`${base}/${id}/draft/publish`, { method: 'POST', ifMatch: etag }),

  previewHtml: (id: string, data?: unknown) =>
    request<HtmlPreviewDto>(`${base}/${id}/draft/preview/html`, {
      method: 'POST',
      body: data === undefined ? undefined : { data },
    }).then((r) => r.body),

  /** Queues a PDF preview of the saved draft (rendered by the Worker). */
  queuePdfPreview: (id: string, data?: unknown) =>
    request<PdfPreviewDto>(`${base}/${id}/draft/preview/pdf`, {
      method: 'POST',
      body: data === undefined ? undefined : { data },
    }).then((r) => r.body),
}

/** PDF previews (docs/api.md#pdf-previews). */
export const previewsApi = {
  get: (previewId: string, signal?: AbortSignal) =>
    request<PdfPreviewDto>(`/api/v1/previews/${previewId}`, { signal }).then((r) => r.body),

  contentUrl: (previewId: string) => `/api/v1/previews/${previewId}/content`,
}
