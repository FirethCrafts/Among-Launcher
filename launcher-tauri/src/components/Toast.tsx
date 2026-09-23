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

// Tone accents on the shared glass toast surface (dark-only theme).
const TONE_CLASS: Record<ToastTone, string> = {
  error: "border-red-500/50 bg-red-950/40 text-red-100",
  success: "border-emerald-500/50 bg-emerald-950/40 text-emerald-100",
  neutral: "border-white/10 bg-card/70",
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
          className={`rounded-xl border px-4 py-2 text-sm shadow-[0_8px_24px_-8px_rgb(0_0_0/0.45),0_2px_8px_-2px_rgb(0_0_0/0.3)] backdrop-blur-md ${TONE_CLASS[t.tone]}`}
        >
          {t.message}
        </div>
      ))}
    </div>
  );
}
