import { useState, useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { open } from "@tauri-apps/plugin-dialog";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { showToast } from "@/components/Toast";
import { ConfirmModal } from "@/components/Modal";
import { LibraryPickerModal } from "@/components/LibraryPickerModal";
import { useLauncher, type LauncherConfig } from "@/state/LauncherContext";
import { Play, Square, Gamepad2, FolderOpen, Package, Folder, Copy, Trash2, Archive, Library } from "lucide-react";

interface GameSearchResult {
  path?: string | null;
  storefront?: string | null;
  detected_but_unavailable?: boolean;
}

interface InstallStatus {
  bepinex_installed: boolean;
  among_api_installed: boolean;
}

interface ModEntry {
  name: string;
  filename: string;
  size: number;
  path: string;
  version?: string | null;
}

interface InstallProgress {
  stage: string;
  progress: number;
  total: number;
}

function formatBytes(bytes: number): string {
  if (bytes === 0) return "0 B";
  const k = 1024;
  const sizes = ["B", "KB", "MB", "GB"];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + " " + sizes[i];
}

export default function HomeView() {
  const navigate = useNavigate();
  const { config, updateConfig } = useLauncher();
  const [gamePath, setGamePath] = useState<string | null>(null);
  const [storefront, setStorefront] = useState<string | null>(null);
  const [detected, setDetected] = useState<GameSearchResult | null>(null);
  const [isRunning, setIsRunning] = useState(false);
  const [bepinexInstalled, setBepinexInstalled] = useState(false);
  const [amongApiInstalled, setAmongApiInstalled] = useState(false);
  const [mods, setMods] = useState<ModEntry[]>([]);
  const [installProgress, setInstallProgress] = useState<InstallProgress | null>(null);
  const [loading, setLoading] = useState(true);
  const [libraryPickerOpen, setLibraryPickerOpen] = useState(false);
  const [removeTarget, setRemoveTarget] = useState<ModEntry | null>(null);
  const [confirmStop, setConfirmStop] = useState(false);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const result = await invoke<GameSearchResult>("detect_game", {});
        if (!cancelled) setDetected(result);
      } catch {
        if (!cancelled) showToast("Failed to detect game", "error");
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();

    // `game-stopped` also fires on natural game exit and can fire twice on a
    // manual stop (command + IPC disconnect) — setting false is idempotent.
    const unlistenGameStopped = listen("game-stopped", () => {
      setIsRunning(false);
    });

    const unlistenInstallProgress = listen<InstallProgress>("install-progress", (event) => {
      setInstallProgress(event.payload);
      if (event.payload.stage === "complete") {
        setTimeout(() => setInstallProgress(null), 2000);
      }
    });

    return () => {
      cancelled = true;
      unlistenGameStopped.then((fn) => fn());
      unlistenInstallProgress.then((fn) => fn());
    };
  }, []);

  // Derive path + storefront from the shared config (context) plus local
  // detection fallback — no per-page read_config/whole-object write.
  useEffect(() => {
    if (!config) return;
    setStorefront(config.storefront || detected?.storefront || null);
    const moddedPath =
      config.modded_install_path && config.modded_install_path.trim()
        ? config.modded_install_path
        : null;
    setGamePath(moddedPath || detected?.path || null);
  }, [config, detected]);

  useEffect(() => {
    if (gamePath) {
      checkInstallStatus();
      loadMods();
    }
  }, [gamePath]);

  async function checkInstallStatus() {
    if (!gamePath) return;
    try {
      const status = await invoke<InstallStatus>("get_install_status", { gamePath });
      setBepinexInstalled(status.bepinex_installed);
      setAmongApiInstalled(status.among_api_installed);
    } catch {
      showToast("Failed to get install status", "error");
    }
  }

  async function loadMods() {
    if (!gamePath) return;
    try {
      const installedMods = await invoke<ModEntry[]>("get_mod_list", { gamePath });
      setMods(installedMods);
    } catch {
      showToast("Failed to get mods", "error");
    }
  }

  async function browseFiles(path: string) {
    try {
      await invoke("browse_files", { path });
    } catch {
      showToast("Failed to open file browser", "error");
    }
  }

  async function copyPath() {
    if (!gamePath) return;
    try {
      await navigator.clipboard.writeText(gamePath);
      showToast("Path copied to clipboard", "success");
    } catch {
      showToast("Failed to copy path", "error");
    }
  }

  async function launchGame() {
    if (!gamePath) return;
    try {
      await invoke("launch_game", { gamePath });
      setIsRunning(true);
    } catch {
      showToast("Failed to launch game", "error");
    }
  }

  async function stopGame() {
    try {
      await invoke("stop_game");
      setIsRunning(false);
    } catch {
      showToast("Failed to stop game", "error");
    }
  }

  async function handleToggle(field: "auto_post_lobby" | "debug_mode") {
    // Guard BEFORE any optimistic flip: without config there is nothing to
    // merge into or persist, and flipping first shows a phantom toggle. (#8)
    if (!config) {
      showToast("Settings are still loading", "error");
      return;
    }
    try {
      const partial: Partial<LauncherConfig> =
        field === "auto_post_lobby"
          ? { auto_post_lobby: !config.auto_post_lobby }
          : { debug_mode: !config.debug_mode };
      // updateConfig applies the flip optimistically and rolls it back on
      // failure — no local mirror state to desync.
      await updateConfig(partial);
    } catch {
      showToast("Failed to save option", "error");
    }
  }

  async function removeMod(mod: ModEntry) {
    if (!gamePath) return;
    try {
      await invoke("remove_mod", { gamePath, filename: mod.filename });
      await loadMods();
      showToast("Mod removed", "success");
    } catch {
      showToast("Failed to remove mod", "error");
    }
  }

  async function handleImportMod() {
    if (!gamePath) return;
    try {
      const selected = await open({
        multiple: true,
        filters: [{ name: "DLL Files", extensions: ["dll"] }],
      });
      if (selected) {
        const paths = Array.isArray(selected) ? selected : [selected];
        await invoke("import_mod", { gamePath, modPaths: paths });
        await loadMods();
      }
    } catch {
      showToast("Failed to import mod", "error");
    }
  }

  async function copyModToLibrary(mod: ModEntry) {
    try {
      await invoke("add_to_library", { sourcePath: mod.path });
      showToast(`Saved ${mod.filename} to library`, "success");
    } catch {
      showToast("Failed to save mod to library", "error");
    }
  }

  const isReady = !!gamePath && bepinexInstalled && amongApiInstalled;
  const libraryCount = config?.library.length ?? 0;

  return (
    <div className="min-h-full p-6 space-y-6">
      <h1 className="text-3xl font-bold tracking-tight">Home</h1>

      {loading ? (
        <div className="grid gap-6 md:grid-cols-2">
          <Card>
            <CardHeader>
              <Skeleton className="h-5 w-32" />
            </CardHeader>
            <CardContent className="space-y-3">
              <Skeleton className="h-4 w-full" />
              <Skeleton className="h-4 w-3/4" />
              <Skeleton className="h-4 w-1/2" />
              <Skeleton className="h-9 w-full mt-4" />
            </CardContent>
          </Card>
          <Card>
            <CardHeader>
              <Skeleton className="h-5 w-32" />
            </CardHeader>
            <CardContent className="space-y-3">
              <Skeleton className="h-12 w-full" />
              <Skeleton className="h-12 w-full" />
              <Skeleton className="h-9 w-full mt-4" />
            </CardContent>
          </Card>
        </div>
      ) : (
        <>
          {installProgress && (
            <Card>
          <CardContent className="pt-6">
            <div className="space-y-2">
              <div className="flex items-center justify-between text-sm">
                <span className="font-medium capitalize">
                  {installProgress.stage === "complete" ? "Install Complete" : installProgress.stage}
                </span>
                <span className="text-muted-foreground">
                  {installProgress.total > 0
                    ? `${Math.round((installProgress.progress / installProgress.total) * 100)}%`
                    : "Preparing..."}
                </span>
              </div>
              <div className="h-2 w-full overflow-hidden rounded-full bg-secondary">
                <div
                  className="h-full bg-primary transition-all duration-300"
                  style={{
                    width:
                      installProgress.total > 0
                        ? `${(installProgress.progress / installProgress.total) * 100}%`
                        : "100%",
                  }}
                />
              </div>
            </div>
          </CardContent>
        </Card>
      )}

      <Card className="glass-shadow">
        <CardContent className="p-8">
          <div className="flex flex-col gap-6 md:flex-row md:items-center md:justify-between">
            <div className="space-y-2">
              <div className="flex items-center gap-3">
                <h2 className="text-3xl font-bold tracking-tight">
                  {isReady ? "Game Ready" : "Setup Needed"}
                </h2>
                {isReady ? (
                  <Badge variant="neutral" showDot dotColor="emerald">
                    Ready
                  </Badge>
                ) : (
                  <Badge variant="muted" showDot dotColor="red">
                    Incomplete
                  </Badge>
                )}
              </div>
              <p className="text-sm text-muted-foreground">
                {isReady
                  ? "Everything is set up. Jump into a lobby."
                  : "Finish installing Among Us + BepInEx to play."}
              </p>
            </div>
            <div>
              {isReady ? (
                isRunning ? (
                  <Button onClick={() => setConfirmStop(true)} variant="destructive" size="lg">
                    <Square className="h-5 w-5" />
                    Stop
                  </Button>
                ) : (
                  <Button onClick={() => void launchGame()} size="lg">
                    <Play className="h-5 w-5" />
                    Launch
                  </Button>
                )
              ) : (
                <Button onClick={() => navigate("/setup")} variant="outline" size="lg">
                  <Gamepad2 className="h-5 w-5" />
                  Set Up Game
                </Button>
              )}
            </div>
          </div>

          <div className="mt-6 flex flex-wrap items-center gap-3 border-t pt-6">
            {storefront && (
              <Badge variant="muted" className="capitalize">
                {storefront.replace("_", " ")}
              </Badge>
            )}
            {gamePath ? (
              <div className="flex min-w-0 items-center gap-1">
                <span className="text-xs font-mono text-muted-foreground truncate max-w-[320px]">
                  {gamePath}
                </span>
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => void copyPath()}
                  aria-label="Copy game path"
                  title="Copy game path"
                >
                  <Copy className="h-3 w-3" />
                </Button>
              </div>
            ) : (
              <span className="text-xs text-muted-foreground">No game path detected</span>
            )}
            <div className="flex items-center gap-2">
              <Badge variant={bepinexInstalled ? "neutral" : "muted"}>
                BepInEx {bepinexInstalled ? "installed" : "missing"}
              </Badge>
              <Badge variant={amongApiInstalled ? "neutral" : "muted"}>
                AmongApi {amongApiInstalled ? "installed" : "missing"}
              </Badge>
            </div>
          </div>
        </CardContent>
      </Card>

      <div className="grid gap-6 md:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Package className="h-5 w-5" />
              Installed Mods
            </CardTitle>
          </CardHeader>
          <CardContent>
            {mods.length === 0 ? (
              <p className="text-sm text-muted-foreground">
                No mods installed yet — use Import Mod or From Library below.
              </p>
            ) : (
              <ul className="space-y-1">
                {mods.map((mod) => (
                  <li
                    key={mod.filename}
                    className="flex items-center justify-between rounded-xl px-3 py-2 transition-colors hover:bg-white/5"
                  >
                    <div className="min-w-0">
                      <span className="text-sm font-medium">{mod.name}</span>
                      {mod.version && (
                        <span className="ml-2 text-xs text-muted-foreground">
                          v{mod.version}
                        </span>
                      )}
                      <span className="ml-2 text-xs text-muted-foreground">
                        {formatBytes(mod.size)}
                      </span>
                    </div>
                    <div className="flex items-center gap-1">
                      <Button
                        variant="ghost"
                        size="sm"
                        onClick={() => void copyModToLibrary(mod)}
                        aria-label={`Save ${mod.name} to library`}
                        title="Save to library"
                      >
                        <Archive className="h-3 w-3" />
                      </Button>
                      <Button
                        variant="ghost"
                        size="sm"
                        onClick={() => void browseFiles(mod.path)}
                        aria-label={`Open ${mod.name} folder`}
                        title="Open folder"
                      >
                        <Folder className="h-3 w-3" />
                      </Button>
                      <Button
                        variant="ghost"
                        size="sm"
                        onClick={() => setRemoveTarget(mod)}
                        aria-label={`Remove ${mod.name}`}
                        title="Remove mod"
                      >
                        <Trash2 className="h-3 w-3" />
                      </Button>
                    </div>
                  </li>
                ))}
              </ul>
            )}
            <div className="flex gap-2 mt-4">
              <Button onClick={() => void handleImportMod()} variant="outline" className="flex-1">
                <FolderOpen className="h-4 w-4 mr-2" />
                Import Mod
              </Button>
              <Button
                onClick={() => setLibraryPickerOpen(true)}
                variant="outline"
                className="flex-1"
                disabled={libraryCount === 0}
                title={libraryCount === 0 ? "Library is empty" : "Install a mod from your library"}
              >
                <Library className="h-4 w-4 mr-2" />
                From Library
              </Button>
            </div>
            <LibraryPickerModal
              isOpen={libraryPickerOpen}
              onClose={() => setLibraryPickerOpen(false)}
              gamePath={gamePath}
              onInstalled={loadMods}
            />
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Options</CardTitle>
          </CardHeader>
          <CardContent>
            <div className="space-y-4">
              <div className="flex items-center justify-between">
                <span className="text-sm font-medium">Auto-post game data</span>
                <Switch
                  checked={config?.auto_post_lobby ?? false}
                  disabled={!config}
                  onCheckedChange={() => void handleToggle("auto_post_lobby")}
                />
              </div>
              <div className="flex items-center justify-between">
                <span className="text-sm font-medium">Debug mode</span>
                <Switch
                  checked={config?.debug_mode ?? false}
                  disabled={!config}
                  onCheckedChange={() => void handleToggle("debug_mode")}
                />
              </div>
            </div>
          </CardContent>
        </Card>
      </div>

      <ConfirmModal
        isOpen={removeTarget !== null}
        onClose={() => setRemoveTarget(null)}
        onConfirm={() => {
          const mod = removeTarget;
          if (mod) void removeMod(mod);
        }}
        title="Remove mod?"
        message={`Remove ${removeTarget?.filename ?? ""} from the modded install? You can add it back later via Import Mod or Library.`}
        danger
        confirmText="Remove"
      />
      <ConfirmModal
        isOpen={confirmStop}
        onClose={() => setConfirmStop(false)}
        onConfirm={() => void stopGame()}
        title="Stop the game?"
        message="Among Us will close immediately. Progress in the current match is lost."
        confirmText="Stop"
      />
        </>
      )}
    </div>
  );
}
