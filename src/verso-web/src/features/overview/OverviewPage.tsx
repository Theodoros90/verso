import { SystemStatusPanel } from '@/features/health/SystemStatusPanel'

export function OverviewPage() {
  return (
    <div className="max-w-[1440px] px-4 pt-6 pb-10 md:px-7">
      <p className="mb-5 max-w-prose text-ink-2">
        Verso maintains invoice templates and workflows, turns submitted invoice data into PDFs, and archives them.
        This build is the application foundation: navigation, theme, and a live connection to the API.
      </p>
      <SystemStatusPanel />
    </div>
  )
}
