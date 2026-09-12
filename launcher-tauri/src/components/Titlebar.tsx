import { getCurrentWindow } from '@tauri-apps/api/window';

export function Titlebar() {
  const win = getCurrentWindow();

  return (
    <div data-tauri-drag-region className="flex items-center h-10 bg-background border-b border-border select-none">
      <div data-tauri-drag-region className="flex-1 px-4 text-sm font-medium text-muted-foreground">
        Among Launcher
      </div>
      <div data-tauri-drag-region="false" className="flex h-full">
        <button data-tauri-drag-region="false" onClick={() => win.minimize()}
          className="px-3 h-full hover:bg-muted transition-colors">
          <svg className="w-4 h-4" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
            <line x1="5" y1="12" x2="19" y2="12" />
          </svg>
        </button>
        <button data-tauri-drag-region="false" onClick={() => win.toggleMaximize()}
          className="px-3 h-full hover:bg-muted transition-colors">
          <svg className="w-4 h-4" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
            <rect x="5" y="5" width="14" height="14" rx="1" />
          </svg>
        </button>
        <button data-tauri-drag-region="false" onClick={() => win.close()}
          className="px-3 h-full hover:bg-destructive hover:text-destructive-foreground transition-colors">
          <svg className="w-4 h-4" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
            <line x1="6" y1="6" x2="18" y2="18" />
            <line x1="6" y1="18" x2="18" y2="6" />
          </svg>
        </button>
      </div>
    </div>
  );
}
