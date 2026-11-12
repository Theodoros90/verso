import { PenLine, ScanEye, ScanText, Sparkles, Tags, type LucideIcon } from 'lucide-react'

/**
 * AI assistance steps shown in the palette as a preview only (frontend-only, no backend type).
 * They cannot be added to a workflow: AI features are deferred (CLAUDE.md). Labels and descriptions follow the prototype.
 */
export interface AiPreviewStep {
  type: string
  label: string
  description: string
  icon: LucideIcon
}

export const AI_PREVIEW_CATEGORY = 'ai'

export const AI_PREVIEW_STEPS: AiPreviewStep[] = [
  { type: 'ai-extract', label: 'Extract fields', description: 'Reads values from scanned or PDF documents', icon: ScanText },
  { type: 'ai-suggest', label: 'Suggest mappings', description: 'Proposes field mappings for new sources', icon: Sparkles },
  { type: 'ai-classify', label: 'Classify document', description: 'Sorts documents by type', icon: Tags },
  { type: 'ai-draft', label: 'Draft text', description: 'Writes a paragraph for the document', icon: PenLine },
  { type: 'human-review', label: 'Human review', description: 'A person checks AI output before it continues', icon: ScanEye },
]
