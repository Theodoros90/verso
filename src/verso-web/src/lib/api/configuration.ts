/** Configuration package endpoints (docs/api.md#configuration). Types come from the generated OpenAPI file. */
import { request } from './client'
import type { ConfigurationExportRequest, ConfigurationImportRequest, ConfigurationImportResult, ConfigurationPackage } from './generated'

const base = '/api/v1/configuration'

export const configurationApi = {
  /** Templates (latest published version) and workflows by id, with template references as keys. */
  export: (body: ConfigurationExportRequest) => request<ConfigurationPackage>(`${base}/export`, { method: 'POST', body }).then((r) => r.body),

  /** `dryRun` reports what an import would do and changes nothing. Administrators only. */
  import: (body: ConfigurationImportRequest, dryRun: boolean) =>
    request<ConfigurationImportResult>(`${base}/import?dryRun=${dryRun}`, { method: 'POST', body }).then((r) => r.body),
}
