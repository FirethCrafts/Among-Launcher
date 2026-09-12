import { useState, useEffect } from "react";
import { invoke } from "@tauri-apps/api/core";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { showToast, formatError } from "@/components/Toast";
import { Archive, Download, Trash2 } from "lucide-react";

interface LibraryEntry {
  path: string;
  storefront?: string | null;
}

interface LauncherConfig {
  modded_install_path: string;
}

function filenameOf(path: string): string {
  return path.split(/[/\\]/).pop() || path;
}

export default function LibraryView() {
  const [entries, setEntries] = useState<LibraryEntry[]>([]);
  const [gamePath, setGamePath] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    load().finally(() => setLoading(false));
  }, []);

  async function load() {
    try {
      const cfg = await invoke<LauncherConfig>("read_config");
      const moddedPath =
        cfg.modded_install_path && cfg.modded_install_path.trim()
          ? cfg.modded_install_path
          : null;
      setGamePath(moddedPath);
    } catch {
      showToast("Failed to load config", "error");
    }
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
    }
  }

  async function handleRemove(filename: string) {
    if (!confirm(`Remove ${filename} from library?`)) return;
    try {
      await invoke("remove_from_library", { filename });
      showToast(`Removed ${filename}`, "success");
    } catch (e) {
      showToast(`Failed to remove ${filename}: ${formatError(e)}`, "error");
    } finally {
      await load();
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
            <p className="text-sm text-muted-foreground">
              Your library is empty. Copy mods here from Home to reuse them later.
            </p>
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
                        onClick={() => handleInstall(filename)}
                        aria-label={`Install ${filename}`}
                      >
                        <Download className="h-3 w-3" />
                      </Button>
                      <Button
                        variant="ghost"
                        size="sm"
                        onClick={() => handleRemove(filename)}
                        aria-label={`Remove ${filename}`}
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
    </div>
  );
}
