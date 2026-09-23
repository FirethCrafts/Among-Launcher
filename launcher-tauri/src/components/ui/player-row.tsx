import * as React from "react"
import { cn } from "@/lib/utils"
import { Badge } from "@/components/ui/badge"

/** Crewmate color name -> dot class (static classes, safelisted by literals). */
const CREW_DOT: Record<string, string> = {
  red: "bg-crew-red",
  blue: "bg-crew-blue",
  green: "bg-crew-green",
  pink: "bg-crew-pink",
  orange: "bg-crew-orange",
  yellow: "bg-crew-yellow",
  black: "bg-crew-black",
  white: "bg-crew-white",
  purple: "bg-crew-purple",
  brown: "bg-crew-brown",
  cyan: "bg-crew-cyan",
  lime: "bg-crew-lime",
};

/**
 * Resolve a player color (crewmate name like "red" OR a raw CSS color like
 * "#C51111" / "rgb(...)") into dot props. Falls back to a neutral dot.
 */
export function crewDotProps(color?: string | null): { className: string; style?: React.CSSProperties } {
  const value = (color ?? "").trim();
  if (!value) return { className: "bg-muted-foreground" };
  const key = value.toLowerCase();
  if (CREW_DOT[key]) return { className: CREW_DOT[key] };
  if (/^(#|rgb|hsl)/i.test(value)) return { className: "", style: { backgroundColor: value } };
  return { className: "bg-muted-foreground" };
}

interface PlayerRowProps {
  name: string;
  color?: string | null;
  level?: number | null;
  ping?: number | null;
  isHost?: boolean;
  /** Right-aligned slot, e.g. a kick button. */
  actions?: React.ReactNode;
  className?: string;
}

export function PlayerRow({ name, color, level, ping, isHost, actions, className }: PlayerRowProps) {
  const dot = crewDotProps(color);
  return (
    <li
      className={cn(
        "flex items-center justify-between gap-3 rounded-control border border-border bg-surface-2 px-3 py-2 transition-colors hover:bg-border",
        className
      )}
    >
      <div className="flex min-w-0 items-center gap-2">
        <span
          className={cn("h-2.5 w-2.5 shrink-0 rounded-pill border border-border", dot.className)}
          style={dot.style}
          aria-hidden="true"
        />
        <span className="truncate text-sm font-medium text-foreground">{name}</span>
        {isHost && <Badge variant="neutral" className="text-2xs">Host</Badge>}
        {level != null && <span className="text-2xs text-muted-foreground">Lv.{level}</span>}
        {ping != null && <span className="text-2xs text-muted-foreground">{ping}ms</span>}
      </div>
      {actions && <div className="flex shrink-0 items-center gap-1">{actions}</div>}
    </li>
  )
}

export type { PlayerRowProps }
