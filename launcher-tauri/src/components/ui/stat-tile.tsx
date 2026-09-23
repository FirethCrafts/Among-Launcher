import * as React from "react"
import { cn } from "@/lib/utils"

type StatTone = "default" | "success" | "danger" | "warning" | "info";

interface StatTileProps {
  label: string;
  value: React.ReactNode;
  hint?: React.ReactNode;
  icon?: React.ReactNode;
  tone?: StatTone;
  className?: string;
}

const TONE_CLASS: Record<StatTone, string> = {
  default: "text-foreground",
  success: "text-success",
  danger: "text-danger",
  warning: "text-warning",
  info: "text-info",
};

export function StatTile({ label, value, hint, icon, tone = "default", className }: StatTileProps) {
  return (
    <div className={cn("rounded-card border border-border bg-surface p-4", className)}>
      <div className="flex items-start justify-between gap-2">
        <span className="text-2xs font-medium uppercase tracking-wide text-muted-foreground">
          {label}
        </span>
        {icon && <span className="shrink-0 text-muted-foreground">{icon}</span>}
      </div>
      <div className={cn("mt-2 text-xl font-semibold tabular-nums", TONE_CLASS[tone])}>
        {value}
      </div>
      {hint && <div className="mt-1 text-2xs text-muted-foreground">{hint}</div>}
    </div>
  )
}

export type { StatTileProps, StatTone }
