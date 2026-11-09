import type { NavItem } from '@/app/navigation'

/** Honest placeholder for screens that later milestones build. */
export function NotBuiltYetPage({ item }: { item: NavItem }) {
  return (
    <div className="max-w-[1440px] px-4 pt-6 pb-10 md:px-7">
      <section className="max-w-xl rounded-lg border border-dashed border-line-strong bg-surface p-6">
        <div className="mb-3 flex items-center gap-2.5">
          <item.icon className="size-5 text-ink-3" strokeWidth={1.75} aria-hidden="true" />
          <h2 className="text-[14.5px] font-semibold">{item.label}</h2>
          {item.milestone && (
            <span className="rounded border border-line bg-surface-2 px-[7px] py-px text-xs text-ink-2">
              Milestone {item.milestone}
            </span>
          )}
        </div>
        <p className="text-ink-2">{item.summary}</p>
        <p className="mt-2 text-ink-3">Not built yet.</p>
      </section>
    </div>
  )
}
