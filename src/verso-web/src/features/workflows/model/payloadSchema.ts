// Design-time checks for the API / webhook step: is the text JSON, and does the sample fit the expected schema?
// Only the JSON Schema basics the step's settings describe: type (incl. integer), required, properties.

/** The JSON parse error for non-blank text, or null when it parses (blank text is not an error). */
export function jsonError(text: string): string | null {
  if (!text.trim()) return null
  try {
    JSON.parse(text)
    return null
  } catch (e) {
    return (e as Error).message
  }
}

/** The parsed JSON object, or undefined when blank, invalid or not an object. */
export function parseJsonObject(text: string): Record<string, unknown> | undefined {
  if (!text.trim()) return undefined
  try {
    const v: unknown = JSON.parse(text)
    return isObject(v) ? v : undefined
  } catch {
    return undefined
  }
}

/** A problem for non-blank text that isn't a JSON object, else null. */
export function jsonObjectProblem(text: string): string | null {
  const error = jsonError(text)
  if (error) return `Not valid JSON: ${error}`
  return text.trim() && !parseJsonObject(text) ? 'Must be a JSON object.' : null
}

const isObject = (v: unknown): v is Record<string, unknown> => v !== null && typeof v === 'object' && !Array.isArray(v)

const typeName = (v: unknown) => (v === null ? 'null' : Array.isArray(v) ? 'array' : typeof v)

/** Where `data` doesn't fit `schema`, as readable messages such as "invoice.number is required but missing". */
export function schemaErrors(schema: unknown, data: unknown, path = ''): string[] {
  if (!isObject(schema)) return []
  const where = path || 'payload'
  const type = schema.type
  if (typeof type === 'string' && typeName(data) !== type && !(type === 'integer' && Number.isInteger(data))) {
    return [`${where} should be ${type}, found ${typeName(data)}`]
  }
  const errors: string[] = []
  if (type === 'object' && isObject(data)) {
    const required = Array.isArray(schema.required) ? schema.required.filter((k): k is string => typeof k === 'string') : []
    for (const k of required) if (!(k in data)) errors.push(`${path ? `${path}.` : ''}${k} is required but missing`)
    const properties = isObject(schema.properties) ? schema.properties : {}
    for (const [k, s] of Object.entries(properties)) if (k in data) errors.push(...schemaErrors(s, data[k], path ? `${path}.${k}` : k))
  }
  return errors
}

/** What each authentication method means for callers (labels come from the node catalog). */
export const AUTH_HINTS: Record<string, string> = {
  none: 'Anyone who knows the URL can post. Use only for local testing.',
  'api-key': 'Callers send the key in the X-Api-Key header.',
  'hmac-sha256': 'Callers sign the body with a shared secret and send it in X-Verso-Signature.',
  oauth2: 'Callers send a token from your identity provider in the Authorization header.',
  mtls: 'Callers present a client certificate that your CA issued.',
}

export const MOCK_ENDPOINT_ORIGIN = 'https://mock.verso.local'
