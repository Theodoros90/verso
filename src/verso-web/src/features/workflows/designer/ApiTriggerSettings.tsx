import { CircleCheck, CircleX, Copy, FlaskConical, TriangleAlert } from 'lucide-react'
import { useId, useState } from 'react'

import { Button } from '@/components/ui/button'
import { Field, Input, Select, Textarea } from '@/components/ui/form'
import { cn } from '@/lib/utils'

import { AUTH_HINTS, jsonError, MOCK_ENDPOINT_ORIGIN, parseJsonObject, schemaErrors } from '../model/payloadSchema'
import type { FieldSchema } from './SchemaForm'

type Config = Record<string, unknown>

export interface TemplateSample {
  templateName: string
  data: unknown
}

/**
 * Settings for the API / webhook step, laid out as in the design reference: endpoint (mock path, mock URL,
 * authentication method), expected JSON schema, sample payload and live checks of the sample against the schema.
 */
export function ApiTriggerSettings({
  schema,
  value,
  onChange,
  errorsFor,
  readOnly,
  workflowKey,
  templateSample,
}: {
  schema: FieldSchema
  value: Config
  onChange: (next: Config, key: string) => void
  errorsFor: (field: string) => string[]
  readOnly: boolean
  workflowKey: string
  templateSample?: TemplateSample
}) {
  const ids = { path: useId(), url: useId(), auth: useId(), schema: useId(), sample: useId(), load: useId() }
  const [copied, setCopied] = useState<'ok' | 'blocked' | null>(null)

  const str = (k: string) => (typeof value[k] === 'string' ? (value[k] as string) : '')
  const set = (k: string, v: string, key = k) => onChange({ ...value, [k]: v }, key)

  const path = str('path')
  const auth = str('auth')
  const expected = str('expectedSchema')
  const sample = str('samplePayload')
  const mockUrl = MOCK_ENDPOINT_ORIGIN + path

  const authField = schema.properties?.auth
  const authKeys = authField?.enum ?? Object.keys(AUTH_HINTS)
  const authLabels = authField?.['x-enumLabels'] ?? authKeys

  const copy = () => {
    if (!navigator.clipboard) return setCopied('blocked')
    navigator.clipboard.writeText(mockUrl).then(
      () => setCopied('ok'),
      () => setCopied('blocked'),
    )
  }

  const formatSample = () => {
    const parsed = parseJsonObject(sample)
    if (parsed) set('samplePayload', JSON.stringify(parsed, null, 2), 'samplePayload:format')
  }

  return (
    <div className="flex flex-col gap-4">
      <p className="flex gap-2 rounded-md bg-cyan-soft px-2.5 py-2 text-[12.5px] text-cyan-strong">
        <FlaskConical className="mt-px size-4 flex-none" aria-hidden="true" />
        <span>
          Mock endpoint for testing. Nothing is listening at this address. Real runs are submitted to{' '}
          <span className="font-mono">POST /api/v1/workflows/{workflowKey}/executions</span>.
        </span>
      </p>

      <Subsection title="Endpoint">
        <Field label={<>Path <span className="text-err">Required</span></>} htmlFor={ids.path} error={errorsFor('path')[0]}>
          <Input
            id={ids.path}
            value={path}
            placeholder="/hooks/invoices"
            className="font-mono text-[12.5px]"
            spellCheck={false}
            disabled={readOnly}
            aria-invalid={errorsFor('path').length > 0}
            onChange={(e) => set('path', e.target.value)}
          />
        </Field>
        <Field
          label="Mock endpoint URL"
          htmlFor={ids.url}
          hint={copied === 'ok' ? 'Copied.' : copied === 'blocked' ? 'Copying is blocked in this browser.' : 'Method: POST, content type application/json'}
        >
          <div className="flex gap-1.5">
            <Input id={ids.url} value={mockUrl} readOnly className="font-mono text-[12.5px]" />
            <Button type="button" size="icon" variant="ghost" aria-label="Copy URL" title="Copy URL" onClick={copy}>
              <Copy />
            </Button>
          </div>
        </Field>
        <Field label="Authentication method" htmlFor={ids.auth} hint={AUTH_HINTS[auth]} error={errorsFor('auth')[0]}>
          <Select id={ids.auth} value={auth} disabled={readOnly} aria-invalid={errorsFor('auth').length > 0} onChange={(e) => set('auth', e.target.value)}>
            {!authKeys.includes(auth) && <option value={auth}>{auth || 'Choose…'}</option>}
            {authKeys.map((k, i) => (
              <option key={k} value={k}>
                {authLabels[i] ?? k}
              </option>
            ))}
          </Select>
        </Field>
      </Subsection>

      <Subsection title="Expected JSON schema" htmlFor={ids.schema}>
        <Help error={errorsFor('expectedSchema')[0]} hint="Used to check the sample below. Real submissions are checked by the Validate step.">
          <Textarea
            id={ids.schema}
            value={expected}
            rows={9}
            spellCheck={false}
            className="font-mono text-[12px]"
            disabled={readOnly}
            aria-invalid={errorsFor('expectedSchema').length > 0}
            onChange={(e) => set('expectedSchema', e.target.value)}
          />
        </Help>
      </Subsection>

      <Subsection title="Sample JSON payload" htmlFor={ids.sample}>
        {!readOnly && (
          <div className="flex gap-1.5">
            <Select
              id={ids.load}
              aria-label="Load sample data"
              value=""
              disabled={!templateSample}
              onChange={(e) => {
                if (e.target.value === 'template' && templateSample) set('samplePayload', JSON.stringify(templateSample.data, null, 2), 'samplePayload:load')
              }}
            >
              <option value="">{templateSample ? 'Load sample data…' : 'No template sample data'}</option>
              {templateSample && <option value="template">Sample data of {templateSample.templateName}</option>}
            </Select>
            <Button type="button" size="sm" disabled={!parseJsonObject(sample)} onClick={formatSample}>
              Format
            </Button>
          </div>
        )}
        <Help error={errorsFor('samplePayload')[0]} hint="Field names in it are suggested in later steps.">
          <Textarea
            id={ids.sample}
            value={sample}
            rows={12}
            spellCheck={false}
            className="font-mono text-[12px]"
            disabled={readOnly}
            aria-invalid={errorsFor('samplePayload').length > 0}
            onChange={(e) => set('samplePayload', e.target.value)}
          />
        </Help>
      </Subsection>

      <Subsection title="Checks">
        <Checks expected={expected} sample={sample} />
      </Subsection>
    </div>
  )
}

