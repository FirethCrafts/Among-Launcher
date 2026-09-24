import * as React from "react"
import { cn } from "@/lib/utils"

interface ListRowProps {
  leading?: React.ReactNode
  title: React.ReactNode
  subtitle?: React.ReactNode
  meta?: React.ReactNode
  actions?: React.ReactNode
  className?: string
}

/**
 * Generic dense list row. Renders an `<li>` — place inside a `<ul>` (optionally
 * `divide-y divide-border`). Modeled on `player-row.tsx`.
 */
export function ListRow({ leading, title, subtitle, meta, actions, className }: ListRowProps) {
  return (
    <li
      className={cn(
        "animate-list-in flex items-center gap-3 rounded-control px-3 py-2 transition-[color,background-color,transform,translate,scale] dur-fast active:scale-[0.995] hover:bg-surface-2",
        className
      )}
    >
      {leading && <div className="flex shrink-0 items-center">{leading}</div>}
      <div className="min-w-0 flex-1">
        <div className="truncate text-sm font-medium text-foreground">{title}</div>
        {subtitle && <div className="truncate text-13 text-muted-foreground">{subtitle}</div>}
      </div>
      {meta && (
        <div className="flex shrink-0 items-center gap-2 text-2xs text-muted-foreground">{meta}</div>
      )}
      {actions && <div className="flex shrink-0 items-center gap-1">{actions}</div>}
    </li>
  )
}

export type { ListRowProps }
