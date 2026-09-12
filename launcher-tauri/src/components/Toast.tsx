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

export function showToast(message: string, tone: 'neutral' | 'success' | 'error' = 'neutral') {
  window.dispatchEvent(new CustomEvent('app-toast', { detail: { message, tone } }));
}
export function ToastHost() {
  const [items, setItems] = React.useState<{ id: number; message: string; tone: string }[]>([]);
  React.useEffect(() => {
    const handler = (e: Event) => {
      const { message, tone } = (e as CustomEvent).detail;
      const id = Date.now() + Math.random();
      const safeMessage = typeof message === "string" ? message : formatError(message);
      setItems((prev) => [...prev, { id, message: safeMessage, tone }]);
      setTimeout(() => setItems((prev) => prev.filter((t) => t.id !== id)), 3000);
    };
    window.addEventListener('app-toast', handler);
    return () => window.removeEventListener('app-toast', handler);
  }, []);
  return (
    <div className="fixed bottom-6 right-6 z-50 flex flex-col gap-2">
      {items.map((t) => (
        <div key={t.id} className="rounded-lg border bg-card px-4 py-2 text-sm shadow-lg">{t.message}</div>
      ))}
    </div>
  );
}
