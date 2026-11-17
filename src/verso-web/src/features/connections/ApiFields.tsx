import { Field, Input, Select } from '@/components/ui/form'
import { ApiError } from '@/lib/api/client'
import type { ApiConnectionDto } from '@/lib/api/generated'

/** HTTP API connection form shared by "New connection" (Authentication: HTTP API) and "Configure" on an API card (ADR 0017). */

export const API_AUTH = 'HTTP API'

export type ApiAuthType = 'None' | 'ApiKeyHeader' | 'ApiKeyQuery' | 'Bearer' | 'Basic' | 'OAuthClientCredentials'

export const API_AUTH_TYPES: { id: ApiAuthType; name: string }[] = [
  { id: 'None', name: 'No authentication' },
  { id: 'ApiKeyHeader', name: 'API key in a header' },
  { id: 'ApiKeyQuery', name: 'API key in the address (query)' },
  { id: 'Bearer', name: 'Bearer token' },
  { id: 'Basic', name: 'Basic (user name and password)' },
  { id: 'OAuthClientCredentials', name: 'OAuth 2.0 client credentials' },
]

export const authTypeName = (id: string) => API_AUTH_TYPES.find((a) => a.id === id)?.name ?? id

const CONNECTION_KEY = /^[a-z0-9][a-z0-9-]{1,63}$/
const HEADER_NAME = /^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/
const QUERY_NAME = /^[A-Za-z0-9_.[\]-]{1,64}$/

export interface ApiForm {
  key: string
  baseUrl: string
  testPath: string
  authType: ApiAuthType
  authName: string
  tokenUrl: string
  scope: string
  secret: string
  clearSecret: boolean
  timeoutSeconds: string
}

export const blankApi: ApiForm = { key: '', baseUrl: '', testPath: '', authType: 'None', authName: '', tokenUrl: '', scope: '', secret: '', clearSecret: false, timeoutSeconds: '15' }

const asAuth = (t: string): ApiAuthType => (API_AUTH_TYPES.some((a) => a.id === t) ? (t as ApiAuthType) : 'None')

export const toApiForm = (c: ApiConnectionDto): ApiForm => ({
  key: c.key,
  baseUrl: c.baseUrl,
  testPath: c.testPath,
  authType: asAuth(c.authType),
  authName: c.authName,
  tokenUrl: c.tokenUrl,
  scope: c.scope,
  secret: '', // never filled in: the saved secret is not sent to the browser
  clearSecret: false,
  timeoutSeconds: String(c.timeoutSeconds),
})

/**
 * A pasted full address such as https://api.frankfurter.dev/v1/latest?base=EUR becomes the base address (everything up to the last "/" before
 * the query) and the test path (the last segment and the query). Without a query the address is left as it is.
 */
export function splitFullUrl(url: string): { baseUrl: string; testPath: string } | null {
  const trimmed = url.trim()
  const q = trimmed.indexOf('?')
  if (q < 0) return null
  const beforeQuery = trimmed.slice(0, q)
  const lastSlash = beforeQuery.lastIndexOf('/')
  if (lastSlash < 0 || beforeQuery.slice(0, lastSlash).endsWith('/') || !/^https?:\/\/[^/]+\/./.test(beforeQuery)) return null
  return { baseUrl: beforeQuery.slice(0, lastSlash), testPath: trimmed.slice(lastSlash) }
}

const isHttps = (u: string, allowQuery: boolean) => {
  try {
    const url = new URL(u.trim())
    return url.protocol === 'https:' && url.username === '' && url.password === '' && url.hash === '' && (allowQuery || url.search === '')
  } catch {
    return false
  }
}

export type ApiErrors = Record<string, string>

