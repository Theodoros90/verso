import { cva, type VariantProps } from 'class-variance-authority'
import { Slot } from 'radix-ui'
import * as React from 'react'

import { cn } from '@/lib/utils'

// shadcn/ui Button, restyled to the design reference (.btn, .btn.primary, .btn.ghost, .btn.sm).
const buttonVariants = cva(
  "inline-flex items-center justify-center gap-[7px] whitespace-nowrap rounded-[7px] border font-medium text-[13.5px] transition-colors disabled:pointer-events-none disabled:opacity-50 [&_svg]:pointer-events-none [&_svg:not([class*='size-'])]:size-4 [&_svg]:shrink-0",
  {
    variants: {
      variant: {
        default: 'border-line-strong bg-surface text-ink hover:bg-surface-2',
        primary: 'border-primary bg-primary text-primary-foreground hover:brightness-110',
        ghost: 'border-transparent bg-transparent text-ink-2 hover:bg-surface-2 hover:text-ink',
      },
      size: {
        default: 'px-3 py-[7px]',
        sm: 'px-[9px] py-1 text-[12.5px]',
        icon: 'size-[34px] rounded-lg p-0',
      },
    },
    defaultVariants: { variant: 'default', size: 'default' },
  },
)

function Button({
  className,
  variant,
  size,
  asChild = false,
  ...props
}: React.ComponentProps<'button'> & VariantProps<typeof buttonVariants> & { asChild?: boolean }) {
  const Comp = asChild ? Slot.Root : 'button'
  return <Comp data-slot="button" className={cn(buttonVariants({ variant, size, className }))} {...props} />
}

export { Button, buttonVariants }
