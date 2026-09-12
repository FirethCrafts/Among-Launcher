import * as React from "react"
import { cn } from "@/lib/utils"

type BadgeProps = {
  variant?: 'neutral' | 'muted';
  showDot?: boolean;
  dotColor?: 'emerald' | 'red';
};

export function Badge({ variant = 'neutral', showDot, dotColor, className, children, ...props }: BadgeProps & React.HTMLAttributes<HTMLSpanElement>) {
  return (
    <span className={cn(
      "inline-flex items-center gap-1.5 rounded-full border border-white/10 px-2.5 py-0.5 text-xs font-medium backdrop-blur-md",
      variant === 'neutral' ? "bg-white/10 text-secondary-foreground" : "bg-muted/60 text-muted-foreground",
      className,
    )} {...props}>
      {showDot && <span className={cn("h-1.5 w-1.5 rounded-full", dotColor === 'red' ? "bg-red-500" : "bg-emerald-500")} />}
      {children}
    </span>
  );
}

export type { BadgeProps }