/** A titled group. With `htmlFor`, the title is also the label of that control (as in the design reference). */
function Subsection({ title, htmlFor, children }: { title: string; htmlFor?: string; children: React.ReactNode }) {
  return (
    <div className="flex flex-col gap-3">
      <h5 className="text-[12.5px] font-semibold text-ink">{htmlFor ? <label htmlFor={htmlFor}>{title}</label> : title}</h5>
      {children}
    </div>
  )
}

function Help({ error, hint, children }: { error?: string; hint?: string; children: React.ReactNode }) {
  return (
    <div className="flex flex-col gap-[5px]">
      {children}
      {error ? <p className="text-xs text-err">{error}</p> : hint && <p className="text-xs text-ink-3">{hint}</p>}
    </div>
  )
}

type CheckTone = 'ok' | 'warn' | 'bad'

function Checks({ expected, sample }: { expected: string; sample: string }) {
  const checks: { tone: CheckTone; text: string }[] = []
  const schemaError = jsonError(expected)
  const sampleError = jsonError(sample)

  if (!expected.trim()) checks.push({ tone: 'warn', text: 'No expected schema yet' })
  else checks.push(schemaError ? { tone: 'bad', text: `Schema isn’t valid JSON: ${schemaError}` } : { tone: 'ok', text: 'Schema is valid JSON' })

  if (!sample.trim()) checks.push({ tone: 'warn', text: 'No sample payload yet' })
  else checks.push(sampleError ? { tone: 'bad', text: `Sample isn’t valid JSON: ${sampleError}` } : { tone: 'ok', text: 'Sample is valid JSON' })

  if (expected.trim() && sample.trim() && !schemaError && !sampleError) {
    const errors = schemaErrors(JSON.parse(expected), JSON.parse(sample))
    if (errors.length === 0) checks.push({ tone: 'ok', text: 'Sample matches the schema' })
    else for (const e of errors) checks.push({ tone: 'warn', text: `Sample doesn’t match the schema: ${e}` })
  }

  return (
    <ul aria-label="Checks" className="flex flex-col gap-1.5 text-[12.5px]">
      {checks.map((c, i) => {
        const Icon = c.tone === 'ok' ? CircleCheck : c.tone === 'warn' ? TriangleAlert : CircleX
        return (
          <li key={i} className={cn('flex gap-1.5', c.tone === 'ok' ? 'text-ok' : c.tone === 'warn' ? 'text-warn' : 'text-err')}>
            <Icon className="mt-px size-3.5 flex-none" aria-hidden="true" />
            <span>{c.text}</span>
          </li>
        )
      })}
    </ul>
  )
}
