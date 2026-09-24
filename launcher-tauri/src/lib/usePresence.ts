import { useEffect, useState } from "react";

export type PresenceState = "enter" | "exit";

export interface Presence {
  /** Whether the element should be in the tree at all. */
  mounted: boolean;
  /** Drives the enter/exit animation via `data-state`. */
  state: PresenceState;
}

/**
 * Keeps an element mounted long enough to play its exit animation.
 *
 * - `open` → `{ mounted: true, state: "enter" }`
 * - closing → `{ mounted: true, state: "exit" }` for `exitMs`, then unmounts.
 *
 * `state` is derived during render so the exit animation starts on the same
 * commit the caller closes, and the unmount timer is always cleaned up.
 */
export function usePresence(open: boolean, exitMs = 150): Presence {
  const [mounted, setMounted] = useState(open);
  const state: PresenceState = open ? "enter" : "exit";

  useEffect(() => {
    if (open) {
      setMounted(true);
      return;
    }
    const timer = window.setTimeout(() => setMounted(false), exitMs);
    return () => window.clearTimeout(timer);
  }, [open, exitMs]);

  return { mounted, state };
}
