import { useState, useEffect } from "react";
import { invoke } from "@tauri-apps/api/core";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { EmptyState } from "@/components/ui/empty-state";
import { ListRow } from "@/components/ui/list-row";
import { PageHeader } from "@/components/ui/page-header";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { showToast, formatError } from "@/components/Toast";
import { useLauncher } from "@/state/LauncherContext";
import { Archive, Download, RefreshCw, Trash2 } from "lucide-react";

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
    <div className="min-h-full space-y-6 pb-6">
      <PageHeader
        title="Library"
        actions={
          <>
            {!loading && (
              <Badge variant="muted">
                {entries.length} {entries.length === 1 ? "mod" : "mods"}
              </Badge>
            )}
            <Button
              variant="outline"
              size="sm"
              onClick={() => void load()}
              title="Refresh library"
              aria-label="Refresh library"
            >
              <RefreshCw className="h-3.5 w-3.5" aria-hidden="true" />
              Refresh
            </Button>
          </>
        }
      />

      <div className="space-y-6 px-6">
      {loading ? (
        <div className="space-y-2" aria-busy="true" aria-label="Loading library">
          {[0, 1, 2].map((i) => (
            <div
              key={i}
              className="flex items-center justify-between gap-3 rounded-card border border-border bg-surface px-3 py-3"
            >
              <div className="min-w-0 flex-1 space-y-2">
                <Skeleton className="h-3.5 w-40" />
                <Skeleton className="h-3 w-64 max-w-full" />
              </div>
              <div className="flex shrink-0 items-center gap-1.5">
                <Skeleton className="h-8 w-20" />
                <Skeleton className="h-8 w-8" />
              </div>
            </div>
          ))}
        </div>
      ) : entries.length === 0 ? (
        <EmptyState
          icon={<Archive className="h-5 w-5" aria-hidden="true" />}
          title="Your library is empty"
          description="Save an installed mod from Home to stash it here, then reinstall it with one click."
          className="py-4"
        />
      ) : (
        <ul className="divide-y divide-border overflow-hidden rounded-card border border-border bg-surface">
          {entries.map((entry) => {
            const filename = filenameOf(entry.path);
            return (
              <ListRow
                key={entry.path}
                title={filename}
                subtitle={
                  <span className="block truncate font-mono text-2xs">{entry.path}</span>
                }
                meta={
                  entry.storefront ? (
                    <Badge variant="neutral" className="capitalize">
                      {entry.storefront}
                    </Badge>
                  ) : undefined
                }
                actions={
                  <>
                    <Button
                      variant="primary"
                      size="sm"
                      onClick={() => void handleInstall(filename)}
                      aria-label={`Install ${filename}`}
                      title="Install into modded game"
                    >
                      <Download className="h-3.5 w-3.5" aria-hidden="true" />
                      Install
                    </Button>
                    <Button
                      variant="ghost"
                      size="icon"
                      onClick={() => setRemoveTarget(filename)}
                      aria-label={`Remove ${filename}`}
                      title="Remove from library"
                      className="text-danger hover:bg-danger/10 hover:text-danger"
                    >
                      <Trash2 className="h-3.5 w-3.5" aria-hidden="true" />
                    </Button>
                  </>
                }
              />
            );
          })}
        </ul>
      )}
      </div>

      <ConfirmDialog
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
