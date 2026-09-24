import { cn } from "@/lib/utils"

function Skeleton({ className, ...props }: React.HTMLAttributes<HTMLDivElement>) {
  return (
    <div
      className={cn(
        "relative overflow-hidden rounded-control bg-surface-2 motion-reduce:opacity-60",
        className
      )}
      {...props}
    >
      {/* Transform-only shimmer sweep (matches the frozen `animate-shimmer`
          token). Hidden under reduced motion, leaving the static fill. */}
      <div
        aria-hidden="true"
        className="pointer-events-none absolute inset-0 overflow-hidden motion-reduce:hidden"
      >
        <div className="h-full w-1/2 -translate-x-full animate-shimmer bg-gradient-to-r from-transparent via-foreground/10 to-transparent" />
      </div>
    </div>
  )
}

export { Skeleton }
