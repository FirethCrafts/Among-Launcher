import { getCurrentWindow } from '@tauri-apps/api/window';

export function Titlebar() {
  const win = getCurrentWindow();

  return (
    <div data-tauri-drag-region className="flex h-10 select-none items-center border-b border-border bg-background">
      <div data-tauri-drag-region className="flex-1 px-4 text-sm font-medium text-muted-foreground">
        Among Launcher
      </div>
      {/* Interactive buttons are NOT drag regions: `data-tauri-drag-region`
          lives only on the drag surface above (a "false" value would still
          match the attribute selector, so buttons carry no attribute at all). */}
      <div className="flex h-full">
        <button onClick={() => win.minimize()}
          title="Minimize" aria-label="Minimize"
          className="px-3 h-full text-muted-foreground transition-colors hover:bg-surface-2 hover:text-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring">
          <svg className="w-4 h-4" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
            <line x1="5" y1="12" x2="19" y2="12" />
          </svg>
        </button>
        <button onClick={() => win.toggleMaximize()}
          title="Maximize" aria-label="Maximize"
          className="px-3 h-full text-muted-foreground transition-colors hover:bg-surface-2 hover:text-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring">
          <svg className="w-4 h-4" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
            <rect x="5" y="5" width="14" height="14" rx="1" />
          </svg>
        </button>
        <button onClick={() => win.close()}
          title="Close" aria-label="Close"
          className="px-3 h-full text-muted-foreground transition-colors hover:bg-danger hover:text-white focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring">
          <svg className="w-4 h-4" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
            <line x1="6" y1="6" x2="18" y2="18" />
            <line x1="6" y1="18" x2="18" y2="6" />
          </svg>
        </button>
      </div>
    </div>
  );
}
