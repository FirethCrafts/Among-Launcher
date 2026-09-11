import { useState, useEffect } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { open } from "@tauri-apps/plugin-dialog";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { CardContainer, CardBody, CardItem } from "@/components/ui/3d-card";
import { Play, Square, Gamepad2, CheckCircle2, XCircle, FolderOpen, Package } from "lucide-react";

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
  version?: string;
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

  useEffect(() => {
    detectGame();
    loadConfig();

    const unlisten = listen("game-stopped", () => {
      setIsRunning(false);
    });

    return () => {
      unlisten.then((fn) => fn());
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
      const installedMods = await invoke<ModEntry[]>("get_mods");
      setMods(installedMods);
    } catch (e) {
      console.error("Failed to get mods:", e);
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

  async function handleAutoPostToggle() {
    const newValue = !autoPost;
    setAutoPost(newValue);
    if (config) {
      const newConfig = { ...config, auto_post_lobby: newValue };
      try {
        await invoke("write_config", { newConfig });
        setConfig(newConfig);
      } catch (e) {
        console.error("Failed to write config:", e);
        setAutoPost(!newValue);
      }
    }
  }

  async function handleDebugModeToggle() {
    const newValue = !debugMode;
    setDebugMode(newValue);
    if (config) {
      const newConfig = { ...config, debug_mode: newValue };
      try {
        await invoke("write_config", { newConfig });
        setConfig(newConfig);
      } catch (e) {
        console.error("Failed to write config:", e);
        setDebugMode(!newValue);
      }
    }
  }

  async function handleImportMod() {
    try {
      const selected = await open({
        multiple: true,
        filters: [{ name: "DLL Files", extensions: ["dll"] }],
      });
      if (selected) {
        console.log("Selected files:", selected);
      }
    } catch (e) {
      console.error("Failed to open dialog:", e);
    }
  }

  return (
    <div className="min-h-full bg-grid p-6 space-y-6">
      <h1 className="font-display text-3xl font-bold text-primary">Home</h1>

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
                    <Badge variant={gamePath ? "default" : "outline"}>
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
                      <Badge variant={bepinexInstalled ? "default" : "outline"}>
                        {bepinexInstalled ? "Installed" : "Not Installed"}
                      </Badge>
                    </div>
                  )}
                  {gamePath && (
                    <div className="flex items-center justify-between">
                      <span className="text-sm text-muted-foreground">AmongApi</span>
                      <Badge variant={amongApiInstalled ? "default" : "outline"}>
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
                          key={mod.name}
                          className="flex items-center justify-between rounded-lg bg-secondary/50 px-3 py-2"
                        >
                          <div>
                            <span className="text-sm font-medium">{mod.name}</span>
                            {mod.version && (
                              <span className="ml-2 text-xs text-muted-foreground">v{mod.version}</span>
                            )}
                          </div>
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
                onClick={handleAutoPostToggle}
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
                onClick={handleDebugModeToggle}
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
    </div>
  );
}
