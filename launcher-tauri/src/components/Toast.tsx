import * as React from "react"

export function showToast(message: string, tone: 'neutral' | 'success' | 'error' = 'neutral') {
  window.dispatchEvent(new CustomEvent('app-toast', { detail: { message, tone } }));
}
export function ToastHost() {
  const [items, setItems] = React.useState<{ id: number; message: string; tone: string }[]>([]);
  React.useEffect(() => {
    const handler = (e: Event) => {
      const { message, tone } = (e as CustomEvent).detail;
      const id = Date.now() + Math.random();
      setItems((prev) => [...prev, { id, message, tone }]);
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
