import { useEffect, useState } from 'react'

import { Button } from '@/components/ui/button'
import { Dialog } from '@/components/ui/dialog'
import { Field, Textarea } from '@/components/ui/form'

import { inferSchema } from '../model/schema'

function parseObject(text: string): { value?: Record<string, unknown>; error?: string } {
  try {
    const v: unknown = JSON.parse(text)
    if (!v || typeof v !== 'object' || Array.isArray(v)) return { error: 'Must be a JSON object.' }
    return { value: v as Record<string, unknown> }
  } catch (e) {
    return { error: e instanceof Error ? `Not valid JSON: ${e.message}` : 'Not valid JSON.' }
  }
}

/** Advanced: edit the data model (JSON Schema) and the synthetic sample data directly. */
export function DataJsonDialog({
  open,
  onOpenChange,
  dataSchema,
  sampleData,
  onApply,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  dataSchema: unknown
  sampleData: unknown
  onApply: (dataSchema: Record<string, unknown>, sampleData: Record<string, unknown>) => void
}) {
  const [schemaText, setSchemaText] = useState('')
  const [sampleText, setSampleText] = useState('')
  const [errors, setErrors] = useState<{ schema?: string; sample?: string }>({})

  useEffect(() => {
    if (open) {
      setSchemaText(JSON.stringify(dataSchema ?? { type: 'object' }, null, 2))
      setSampleText(JSON.stringify(sampleData ?? {}, null, 2))
      setErrors({})
    }
  }, [open, dataSchema, sampleData])

  const derive = () => {
    const sample = parseObject(sampleText)
    if (sample.error) return setErrors({ sample: sample.error })
    setSchemaText(JSON.stringify(inferSchema(sample.value), null, 2))
    setErrors({})
  }

  const apply = () => {
    const schema = parseObject(schemaText)
    const sample = parseObject(sampleText)
    setErrors({ schema: schema.error, sample: sample.error })
    if (schema.value && sample.value) {
      onApply(schema.value, sample.value)
      onOpenChange(false)
    }
  }

  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      title="Data model and sample data"
      description="Synthetic data only. The data model is a JSON Schema; bindings are checked against it."
      className="max-w-4xl"
      footer={
        <>
          <Button onClick={() => onOpenChange(false)}>Cancel</Button>
          <Button variant="primary" onClick={apply}>
            Apply
          </Button>
        </>
      }
    >
      <div className="grid gap-4 md:grid-cols-2">
        <Field label="Sample data" htmlFor="json-sample" error={errors.sample}>
          <Textarea id="json-sample" value={sampleText} onChange={(e) => setSampleText(e.target.value)} className="h-[50vh] font-mono text-xs" spellCheck={false} />
        </Field>
        <Field label="Data model (JSON Schema)" htmlFor="json-schema" error={errors.schema}>
          <Textarea id="json-schema" value={schemaText} onChange={(e) => setSchemaText(e.target.value)} className="h-[50vh] font-mono text-xs" spellCheck={false} />
        </Field>
      </div>
      <Button size="sm" className="mt-3" onClick={derive}>
        Derive data model from sample data
      </Button>
    </Dialog>
  )
}
