import * as React from "react"
import { cn } from "@/lib/utils"

interface TooltipProps {
  content: React.ReactNode;
  children: React.ReactNode;
  side?: "top" | "bottom";
  className?: string;
}

/**
 * CSS-only tooltip (no portal/dependency). Shows on hover and focus-within.
 */
export function Tooltip({ content, children, side = "top", className }: TooltipProps) {
  return (
    <span className="group/tt relative inline-flex">
      {children}
      <span
        role="tooltip"
        className={cn(
          "pointer-events-none absolute left-1/2 z-50 -translate-x-1/2 translate-y-1 whitespace-nowrap rounded-control border border-border bg-surface-2 px-2 py-1 text-xs text-foreground opacity-0 shadow-card transition-[opacity,transform,translate] dur-fast group-hover/tt:translate-y-0 group-hover/tt:opacity-100 group-focus-within/tt:translate-y-0 group-focus-within/tt:opacity-100",
          side === "top" ? "bottom-full mb-2" : "top-full mt-2",
          className
        )}
      >
        {content}
      </span>
    </span>
  )
}
