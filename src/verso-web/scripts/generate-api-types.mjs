// Generates src/lib/api/generated.ts from docs/openapi/v1.json (component schemas only).
// No dependencies on purpose. Run `npm run gen:api` after the OpenAPI snapshot changes;
// src/lib/api/generated.test.ts fails while the checked-in file is out of date.
import { readFileSync, writeFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
export const OPENAPI_PATH = resolve(here, '../../../docs/openapi/v1.json')
export const OUTPUT_PATH = resolve(here, '../src/lib/api/generated.ts')

function refName(ref) {
  return ref.replace('#/components/schemas/', '')
}

function typeOf(schema) {
  if (!schema || Object.keys(schema).length === 0) return 'unknown'
  if (schema.$ref) return refName(schema.$ref)
  if (schema.oneOf) return [...new Set(schema.oneOf.map(typeOf))].join(' | ')

  const types = Array.isArray(schema.type) ? schema.type : schema.type ? [schema.type] : []
  const nullable = types.includes('null')
  const numeric = types.includes('integer') || types.includes('number')
  const parts = []
  // .NET emits numbers as ["integer","string"] with a numeric pattern; the API always writes JSON numbers.
  if (numeric) parts.push('number')
  else if (types.includes('string')) parts.push('string')
  if (types.includes('boolean')) parts.push('boolean')
  if (types.includes('array')) parts.push(`${wrap(typeOf(schema.items))}[]`)
  if (types.includes('object')) {
    if (schema.properties) parts.push(inlineObject(schema))
    else if (schema.additionalProperties) parts.push(`Record<string, ${typeOf(schema.additionalProperties)}>`)
    else parts.push('Record<string, unknown>')
  }
  if (nullable) parts.push('null')
  else if (parts.length === 0) parts.push('unknown')
  return parts.join(' | ')
}

function wrap(t) {
  return t.includes('|') ? `(${t})` : t
}

function members(schema, indent) {
  const required = new Set(schema.required ?? [])
  return Object.entries(schema.properties ?? {})
    .map(([name, prop]) => `${indent}${name}${required.has(name) ? '' : '?'}: ${typeOf(prop)}`)
    .join('\n')
}

function inlineObject(schema) {
  return `{ ${members(schema, '').split('\n').join('; ')} }`
}

export function generate(openApi) {
  const out = [
    '// GENERATED from docs/openapi/v1.json by scripts/generate-api-types.mjs. Do not edit; run `npm run gen:api`.',
    '/* eslint-disable */',
    '',
  ]
  const schemas = openApi.components?.schemas ?? {}
  for (const name of Object.keys(schemas).sort()) {
    const schema = schemas[name]
    const isObject = schema.properties || (schema.type === 'object' && !schema.additionalProperties)
    if (isObject && schema.properties) {
      out.push(`export interface ${name} {`, members(schema, '  '), '}', '')
    } else {
      out.push(`export type ${name} = ${typeOf(schema)}`, '')
    }
  }
  return out.join('\n')
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const openApi = JSON.parse(readFileSync(OPENAPI_PATH, 'utf8'))
  writeFileSync(OUTPUT_PATH, generate(openApi))
  console.log(`Wrote ${OUTPUT_PATH}`)
}
