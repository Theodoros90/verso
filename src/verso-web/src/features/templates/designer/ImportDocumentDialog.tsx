import { FileUp, Loader2 } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'

import { Button } from '@/components/ui/button'
import { Dialog } from '@/components/ui/dialog'
import { cn } from '@/lib/utils'

import { IMPORT_ACCEPT, importDocument, ImportError, type ImportedDesign } from '../import'

const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`

/**
 * Start from a Word or RTF document instead of an empty page (design reference v14, "Import DOCX"). The file is read
 * in the browser and shown as a summary first; nothing changes until the author applies it, and Apply is one undo step.
 */
export function ImportDocumentDialog({
  open,
  onOpenChange,
  hasContent,
  onApply,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  /** The draft already has elements, so applying replaces them. */
  hasContent: boolean
  onApply: (design: ImportedDesign, fileName: string) => void
}) {
  const [state, setState] = useState<{ kind: 'idle' } | { kind: 'reading'; name: string } | { kind: 'error'; message: string } | { kind: 'ready'; name: string; design: ImportedDesign }>({ kind: 'idle' })
  const [dragging, setDragging] = useState(false)
  const input = useRef<HTMLInputElement>(null)

  useEffect(() => {
    if (!open) setState({ kind: 'idle' })
  }, [open])

  const read = async (file: File) => {
    setState({ kind: 'reading', name: file.name })
    try {
      setState({ kind: 'ready', name: file.name, design: await importDocument(file) })
    } catch (e) {
      setState({ kind: 'error', message: e instanceof ImportError ? e.message : 'The document could not be read. It may be damaged; try saving it again as .docx.' })
    }
  }

  const summary = state.kind === 'ready' ? state.design.summary : null
  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      title="Import a document"
      description="Start from a Word (.docx) or RTF document instead of designing from scratch. It is read in your browser and nothing is uploaded."
      className="max-w-xl"
      footer={
        <>
          <Button type="button" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button type="button" variant="primary" disabled={state.kind !== 'ready'} onClick={() => state.kind === 'ready' && onApply(state.design, state.name)}>
            {hasContent ? 'Replace the current design' : 'Use this design'}
          </Button>
        </>
      }
    >
      <div
        onDragOver={(e) => {
          e.preventDefault()
          setDragging(true)
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={(e) => {
          e.preventDefault()
          setDragging(false)
          const file = e.dataTransfer.files[0]
          if (file) void read(file)
        }}
        className={cn('flex flex-col items-center gap-2 rounded-[10px] border border-dashed border-line-strong px-4 py-6 text-center', dragging && 'border-cyan bg-cyan-soft')}
      >
        <FileUp className="size-6 text-ink-3" aria-hidden="true" />
        <p className="m-0 text-[13px] text-ink-2">Drop a file here, or</p>
        <Button type="button" size="sm" onClick={() => input.current?.click()}>
          Choose a file
        </Button>
        <input
          ref={input}
          type="file"
          accept={IMPORT_ACCEPT}
          hidden
          aria-label="Document to import"
          onChange={(e) => {
            const file = e.target.files?.[0]
            e.target.value = ''
            if (file) void read(file)
          }}
        />
        <p className="m-0 text-xs text-ink-3">.docx and .rtf, up to 20 MB. For an old .doc file, save it as .docx in Word first.</p>
      </div>

      <div role="status" aria-live="polite" className="mt-3 text-[13px]">
        {state.kind === 'reading' && (
          <p className="m-0 flex items-center gap-2 text-ink-3">
            <Loader2 className="size-4 animate-spin" aria-hidden="true" /> Reading {state.name}…
          </p>
        )}
        {state.kind === 'error' && <p className="m-0 text-err">{state.message}</p>}
        {state.kind === 'ready' && summary && (
          <div className="flex flex-col gap-2">
            <p className="m-0 text-ink">
              <b>{state.name}</b>: {plural(summary.text, 'text block')}, {plural(summary.tables, 'table')}, {plural(summary.images, 'image')}
              {summary.header + summary.footer > 0 && <>, header with {plural(summary.header, 'item')}, footer with {plural(summary.footer, 'item')}</>}.
            </p>
            {hasContent && <p className="m-0 text-warn">This replaces what is on the page now. Nothing is saved until you choose Save draft, and Undo brings the old design back.</p>}
            <ul className="m-0 flex list-disc flex-col gap-1 pl-5 text-ink-2">
              {state.design.report.map((r) => (
                <li key={r}>{r}</li>
              ))}
            </ul>
          </div>
        )}
      </div>
    </Dialog>
  )
}
