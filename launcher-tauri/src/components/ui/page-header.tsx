import * as React from "react"
import { cn } from "@/lib/utils"

interface PageHeaderProps {
  title: string
  description?: string
  actions?: React.ReactNode
  className?: string
}

/**
 * Sticky, opaque page title bar. Lives as the first child of a page's root so
 * the title stays pinned while the page body scrolls beneath it. `z-10` keeps
 * it under Modal's `z-50`.
 */
export function PageHeader({ title, description, actions, className }: PageHeaderProps) {
  return (
    <header
      className={cn(
        "sticky top-0 z-10 flex items-center justify-between gap-4 border-b border-border bg-background px-6 py-4",
        className
      )}
    >
      <div className="min-w-0">
        <h1 className="text-display font-semibold">{title}</h1>
        {description && <p className="text-13 text-muted-foreground">{description}</p>}
      </div>
      {actions && <div className="flex shrink-0 items-center gap-2">{actions}</div>}
    </header>
  )
}

export type { PageHeaderProps }
