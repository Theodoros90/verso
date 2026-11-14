import { parseDocx } from './docx'
import { parseRtf } from './rtf'
import { buildDesign, type ImportedDesign } from './toLayout'
import { ImportError, MAX_FILE_BYTES } from './types'

export { ImportError, type ImportedDesign }

export const IMPORT_ACCEPT = '.docx,.docm,.dotx,.rtf,.doc'

function readBuffer(file: File): Promise<ArrayBuffer> {
  if (typeof file.arrayBuffer === 'function') return file.arrayBuffer()
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => resolve(reader.result as ArrayBuffer)
    reader.onerror = () => reject(new ImportError('The file could not be read.'))
    reader.readAsArrayBuffer(file)
  })
}

/**
 * Reads a Word (DOCX) or RTF document into a template design. The file is recognised by its content, not its name, so an
 * RTF saved as `.doc` still works. Nothing leaves the browser. The old binary Word format (`.doc`) is refused with advice.
 */
export async function importDocument(file: File): Promise<ImportedDesign> {
  if (file.size > MAX_FILE_BYTES) throw new ImportError('The file is over 20 MB, which is more than Verso can import.')
  if (file.size === 0) throw new ImportError('The file is empty.')
  const buffer = await readBuffer(file)
  const head = new Uint8Array(buffer, 0, Math.min(8, buffer.byteLength))

  if (head[0] === 0x50 && head[1] === 0x4b) return buildDesign(await parseDocx(buffer))
  if (new TextDecoder('latin1').decode(head.subarray(0, 5)) === '{\\rtf') return buildDesign(parseRtf(new TextDecoder('latin1').decode(buffer)))
  if (head[0] === 0xd0 && head[1] === 0xcf && head[2] === 0x11 && head[3] === 0xe0)
    throw new ImportError('Old Word files (.doc) can’t be imported. Open it in Word, choose Save As → Word Document (.docx), and import that file.')
  throw new ImportError('Choose a Word document (.docx) or an RTF file (.rtf).')
}
