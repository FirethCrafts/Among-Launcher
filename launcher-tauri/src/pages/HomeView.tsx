import { useState, useEffect } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { open } from "@tauri-apps/plugin-dialog";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import type { ReactNode } from "react";
// TEMP Task 2 shim: ui/3d-card deleted; Tasks 3-4 remove these tilt wrappers.
const CardContainer = ({ children, className }: { children?: ReactNode; className?: string }) => (
  <div className={className}>{children}</div>
);
const CardBody = ({ children, className }: { children?: ReactNode; className?: string }) => (
  <div className={className}>{children}</div>
);
const CardItem = ({ children, className }: { children?: ReactNode; className?: string; translateZ?: number }) => (
  <div className={className}>{children}</div>
);
import { Play, Square, Gamepad2, CheckCircle2, XCircle, FolderOpen, Package, Folder } from "lucide-react";

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
    Promise.all([detectGame(), loadConfig()]).finally(() => setLoading(false));

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

  async function detectGame() {
    try {
      const result = await invoke<GameSearchResult>("detect_game", {});
      if (result.path) {
        setGamePath(result.path);
        setStorefront(result.storefront || null);
      }
    } catch (e) {
      console.error("Failed to detect game:", e);
    }
  }

  async function loadConfig() {
    try {
      const cfg = await invoke<LauncherConfig>("read_config");
      setConfig(cfg);
      setAutoPost(cfg.auto_post_lobby);
      setDebugMode(cfg.debug_mode);
    } catch (e) {
      console.error("Failed to load config:", e);
    }
  }

  async function checkInstallStatus() {
    if (!gamePath) return;
    try {
      const status = await invoke<InstallStatus>("get_install_status", { gamePath });
      setBepinexInstalled(status.bepinex_installed);
      setAmongApiInstalled(status.among_api_installed);
    } catch (e) {
      console.error("Failed to get install status:", e);
    }
  }

  async function loadMods() {
    if (!gamePath) return;
    try {
      const installedMods = await invoke<ModEntry[]>("get_mod_list", { gamePath });
      setMods(installedMods);
    } catch (e) {
      console.error("Failed to get mods:", e);
    }
  }

  async function browseFiles(path: string) {
    try {
      await invoke("browse_files", { path });
    } catch (e) {
      console.error("Failed to browse files:", e);
    }
  }

  async function launchGame() {
    if (!gamePath) return;
    try {
      await invoke("launch_game", { gamePath });
      setIsRunning(true);
    } catch (e) {
      console.error("Failed to launch game:", e);
    }
  }

  async function stopGame() {
    try {
      await invoke("stop_game");
      setIsRunning(false);
    } catch (e) {
      console.error("Failed to stop game:", e);
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
      } catch (e) {
        console.error("Failed to write config:", e);
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
    } catch (e) {
      console.error("Failed to import mod:", e);
    }
  }

  return (
    <div className="min-h-full bg-grid p-6 space-y-6">
      <h1 className="font-display text-3xl font-bold text-primary">Home</h1>

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
            <Card className="glow-primary">
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
        <CardContainer className="w-full">
          <CardBody>
            <CardItem translateZ={20}>
              <Card className="w-full glow-primary">
                <CardHeader>
                  <CardTitle className="flex items-center gap-2">
                    <Gamepad2 className="h-5 w-5" />
                    Game Status
                  </CardTitle>
                </CardHeader>
                <CardContent className="space-y-4">
                  <div className="flex items-center justify-between">
                    <span className="text-sm text-muted-foreground">Status</span>
                    <Badge variant={gamePath ? "neutral" : "muted"}>
                      {gamePath ? (
                        <span className="flex items-center gap-1">
                          <CheckCircle2 className="h-3 w-3" /> Installed
                        </span>
                      ) : (
                        <span className="flex items-center gap-1">
                          <XCircle className="h-3 w-3 text-destructive" /> Not Installed
                        </span>
                      )}
                    </Badge>
                  </div>
                  {storefront && (
                    <div className="flex items-center justify-between">
                      <span className="text-sm text-muted-foreground">Storefront</span>
                      <span className="text-sm font-medium capitalize">{storefront.replace("_", " ")}</span>
                    </div>
                  )}
                  {gamePath && (
                    <div className="flex items-center justify-between">
                      <span className="text-sm text-muted-foreground">Path</span>
                      <span className="text-xs font-mono text-muted-foreground truncate max-w-[200px]">
                        {gamePath}
                      </span>
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
            </CardItem>
          </CardBody>
        </CardContainer>

        <CardContainer className="w-full">
          <CardBody>
            <CardItem translateZ={20}>
              <Card className="w-full glow-sky">
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
                          className="flex items-center justify-between rounded-lg bg-secondary/50 px-3 py-2"
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
            </CardItem>
          </CardBody>
        </CardContainer>
      </div>

      <Card className="glow-violet">
        <CardHeader>
          <CardTitle>Options</CardTitle>
        </CardHeader>
        <CardContent>
          <div className="space-y-4">
            <label className="flex items-center justify-between cursor-pointer">
              <span className="text-sm font-medium">Auto-post game data</span>
              <button
                onClick={() => handleToggle("auto_post_lobby", autoPost)}
                className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${
                  autoPost ? "bg-primary" : "bg-muted"
                }`}
              >
                <span
                  className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${
                    autoPost ? "translate-x-6" : "translate-x-1"
                  }`}
                />
              </button>
            </label>
            <label className="flex items-center justify-between cursor-pointer">
              <span className="text-sm font-medium">Debug mode</span>
              <button
                onClick={() => handleToggle("debug_mode", debugMode)}
                className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${
                  debugMode ? "bg-primary" : "bg-muted"
                }`}
              >
                <span
                  className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${
                    debugMode ? "translate-x-6" : "translate-x-1"
                  }`}
                />
              </button>
            </label>
          </div>
        </CardContent>
      </Card>
        </>
      )}
    </div>
  );
}
