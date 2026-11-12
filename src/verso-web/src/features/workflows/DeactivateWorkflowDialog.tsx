import { useState } from 'react'

import { Button } from '@/components/ui/button'
import { Dialog } from '@/components/ui/dialog'
import { workflowsApi } from '@/lib/api/workflows'

/** Asks before deactivating a workflow. It stops new runs and moves out of the main list; nothing is deleted and it can be activated again. */
export function DeactivateWorkflowDialog({
  workflow,
  open,
  onOpenChange,
  onDone,
}: {
  workflow: { id: string; name: string }
  open: boolean
  onOpenChange: (open: boolean) => void
  onDone: () => void
}) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const deactivate = async () => {
    setBusy(true)
    setError(null)
    try {
      await workflowsApi.deactivate(workflow.id)
      onOpenChange(false)
      onDone()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not deactivate the workflow.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      title={`Deactivate "${workflow.name}"?`}
      description="It stops accepting new runs (and its folder is no longer scanned), and moves out of the workflow list. Nothing is deleted: its versions, runs, archived documents and audit trail are kept. Find it under “Show deactivated” to activate it again."
      footer={
        <>
          <Button onClick={() => onOpenChange(false)}>Cancel</Button>
          <Button variant="primary" disabled={busy} onClick={() => void deactivate()}>
            {busy ? 'Deactivating…' : 'Deactivate workflow'}
          </Button>
        </>
      }
    >
      {error ? (
        <p role="alert" className="text-[13px] text-err">
          {error}
        </p>
      ) : (
        <p className="text-[13px] text-ink-2">Runs that are already in progress carry on. The key stays reserved, so it can’t be reused for another workflow.</p>
      )}
    </Dialog>
  )
}
