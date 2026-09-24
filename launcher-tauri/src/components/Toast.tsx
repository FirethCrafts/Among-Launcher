import * as React from "react"

export function formatError(e: unknown): string {
  if (typeof e === "string") return e;
  if (e instanceof Error) return e.message;
  if (e !== null && typeof e === "object") {
    const keys = Object.keys(e as Record<string, unknown>);
    if (keys.length === 1) {
      const key = keys[0];
      const value = (e as Record<string, unknown>)[key];
      if (typeof value === "string") return `${key}: ${value}`;
    }
    try {
      const json = JSON.stringify(e);
      return json.length > 300 ? json.slice(0, 300) : json;
    } catch {
      return String(e);
    }
  }
  return String(e);
}

export type ToastTone = "neutral" | "success" | "error";

// Errors linger long enough to read the full detail; success/info clear fast.
const TONE_DURATION_MS: Record<ToastTone, number> = {
  error: 6000,
  success: 3000,
  neutral: 3000,
};

// Matches the `--animate-toast-out` duration (150ms). The toast is marked
// `leaving`, animates out, then is removed after this delay.
const TOAST_EXIT_MS = 150;

// Tone is meaning: a flat surface with a semantic border + dot.
const TONE_CLASS: Record<ToastTone, string> = {
  error: "border-danger/50 bg-danger/10 text-foreground",
  success: "border-success/50 bg-success/10 text-foreground",
  neutral: "border-border bg-surface-2 text-foreground",
};

const TONE_DOT: Record<ToastTone, string> = {
  error: "bg-danger",
  success: "bg-success",
  neutral: "bg-muted-foreground",
};

export function showToast(message: string, tone: ToastTone = "neutral") {
  window.dispatchEvent(new CustomEvent('app-toast', { detail: { message, tone } }));
}
export function ToastHost() {
  const [items, setItems] = React.useState<{ id: number; message: string; tone: ToastTone; leaving: boolean }[]>([]);
  React.useEffect(() => {
    // Each toast schedules two nested timers (tone duration, then the exit
    // removal). Track them so unmount clears any still pending instead of
    // firing setState on a dead tree.
    const timers = new Set<number>();
    const handler = (e: Event) => {
      const { message, tone } = (e as CustomEvent).detail;
      const id = Date.now() + Math.random();
      const safeMessage = typeof message === "string" ? message : formatError(message);
      const safeTone: ToastTone = tone === "success" || tone === "error" ? tone : "neutral";
      setItems((prev) => [...prev, { id, message: safeMessage, tone: safeTone, leaving: false }]);
      const toneTimer = window.setTimeout(() => {
        timers.delete(toneTimer);
        // Mark as leaving so it animates out, then remove after the exit.
        setItems((prev) => prev.map((t) => (t.id === id ? { ...t, leaving: true } : t)));
        const exitTimer = window.setTimeout(() => {
          timers.delete(exitTimer);
          setItems((prev) => prev.filter((t) => t.id !== id));
        }, TOAST_EXIT_MS);
        timers.add(exitTimer);
      }, TONE_DURATION_MS[safeTone]);
      timers.add(toneTimer);
    };
    window.addEventListener('app-toast', handler);
    return () => {
      window.removeEventListener('app-toast', handler);
      timers.forEach((t) => window.clearTimeout(t));
      timers.clear();
    };
  }, []);
  return (
    <div className="fixed bottom-6 right-6 z-50 flex flex-col gap-2">
      {items.map((t) => (
        <div
          key={t.id}
          role="status"
          aria-live="polite"
          data-state={t.leaving ? "exit" : "enter"}
          className={`flex items-center gap-2 rounded-control border px-4 py-2 text-sm shadow-card ${TONE_CLASS[t.tone]} ${
            t.leaving ? "animate-toast-out" : "animate-toast-in"
          }`}
        >
          <span className={`h-1.5 w-1.5 shrink-0 rounded-pill ${TONE_DOT[t.tone]}`} aria-hidden="true" />
          {t.message}
        </div>
      ))}
    </div>
  );
}
