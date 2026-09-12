import { useState, useEffect } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { open } from "@tauri-apps/plugin-dialog";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { showToast } from "@/components/Toast";
import { Play, Square, Gamepad2, FolderOpen, Package, Folder, Copy } from "lucide-react";

interface GameSearchResult {
  path?: string | null;
  storefront?: string | null;
  detected_but_unavailable?: boolean;
}

interface InstallStatus {
  bepinex_installed: boolean;
  among_api_installed: boolean;
}

interface LauncherConfig {
  storefront?: string | null;
  modded_install_path: string;
  debug_mode: boolean;
  auto_post_lobby: boolean;
}

interface ModEntry {
  name: string;
  filename: string;
  size: number;
  path: string;
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
  const [gamePath, setGamePath] = useState<string | null>(null);
  const [storefront, setStorefront] = useState<string | null>(null);
  const [isRunning, setIsRunning] = useState(false);
  const [autoPost, setAutoPost] = useState(false);
  const [debugMode, setDebugMode] = useState(false);
  const [bepinexInstalled, setBepinexInstalled] = useState(false);
  const [amongApiInstalled, setAmongApiInstalled] = useState(false);
  const [mods, setMods] = useState<ModEntry[]>([]);
  const [config, setConfig] = useState<LauncherConfig | null>(null);
  const [installProgress, setInstallProgress] = useState<InstallProgress | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    load().finally(() => setLoading(false));

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
      unlistenGameStopped.then((fn) => fn());
      unlistenInstallProgress.then((fn) => fn());
    };
  }, []);

  useEffect(() => {
    if (gamePath) {
      checkInstallStatus();
      loadMods();
    }
  }, [gamePath]);

  async function load() {
    let detected: GameSearchResult | null = null;
    let cfg: LauncherConfig | null = null;
    try {
      detected = await invoke<GameSearchResult>("detect_game", {});
    } catch {
      showToast("Failed to detect game", "error");
    }
    try {
      cfg = await invoke<LauncherConfig>("read_config");
    } catch {
      showToast("Failed to load config", "error");
    }

    if (cfg) {
      setConfig(cfg);
      setAutoPost(cfg.auto_post_lobby);
      setDebugMode(cfg.debug_mode);
      setStorefront(cfg.storefront || detected?.storefront || null);
      const moddedPath =
        cfg.modded_install_path && cfg.modded_install_path.trim()
          ? cfg.modded_install_path
          : null;
      setGamePath(moddedPath || detected?.path || null);
    } else if (detected?.path) {
      setGamePath(detected.path);
      setStorefront(detected.storefront || null);
    }
  }

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

  async function handleToggle(field: keyof LauncherConfig, currentValue: boolean) {
    const newValue = !currentValue;
    if (field === "auto_post_lobby") setAutoPost(newValue);
    if (field === "debug_mode") setDebugMode(newValue);

    if (config) {
      const newConfig = { ...config, [field]: newValue };
      try {
        await invoke("write_config", { newConfig });
        setConfig(newConfig);
      } catch {
        showToast("Failed to save option", "error");
        if (field === "auto_post_lobby") setAutoPost(!newValue);
        if (field === "debug_mode") setDebugMode(!newValue);
      }
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

      <div className="grid gap-6 md:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Gamepad2 className="h-5 w-5" />
              Game Status
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            <div className="flex items-center justify-between">
              <span className="text-sm text-muted-foreground">Status</span>
              {gamePath ? (
                <Badge variant="neutral" showDot dotColor="emerald">
                  Installed
                </Badge>
              ) : (
                <Badge variant="muted" showDot dotColor="red">
                  Not Installed
                </Badge>
              )}
            </div>
            {storefront && (
              <div className="flex items-center justify-between">
                <span className="text-sm text-muted-foreground">Storefront</span>
                <span className="text-sm font-medium capitalize">{storefront.replace("_", " ")}</span>
              </div>
            )}
            {gamePath && (
              <div className="flex items-center justify-between gap-2">
                <span className="text-sm text-muted-foreground">Path</span>
                <div className="flex items-center gap-1">
                  <span className="text-xs font-mono text-muted-foreground truncate max-w-[200px]">
                    {gamePath}
                  </span>
                  <Button variant="ghost" size="sm" onClick={copyPath} aria-label="Copy game path">
                    <Copy className="h-3 w-3" />
                  </Button>
                </div>
              </div>
            )}
            {gamePath && (
              <div className="flex items-center justify-between">
                <span className="text-sm text-muted-foreground">BepInEx</span>
                <Badge variant={bepinexInstalled ? "neutral" : "muted"}>
                  {bepinexInstalled ? "Installed" : "Not Installed"}
                </Badge>
              </div>
            )}
            {gamePath && (
              <div className="flex items-center justify-between">
                <span className="text-sm text-muted-foreground">AmongApi</span>
                <Badge variant={amongApiInstalled ? "neutral" : "muted"}>
                  {amongApiInstalled ? "Installed" : "Not Installed"}
                </Badge>
              </div>
            )}
            <div className="flex gap-2 pt-2">
              <Button
                onClick={launchGame}
                disabled={!gamePath || isRunning}
                className="flex-1"
              >
                <Play className="h-4 w-4" />
                Launch
              </Button>
              <Button
                onClick={stopGame}
                disabled={!isRunning}
                variant="destructive"
                className="flex-1"
              >
                <Square className="h-4 w-4" />
                Stop
              </Button>
            </div>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Package className="h-5 w-5" />
              Installed Mods
            </CardTitle>
          </CardHeader>
          <CardContent>
            {mods.length === 0 ? (
              <p className="text-sm text-muted-foreground">No mods installed.</p>
            ) : (
              <ul className="space-y-2">
                {mods.map((mod) => (
                  <li
                    key={mod.filename}
                    className="flex items-center justify-between rounded-lg border px-3 py-2"
                  >
                    <div className="min-w-0">
                      <span className="text-sm font-medium">{mod.name}</span>
                      <span className="ml-2 text-xs text-muted-foreground">
                        {formatBytes(mod.size)}
                      </span>
                    </div>
                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={() => browseFiles(mod.path)}
                    >
                      <Folder className="h-3 w-3" />
                    </Button>
                  </li>
                ))}
              </ul>
            )}
            <Button onClick={handleImportMod} variant="outline" className="w-full mt-4">
              <FolderOpen className="h-4 w-4 mr-2" />
              Import Mod
            </Button>
          </CardContent>
        </Card>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Options</CardTitle>
        </CardHeader>
        <CardContent>
          <div className="space-y-4">
            <div className="flex items-center justify-between">
              <span className="text-sm font-medium">Auto-post game data</span>
              <Switch
                checked={autoPost}
                onCheckedChange={() => handleToggle("auto_post_lobby", autoPost)}
              />
            </div>
            <div className="flex items-center justify-between">
              <span className="text-sm font-medium">Debug mode</span>
              <Switch
                checked={debugMode}
                onCheckedChange={() => handleToggle("debug_mode", debugMode)}
              />
            </div>
          </div>
        </CardContent>
      </Card>
        </>
      )}
    </div>
  );
}