export function validateApi(form: ApiForm, name: string, existing: boolean): ApiErrors {
  const errors: ApiErrors = {}
  if (!name.trim()) errors.name = 'Give the connection a name.'
  if (!existing && !CONNECTION_KEY.test(form.key)) errors.key = 'Lower-case letters, digits and hyphens, 2–64 characters.'
  if (!isHttps(form.baseUrl, false)) errors.baseUrl = 'Enter the base address starting with https:// (no login and no ? query), such as https://api.example.com/v1.'
  if (!form.testPath.startsWith('/') || form.testPath.startsWith('//') || /\s/.test(form.testPath)) errors.testPath = 'Enter the test request path starting with /, such as /status or /latest?base=EUR (no spaces).'
  const name2 = form.authName.trim()
  if (form.authType === 'ApiKeyHeader' && !HEADER_NAME.test(name2)) errors.authName = 'Enter the header name, such as X-API-Key.'
  if (form.authType === 'ApiKeyQuery' && !QUERY_NAME.test(name2)) errors.authName = 'Enter the query parameter name, such as api_key.'
  if (form.authType === 'Basic' && (name2 === '' || name2.includes(':'))) errors.authName = 'Enter the user name (no colon).'
  if (form.authType === 'OAuthClientCredentials') {
    if (name2 === '') errors.authName = 'Enter the client id.'
    if (!isHttps(form.tokenUrl, true)) errors.tokenUrl = 'Enter the token endpoint address starting with https://.'
  }
  return errors
}

/** The request body. The secret is null when left empty (the server keeps the saved one). */
export function apiBody(form: ApiForm, name: string) {
  const needsName = form.authType !== 'None' && form.authType !== 'Bearer'
  return {
    key: form.key,
    name: name.trim(),
    baseUrl: form.baseUrl.trim(),
    testPath: form.testPath.trim(),
    authType: form.authType,
    authName: needsName ? form.authName.trim() : '',
    tokenUrl: form.authType === 'OAuthClientCredentials' ? form.tokenUrl.trim() : '',
    scope: form.authType === 'OAuthClientCredentials' ? form.scope.trim() : '',
    timeoutSeconds: Number(form.timeoutSeconds),
    secret: form.authType === 'None' || form.secret === '' ? null : form.secret,
    clearSecret: form.clearSecret,
  }
}

export function apiErrorsOf(error: unknown, fallback: string): ApiErrors {
  if (error instanceof ApiError) {
    if (error.code === 'api_connection.key_taken') return { key: 'A connection with this key already exists.' }
    const out: ApiErrors = {}
    for (const [field, messages] of Object.entries(error.problem.errors ?? {})) {
      if (messages[0]) out[field.charAt(0).toLowerCase() + field.slice(1)] = messages[0]
    }
    return Object.keys(out).length > 0 ? out : { form: error.message }
  }
  return { form: fallback }
}

