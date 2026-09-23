import * as React from "react"
import { cn } from "@/lib/utils"

type BadgeVariant = 'neutral' | 'muted' | 'success' | 'danger' | 'warning' | 'info';

/** `emerald`/`red` are legacy aliases of `success`/`danger`. */
type BadgeDotColor = 'emerald' | 'success' | 'red' | 'danger' | 'warning' | 'info';

type BadgeProps = {
  variant?: BadgeVariant;
  showDot?: boolean;
  dotColor?: BadgeDotColor;
};

const VARIANT_CLASS: Record<BadgeVariant, string> = {
  neutral: "border-border bg-surface-2 text-foreground",
  muted: "border-border bg-transparent text-muted-foreground",
  success: "border-success/40 bg-success/10 text-success",
  danger: "border-danger/40 bg-danger/10 text-danger",
  warning: "border-warning/40 bg-warning/10 text-warning",
  info: "border-info/40 bg-info/10 text-info",
};

const DOT_CLASS: Record<BadgeDotColor, string> = {
  emerald: "bg-success",
  success: "bg-success",
  red: "bg-danger",
  danger: "bg-danger",
  warning: "bg-warning",
  info: "bg-info",
};

export function Badge({ variant = 'neutral', showDot, dotColor = 'emerald', className, children, ...props }: BadgeProps & React.HTMLAttributes<HTMLSpanElement>) {
  return (
    <span className={cn(
      "inline-flex items-center gap-1.5 rounded-pill border px-2.5 py-0.5 text-xs font-medium",
      VARIANT_CLASS[variant],
      className,
    )} {...props}>
      {showDot && <span className={cn("h-1.5 w-1.5 shrink-0 rounded-pill", DOT_CLASS[dotColor])} />}
      {children}
    </span>
  );
}

export type { BadgeProps, BadgeVariant, BadgeDotColor }

/** Pill is a friendlier name for the same primitive (radio/status chips). */
export const Pill = Badge;
