import { useState } from 'react'

import { Button } from '@/components/ui/button'
import { Dialog } from '@/components/ui/dialog'
import { Field, Input } from '@/components/ui/form'
import { ApiError } from '@/lib/api/client'
import type { WorkflowDto, WorkflowSummaryDto } from '@/lib/api/generated'
import { workflowsApi } from '@/lib/api/workflows'

const KEY_PATTERN = /^[a-z][a-z0-9]*(-[a-z0-9]+)*$/

/** Copies a workflow under a new key and name; the copy opens as a draft with the same graph. */
export function DuplicateWorkflowDialog({
  workflow,
  open,
  onOpenChange,
  onDone,
}: {
  workflow: WorkflowSummaryDto
  open: boolean
  onOpenChange: (open: boolean) => void
  onDone: (copy: WorkflowDto) => void
}) {
  const [key, setKey] = useState(`${workflow.key}-copy`.slice(0, 48))
  const [name, setName] = useState(`Copy of ${workflow.name}`.slice(0, 200))
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [busy, setBusy] = useState(false)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    const local: Record<string, string> = {}
    if (key.length < 2 || key.length > 48 || !KEY_PATTERN.test(key)) local.key = '2–48 characters: lower-case letters, digits and single hyphens (e.g. invoice-standard).'
    if (!name.trim()) local.name = 'Enter a name.'
    setErrors(local)
    if (Object.keys(local).length) return

    setBusy(true)
    try {
      onDone(await workflowsApi.duplicate(workflow.id, { key, name: name.trim() }))
    } catch (error) {
      if (error instanceof ApiError && error.code === 'workflow.key_taken') setErrors({ key: 'A workflow with this key already exists.' })
      else if (error instanceof ApiError && error.problem.errors) {
        const mapped: Record<string, string> = {}
        for (const [k, v] of Object.entries(error.problem.errors)) mapped[k.toLowerCase()] = v[0] ?? 'Invalid.'
        setErrors(mapped)
      } else setErrors({ form: error instanceof Error ? error.message : 'Could not duplicate the workflow.' })
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      title={`Duplicate "${workflow.name}"`}
      description="The copy starts as a draft with the same steps and settings. Versions and runs aren’t copied. The key is part of the submission URL and can’t be changed later."
      footer={
        <>
          <Button onClick={() => onOpenChange(false)}>Cancel</Button>
          <Button variant="primary" type="submit" form="duplicate-workflow" disabled={busy}>
            Duplicate
          </Button>
        </>
      }
    >
      <form id="duplicate-workflow" onSubmit={submit} className="flex flex-col gap-3.5" noValidate>
        <Field label="Key" htmlFor="dup-key" hint={key ? `POST /api/v1/workflows/${key}/executions` : undefined} error={errors.key}>
          <Input id="dup-key" value={key} onChange={(e) => setKey(e.target.value.toLowerCase())} aria-invalid={!!errors.key} className="font-mono" autoFocus />
        </Field>
        <Field label="Name" htmlFor="dup-name" error={errors.name}>
          <Input id="dup-name" value={name} onChange={(e) => setName(e.target.value)} aria-invalid={!!errors.name} />
        </Field>
        {errors.form && (
          <p role="alert" className="text-[13px] text-err">
            {errors.form}
          </p>
        )}
      </form>
    </Dialog>
  )
}
