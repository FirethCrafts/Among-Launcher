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
  const [items, setItems] = React.useState<{ id: number; message: string; tone: ToastTone }[]>([]);
  React.useEffect(() => {
    const handler = (e: Event) => {
      const { message, tone } = (e as CustomEvent).detail;
      const id = Date.now() + Math.random();
      const safeMessage = typeof message === "string" ? message : formatError(message);
      const safeTone: ToastTone = tone === "success" || tone === "error" ? tone : "neutral";
      setItems((prev) => [...prev, { id, message: safeMessage, tone: safeTone }]);
      setTimeout(
        () => setItems((prev) => prev.filter((t) => t.id !== id)),
        TONE_DURATION_MS[safeTone]
      );
    };
    window.addEventListener('app-toast', handler);
    return () => window.removeEventListener('app-toast', handler);
  }, []);
  return (
    <div className="fixed bottom-6 right-6 z-50 flex flex-col gap-2">
      {items.map((t) => (
        <div
          key={t.id}
          role="status"
          aria-live="polite"
          className={`flex items-center gap-2 rounded-control border px-4 py-2 text-sm shadow-card ${TONE_CLASS[t.tone]}`}
        >
          <span className={`h-1.5 w-1.5 shrink-0 rounded-pill ${TONE_DOT[t.tone]}`} aria-hidden="true" />
          {t.message}
        </div>
      ))}
    </div>
  );
}
