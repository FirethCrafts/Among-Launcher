import { useState, useEffect } from "react";
import { invoke } from "@tauri-apps/api/core";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { showToast, formatError } from "@/components/Toast";
import { ConfirmModal } from "@/components/Modal";
import { useLauncher } from "@/state/LauncherContext";
import { Archive, Download, Trash2 } from "lucide-react";

interface LibraryEntry {
  path: string;
  storefront?: string | null;
}

function filenameOf(path: string): string {
  return path.split(/[/\\]/).pop() || path;
}

export default function LibraryView() {
  // gamePath comes from the shared config (no per-page read_config).
  const { config, refreshConfig } = useLauncher();
  const [entries, setEntries] = useState<LibraryEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [removeTarget, setRemoveTarget] = useState<string | null>(null);

  const gamePath =
    config?.modded_install_path && config.modded_install_path.trim()
      ? config.modded_install_path
      : null;

  useEffect(() => {
    load().finally(() => setLoading(false));
  }, []);

  async function load() {
    try {
      const library = await invoke<LibraryEntry[]>("list_library");
      setEntries(library);
    } catch (e) {
      showToast(`Failed to load library: ${formatError(e)}`, "error");
    }
  }

  async function handleInstall(filename: string) {
    if (!gamePath) {
      showToast("No modded game path set", "error");
      return;
    }
    try {
      await invoke("install_from_library", { gamePath, filename });
      showToast(`Installed ${filename}`, "success");
    } catch (e) {
      showToast(`Failed to install ${filename}: ${formatError(e)}`, "error");
    } finally {
      await load();
      // install_from_library mutates config.profiles backend-side; keep the
      // shared config fresh so other pages don't write stale data back.
      await refreshConfig();
    }
  }

  async function handleRemove(filename: string) {
    try {
      await invoke("remove_from_library", { filename });
      showToast(`Removed ${filename}`, "success");
    } catch (e) {
      showToast(`Failed to remove ${filename}: ${formatError(e)}`, "error");
    } finally {
      await load();
      // remove_from_library mutates config.library backend-side.
      await refreshConfig();
    }
  }

  return (
    <div className="min-h-full p-6 space-y-6">
      <div className="flex items-center gap-3">
        <h1 className="text-3xl font-bold tracking-tight">Library</h1>
        {!loading && (
          <Badge variant="muted">
            {entries.length} {entries.length === 1 ? "mod" : "mods"}
          </Badge>
        )}
      </div>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Archive className="h-5 w-5" />
            Saved Mods
          </CardTitle>
        </CardHeader>
        <CardContent>
          {loading ? (
            <p className="text-sm text-muted-foreground">Loading library...</p>
          ) : entries.length === 0 ? (
            <div className="space-y-1">
              <p className="text-sm text-muted-foreground">
                Your library is empty — nothing saved yet.
              </p>
              <p className="text-sm text-muted-foreground">
                On Home, hover an installed mod and press the Archive
                (&quot;Save to library&quot;) button to stash it here, then
                reinstall it with one click from this page.
              </p>
            </div>
          ) : (
            <ul className="space-y-2">
              {entries.map((entry) => {
                const filename = filenameOf(entry.path);
                return (
                  <li
                    key={entry.path}
                    className="flex items-center justify-between rounded-xl border border-white/10 bg-white/[0.03] px-3 py-2 transition-colors hover:bg-white/[0.07]"
                  >
                    <div className="min-w-0">
                      <span className="text-sm font-medium">{filename}</span>
                      <div className="truncate font-mono text-xs text-muted-foreground">
                        {entry.path}
                      </div>
                    </div>
                    <div className="flex shrink-0 items-center gap-1">
                      <Button
                        variant="ghost"
                        size="sm"
                        onClick={() => void handleInstall(filename)}
                        aria-label={`Install ${filename}`}
                        title="Install into modded game"
                      >
                        <Download className="h-3 w-3" />
                      </Button>
                      <Button
                        variant="ghost"
                        size="sm"
                        onClick={() => setRemoveTarget(filename)}
                        aria-label={`Remove ${filename}`}
                        title="Remove from library"
                      >
                        <Trash2 className="h-3 w-3" />
                      </Button>
                    </div>
                  </li>
                );
              })}
            </ul>
          )}
        </CardContent>
      </Card>

      <ConfirmModal
        isOpen={removeTarget !== null}
        onClose={() => setRemoveTarget(null)}
        onConfirm={() => {
          const filename = removeTarget;
          if (filename) void handleRemove(filename);
        }}
        title="Remove from library?"
        message={`Delete ${removeTarget ?? ""} from your library? The installed copy in your modded game is not affected.`}
        danger
        confirmText="Remove"
      />
    </div>
  );
}
