import * as React from "react"
import { ChevronDown } from "lucide-react"
import { cn } from "@/lib/utils"

type SelectProps = React.SelectHTMLAttributes<HTMLSelectElement> & {
  /** Class for the wrapper (the native select always fills it). */
  wrapperClassName?: string;
};

const Select = React.forwardRef<HTMLSelectElement, SelectProps>(
  ({ className, wrapperClassName, children, ...props }, ref) => (
    <div className={cn("relative w-full", wrapperClassName)}>
      <select
        ref={ref}
        className={cn(
          "h-9 w-full appearance-none rounded-control border border-border bg-surface-2 px-3 pr-9 text-sm text-foreground transition-colors focus-visible:border-ring focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50",
          className
        )}
        {...props}
      >
        {children}
      </select>
      <ChevronDown
        className="pointer-events-none absolute right-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground"
        aria-hidden="true"
      />
    </div>
  )
)
Select.displayName = "Select"

export { Select }
export type { SelectProps }
