import { listen } from "@tauri-apps/api/event";

interface DeepLinkPayload {
  kind: string;
  code: string;
}

type JoinHandler = (code: string) => void;

let pending: string | null = null;
const handlers = new Set<JoinHandler>();

function deliver(code: string) {
  if (handlers.size === 0) {
    // Buffer until App subscribes — the event can land before React mounts.
    pending = code;
    return;
  }
  handlers.forEach((handler) => handler(code));
}

// Registered at module import time — before React renders and before any
// await — so the `deep-link` listener exists as early as possible. Rust
// emits this event immediately on warm launch (single-instance forward) and
// ~1500ms after cold-start argv parsing.
void listen<DeepLinkPayload>("deep-link", (event) => {
  const payload = event.payload;
  if (
    payload &&
    payload.kind === "join" &&
    typeof payload.code === "string" &&
    payload.code.length > 0
  ) {
    deliver(payload.code);
  }
});

/**
 * Subscribe to `deep-link` join events. If an event arrived before any
 * subscriber existed it is delivered on the next microtask. Returns an
 * unsubscribe function.
 */
export function onDeepLinkJoin(handler: JoinHandler): () => void {
  handlers.add(handler);
  if (pending) {
    const code = pending;
    pending = null;
    queueMicrotask(() => handler(code));
  }
  return () => {
    handlers.delete(handler);
  };
}
