import * as React from "react"
import { cn } from "@/lib/utils"

interface EmptyStateProps {
  icon?: React.ReactNode;
  title: string;
  description?: React.ReactNode;
  action?: React.ReactNode;
  className?: string;
}

export function EmptyState({ icon, title, description, action, className }: EmptyStateProps) {
  return (
    <div
      className={cn(
        "flex flex-col items-center justify-center gap-2 rounded-card border border-dashed border-border bg-transparent px-6 py-10 text-center",
        className
      )}
    >
      {icon && (
        <div className="mb-1 flex h-10 w-10 items-center justify-center rounded-control bg-surface-2 text-muted-foreground">
          {icon}
        </div>
      )}
      <p className="text-sm font-medium text-foreground">{title}</p>
      {description && (
        <p className="max-w-sm text-xs text-muted-foreground">{description}</p>
      )}
      {action && <div className="mt-2">{action}</div>}
    </div>
  )
}

export type { EmptyStateProps }