/** The fields (no name: the dialog above owns it). `existing` is the connection being edited, or null for a new one. */
export function ApiFields({
  form,
  errors,
  existing,
  onChange,
}: {
  form: ApiForm
  errors: ApiErrors
  existing: ApiConnectionDto | null
  onChange: (patch: Partial<ApiForm>) => void
}) {
  const t = form.authType
  const nameLabel = t === 'ApiKeyHeader' ? 'Header name' : t === 'ApiKeyQuery' ? 'Query parameter name' : t === 'Basic' ? 'User name' : 'Client id'
  const namePlaceholder = t === 'ApiKeyHeader' ? 'X-API-Key' : t === 'ApiKeyQuery' ? 'api_key' : ''
  const secretLabel = t === 'Bearer' ? 'Bearer token' : t === 'Basic' ? 'Password' : t === 'OAuthClientCredentials' ? 'Client secret' : 'API key'
  return (
    <>
      <Field
        label="Base address"
        htmlFor="api-base"
        error={errors.baseUrl}
        hint="https only. You can paste a full address with a query; it is split into the base and the test path."
      >
        <Input
          id="api-base"
          className="font-mono"
          value={form.baseUrl}
          onChange={(e) => onChange({ baseUrl: e.target.value })}
          onBlur={() => {
            const split = splitFullUrl(form.baseUrl)
            if (split && form.testPath === '') onChange(split)
          }}
          aria-invalid={!!errors.baseUrl}
          placeholder="https://api.frankfurter.dev/v1"
          autoComplete="off"
        />
      </Field>
      <Field label="Test request path" htmlFor="api-path" error={errors.testPath} hint="The one GET that Test connection sends, added to the base address.">
        <Input id="api-path" className="font-mono" value={form.testPath} onChange={(e) => onChange({ testPath: e.target.value })} aria-invalid={!!errors.testPath} placeholder="/latest?base=EUR&symbols=USD,GBP" autoComplete="off" />
      </Field>
      <div className="grid grid-cols-[1fr_8rem] gap-3">
        <Field label="API credentials" htmlFor="api-auth">
          <Select id="api-auth" value={form.authType} onChange={(e) => onChange({ authType: e.target.value as ApiAuthType, clearSecret: false })}>
            {API_AUTH_TYPES.map((a) => (
              <option key={a.id} value={a.id}>
                {a.name}
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Timeout (seconds)" htmlFor="api-timeout" error={errors.timeoutSeconds}>
          <Input id="api-timeout" inputMode="numeric" value={form.timeoutSeconds} onChange={(e) => onChange({ timeoutSeconds: e.target.value.replace(/\D/g, '') })} />
        </Field>
      </div>
      {t !== 'None' && t !== 'Bearer' && (
        <Field label={nameLabel} htmlFor="api-authname" error={errors.authName}>
          <Input id="api-authname" className="font-mono" value={form.authName} onChange={(e) => onChange({ authName: e.target.value })} aria-invalid={!!errors.authName} placeholder={namePlaceholder} autoComplete="off" />
        </Field>
      )}
      {t === 'OAuthClientCredentials' && (
        <>
          <Field label="Token URL" htmlFor="api-token" error={errors.tokenUrl} hint="Where the access token is requested. https only.">
            <Input id="api-token" className="font-mono" value={form.tokenUrl} onChange={(e) => onChange({ tokenUrl: e.target.value })} aria-invalid={!!errors.tokenUrl} placeholder="https://auth.example.com/oauth/token" autoComplete="off" />
          </Field>
          <Field label="Scope (optional)" htmlFor="api-scope" error={errors.scope}>
            <Input id="api-scope" className="font-mono" value={form.scope} onChange={(e) => onChange({ scope: e.target.value })} autoComplete="off" />
          </Field>
        </>
      )}
      {t !== 'None' && (
        <Field
          label={secretLabel}
          htmlFor="api-secret"
          error={errors.secret}
          hint={
            existing?.hasSecret
              ? 'A secret is saved. Leave empty to keep it; typing replaces it.'
              : t === 'ApiKeyQuery'
                ? 'Stored encrypted on the server. It is sent in the address, which some servers log.'
                : 'Stored encrypted on the server. Never shown again.'
          }
        >
          <Input
            id="api-secret"
            type="password"
            autoComplete="new-password"
            value={form.secret}
            onChange={(e) => onChange({ secret: e.target.value, clearSecret: false })}
            placeholder={existing?.hasSecret ? '••••••••' : ''}
          />
        </Field>
      )}
      {existing?.hasSecret && t !== 'None' && (
        <label className="inline-flex items-center gap-2 text-[13px] text-ink-2">
          <input type="checkbox" className="size-4 accent-cyan" checked={form.clearSecret} onChange={(e) => onChange({ clearSecret: e.target.checked, secret: '' })} />
          Remove the saved secret
        </label>
      )}
      <Field label="Connection key" htmlFor="api-key" error={errors.key} hint="A short name for this connection in workflows later. It cannot change.">
        <Input id="api-key" className="font-mono" value={form.key} disabled={!!existing} onChange={(e) => onChange({ key: e.target.value.toLowerCase() })} aria-invalid={!!errors.key} placeholder="frankfurter" />
      </Field>
      {errors.form && <p role="alert" className="text-[13px] text-err">{errors.form}</p>}
    </>
  )
}
