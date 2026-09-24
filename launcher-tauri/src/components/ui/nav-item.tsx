import type { LucideIcon } from "lucide-react"
import { cn } from "@/lib/utils"

interface NavItemProps {
  icon: LucideIcon
  label: string
  active: boolean
  collapsed?: boolean
  onClick: () => void
}

/**
 * Sidebar nav button. Collapsed = 48px icon rail (title/aria-label carry the
 * label); expanded = full-width icon + label. The label stays mounted and
 * fades on collapse so the rail animates instead of snapping.
 */
export function NavItem({ icon: Icon, label, active, collapsed = false, onClick }: NavItemProps) {
  return (
    <button
      type="button"
      onClick={onClick}
      title={label}
      aria-label={label}
      aria-current={active ? "page" : undefined}
      className={cn(
        "flex items-center rounded-control transition-[color,background-color,transform,translate,scale] dur-fast active:scale-[0.995] focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring",
        collapsed ? "h-12 w-12 justify-center" : "h-10 w-full gap-3 px-3",
        active
          ? "bg-primary text-primary-foreground"
          : "text-muted-foreground hover:bg-surface-2 hover:text-foreground"
      )}
    >
      <Icon className="h-5 w-5 shrink-0" />
      <span
        className={cn(
          "truncate text-sm transition-opacity dur-fast",
          collapsed ? "w-0 opacity-0" : "opacity-100"
        )}
      >
        {label}
      </span>
    </button>
  )
}

export type { NavItemProps }
