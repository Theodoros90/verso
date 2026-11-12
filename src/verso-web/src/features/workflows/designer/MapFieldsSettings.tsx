import { CircleCheck, Plus } from 'lucide-react'

import { SchemaForm, type FieldSchema, type FormContext } from './SchemaForm'

type Config = Record<string, unknown>
type Row = { from?: unknown; to?: unknown; transform?: unknown }

const text = (v: unknown) => (typeof v === 'string' ? v.trim() : '')

/** The template fields no complete row (source and destination filled in) maps yet; mirrors FieldMapper.Unmapped on the server. */
export function unmappedDestinations(config: Config, templateFields: string[]): string[] {
  const rows = Array.isArray(config.rows) ? (config.rows as Row[]) : []
  const mapped = new Set(rows.filter((r) => text(r.from) && text(r.to)).map((r) => text(r.to)))
  return templateFields.filter((d) => !mapped.has(d))
}

/**
 * Settings of the Map fields step: the generic rows editor (source → destination, transform), plus the fields of the template
 * the workflow's Generate PDF step uses that no row maps yet. Clicking one adds a row for it; the source is left for the author.
 */
export function MapFieldsSettings({
  schema,
  value,
  onChange,
  ctx,
  templateFields,
  templateName,
}: {
  schema: FieldSchema
  value: Config
  onChange: (next: Config, key: string) => void
  ctx: FormContext
  /** The data model of the template a Generate PDF step uses; undefined when there is none (yet). */
  templateFields?: string[]
  templateName?: string
}) {
  const unmapped = templateFields ? unmappedDestinations(value, templateFields) : []
  const rows = Array.isArray(value.rows) ? (value.rows as Row[]) : []
  const transforms = schema.properties?.rows?.items?.properties?.transform
  const label = templateName ? `“${templateName}”` : 'The template'

  return (
    <div className="flex flex-col gap-4">
      <SchemaForm schema={schema} value={value} onChange={onChange} ctx={ctx} />
      <section aria-label="Required template fields" className="flex flex-col gap-2">
        <h5 className="text-[12.5px] font-medium text-ink-2">Required template fields</h5>
        {templateFields === undefined ? (
          <p className="text-xs text-ink-3">Choose a template in a Generate PDF step to see the fields it uses.</p>
        ) : templateFields.length === 0 ? (
          <p className="text-xs text-ink-3">{label} has no fields in its data model yet.</p>
        ) : unmapped.length === 0 ? (
          <p className="flex items-center gap-1.5 text-[12.5px] text-ok">
            <CircleCheck className="size-3.5" aria-hidden="true" /> All {templateFields.length} fields of {label} are mapped
          </p>
        ) : (
          <>
            <p className="text-xs text-ink-3">{label} uses these. Fine to leave out if the data already has them under that name; click one to add a row.</p>
            <div className="flex flex-wrap gap-1.5">
              {unmapped.map((d) => (
                <button
                  key={d}
                  type="button"
                  disabled={ctx.readOnly}
                  className="inline-flex items-center gap-1 rounded-full border border-warn/40 bg-warn-soft px-2 py-0.5 font-mono text-[11.5px] text-warn disabled:opacity-60"
                  onClick={() => onChange({ ...value, rows: [...rows, { from: '', to: d, transform: transforms?.enum?.[0] ?? 'as-is' }] }, 'rows')}
                >
                  <Plus className="size-3" aria-hidden="true" /> {d}
                </button>
              ))}
            </div>
          </>
        )}
      </section>
    </div>
  )
}
