import { Eye } from 'lucide-react'
import { useEffect, useState } from 'react'

import { Button } from '@/components/ui/button'
import { Dialog } from '@/components/ui/dialog'
import { requestBlob } from '@/lib/api/client'
import { documentsApi } from '@/lib/api/executions'

import { DownloadButton } from './DownloadButton'

type State = { kind: 'loading' } | { kind: 'ready'; url: string } | { kind: 'error'; message: string }

/**
 * Shows an archived PDF in a dialog without downloading it. The bytes are fetched with the user's token (a plain
 * frame can't send it) and shown from a blob URL. The API checks the SHA-256 and audits the read as for a download.
 */
export function PreviewButton({ documentId, fileName }: { documentId: string; fileName: string }) {
  const [open, setOpen] = useState(false)
  const [state, setState] = useState<State>({ kind: 'loading' })

  useEffect(() => {
    if (!open) return
    const controller = new AbortController()
    let objectUrl: string | null = null
    setState({ kind: 'loading' })
    requestBlob(documentsApi.contentUrl(documentId), controller.signal)
      .then((blob) => {
        objectUrl = URL.createObjectURL(blob)
        setState({ kind: 'ready', url: objectUrl })
      })
      .catch((e: unknown) => {
        if (e instanceof DOMException && e.name === 'AbortError') return
        setState({ kind: 'error', message: e instanceof Error ? e.message : 'The preview failed.' })
      })
    return () => {
      controller.abort()
      if (objectUrl) URL.revokeObjectURL(objectUrl)
    }
  }, [open, documentId])

  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        aria-label={`Preview ${fileName}`}
        className="inline-flex items-center gap-1.5 rounded-[7px] border border-line-strong bg-surface px-2.5 py-1 text-[12.5px] text-ink hover:border-cyan"
      >
        <Eye className="size-3.5" aria-hidden="true" /> Preview
      </button>
      <Dialog
        open={open}
        onOpenChange={setOpen}
        title={fileName}
        className="h-[90vh] max-w-4xl"
        footer={
          <>
            <DownloadButton documentId={documentId} fileName={fileName} />
            <Button onClick={() => setOpen(false)}>Close</Button>
          </>
        }
      >
        {state.kind === 'loading' && <p className="text-[13px] text-ink-2">Loading preview…</p>}
        {state.kind === 'error' && (
          <p role="alert" className="text-[13px] text-err">
            {state.message}
          </p>
        )}
        {state.kind === 'ready' && <iframe title={`Preview of ${fileName}`} src={state.url} className="h-[70vh] w-full border-0" />}
      </Dialog>
    </>
  )
}
