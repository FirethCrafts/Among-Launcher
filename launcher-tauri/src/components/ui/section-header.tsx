import * as React from "react"
import { cn } from "@/lib/utils"
import { Badge } from "@/components/ui/badge"

interface SectionHeaderProps {
  title: string
  count?: number
  actions?: React.ReactNode
  className?: string
}

/** Section title with an optional count pill and right-aligned actions. */
export function SectionHeader({ title, count, actions, className }: SectionHeaderProps) {
  return (
    <div className={cn("flex items-center justify-between gap-3", className)}>
      <div className="flex min-w-0 items-center gap-2">
        <h2 className="truncate text-sm font-semibold text-foreground">{title}</h2>
        {count != null && (
          <Badge variant="neutral" className="text-2xs">
            {count}
          </Badge>
        )}
      </div>
      {actions && <div className="flex shrink-0 items-center gap-2">{actions}</div>}
    </div>
  )
}

export type { SectionHeaderProps }
