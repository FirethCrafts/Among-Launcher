import * as React from "react"
import { cn } from "@/lib/utils"

interface SettingsRowProps {
  label: React.ReactNode
  description?: React.ReactNode
  control?: React.ReactNode
  /**
   * When set, associates the row's label with `control` for screen readers
   * (the label node gets this `id`; the control wrapper is labelled by it).
   */
  id?: string
  className?: string
}

/**
 * Label/description on the left, control on the right. Designed to sit inside a
 * `divide-y divide-border` list; spacing is vertical only.
 */
export function SettingsRow({ label, description, control, id, className }: SettingsRowProps) {
  return (
    <div className={cn("flex items-center justify-between gap-4 py-3", className)}>
      <div className="min-w-0">
        <div id={id} className="text-sm text-foreground">{label}</div>
        {description && <div className="mt-0.5 text-13 text-muted-foreground">{description}</div>}
      </div>
      {control && (
        <div
          className="flex shrink-0 items-center gap-2"
          role={id ? "group" : undefined}
          aria-labelledby={id}
        >
          {control}
        </div>
      )}
    </div>
  )
}

export type { SettingsRowProps }
