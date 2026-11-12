import { Tooltip as T } from 'radix-ui'
import * as React from 'react'

import { cn } from '@/lib/utils'

/**
 * Hover/focus tooltip (Radix). Wraps its child in a block-level trigger so it also works over disabled buttons,
 * which swallow pointer events. `title` is a bold heading line, `children` the explanation.
 */
export function Tooltip({
  title,
  content,
  children,
  side = 'right',
  className,
}: {
  title?: string
  content: React.ReactNode
  children: React.ReactNode
  side?: 'top' | 'right' | 'bottom' | 'left'
  className?: string
}) {
  return (
    <T.Provider delayDuration={350} skipDelayDuration={150}>
      <T.Root>
        <T.Trigger asChild>
          <div className={className}>{children}</div>
        </T.Trigger>
        <T.Portal>
          <T.Content
            side={side}
            sideOffset={10}
            collisionPadding={12}
            className={cn(
              'z-50 max-w-[280px] rounded-lg border border-line bg-surface px-3 py-2.5 text-[12.5px] leading-relaxed text-ink-2',
              'shadow-[0_8px_24px_-6px_rgb(15_23_42/0.18),0_2px_6px_rgb(15_23_42/0.08)]',
              'data-[state=delayed-open]:animate-in data-[state=delayed-open]:fade-in-0 data-[state=delayed-open]:zoom-in-95',
              'data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=closed]:zoom-out-95',
            )}
          >
            {title && <div className="mb-0.5 text-[12.5px] font-semibold text-ink">{title}</div>}
            {content}
            <T.Arrow className="fill-surface stroke-line" width={12} height={6} />
          </T.Content>
        </T.Portal>
      </T.Root>
    </T.Provider>
  )
}
