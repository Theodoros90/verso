import { Download } from 'lucide-react'
import { useState } from 'react'

import { downloadFile } from '@/lib/api/client'
import { documentsApi } from '@/lib/api/executions'
import { cn } from '@/lib/utils'

/**
 * Downloads an archived document with the user's token (a plain link can't send it). The API checks the SHA-256
 * before sending and records the download in the audit trail.
 */
export function DownloadButton({ documentId, fileName, className }: { documentId: string; fileName: string; className?: string }) {
  const [state, setState] = useState<'idle' | 'busy' | { error: string }>('idle')

  const download = async () => {
    setState('busy')
    try {
      await downloadFile(documentsApi.contentUrl(documentId), fileName)
      setState('idle')
    } catch (e) {
      setState({ error: e instanceof Error ? e.message : 'Download failed.' })
    }
  }

  return (
    <span className={cn('inline-flex items-center gap-2', className)}>
      <button
        type="button"
        onClick={download}
        disabled={state === 'busy'}
        aria-label={`Download ${fileName}`}
        className="inline-flex items-center gap-1.5 rounded-[7px] border border-line-strong bg-surface px-2.5 py-1 text-[12.5px] text-ink hover:border-cyan disabled:opacity-60"
      >
        <Download className="size-3.5" aria-hidden="true" /> {state === 'busy' ? 'Downloading…' : 'Download'}
      </button>
      {typeof state === 'object' && (
        <span role="alert" className="text-xs text-err">
          {state.error}
        </span>
      )}
    </span>
  )
}
