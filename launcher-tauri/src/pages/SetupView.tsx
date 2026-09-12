import { useState, useEffect, useRef } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { open } from "@tauri-apps/plugin-dialog";
import { Card, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { showToast } from "@/components/Toast";
import { Gamepad2, FolderOpen, Play, Check, Loader2 } from "lucide-react";

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
  discord_access_token: string;
  username: string;
  avatar_url: string;
  last_seen_version: string;
}

interface InstallProgress {
  stage: string;
  progress: number;
  total: number;
}

interface SetupViewProps {
  onComplete: () => void;
}

function dirOfExe(path: string): string {
  const index = path.search(/[\\/][^\\/]*\.exe$/i);
  return index > 0 ? path.slice(0, index) : path;
}

export default function SetupView({ onComplete }: SetupViewProps) {
  const [gamePath, setGamePath] = useState<string | null>(null);
  const [storefront, setStorefront] = useState("");
  const [detected, setDetected] = useState(false);
  const [bepinexInstalled, setBepinexInstalled] = useState(false);
  const [amongApiInstalled, setAmongApiInstalled] = useState(false);
  const [config, setConfig] = useState<LauncherConfig | null>(null);
  const [installProgress, setInstallProgress] = useState<InstallProgress | null>(null);
  const [installing, setInstalling] = useState(false);
  const [loading, setLoading] = useState(true);
  const [success, setSuccess] = useState(false);
  const successToastShown = useRef(false);

  useEffect(() => {
    let cancelled = false;

    async function init() {
      try {
        const [result, cfg] = await Promise.all([
          invoke<GameSearchResult>("detect_game", {}),
          invoke<LauncherConfig>("read_config"),
        ]);
        if (cancelled) return;
        setConfig(cfg);
        setStorefront(result.storefront || cfg.storefront || "");
        const sourcePath = result.path || null;
        if (sourcePath) {
          setGamePath(sourcePath);
          setDetected(true);
        }
        if (cfg.modded_install_path) {
          const status = await invoke<InstallStatus>("get_install_status", {
            gamePath: cfg.modded_install_path,
          });
          if (cancelled) return;
          setBepinexInstalled(status.bepinex_installed);
          setAmongApiInstalled(status.among_api_installed);
          if (status.bepinex_installed && status.among_api_installed) {
            setSuccess(true);
          }
        }
      } catch {
        if (!cancelled) showToast("Failed to detect game", "error");
      } finally {
        if (!cancelled) setLoading(false);
      }
    }

    init();

    const unlistenInstall = listen<InstallProgress>("install-progress", (event) => {
      setInstallProgress(event.payload);
      if (event.payload.stage === "complete") {
        confirmInstalled();
      }
    });

    return () => {
      cancelled = true;
      unlistenInstall.then((fn) => fn());
    };
  }, []);

  function moddedPath(): string | null {
    return config?.modded_install_path || null;
  }

  async function confirmInstalled() {
    const target = moddedPath();
    if (!target) return;
    try {
      const status = await invoke<InstallStatus>("get_install_status", { gamePath: target });
      setBepinexInstalled(status.bepinex_installed);
      setAmongApiInstalled(status.among_api_installed);
      if (status.bepinex_installed && status.among_api_installed) {
        setSuccess(true);
        if (!successToastShown.current) {
          successToastShown.current = true;
          showToast("Game installed successfully", "success");
        }
      } else {
        showToast("Install finished but files are missing", "error");
      }
    } catch {
      showToast("Failed to verify install", "error");
    } finally {
      setInstalling(false);
    }
  }

  async function handleBrowse() {
    try {
      const selected = await open({
        directory: false,
        multiple: false,
        filters: [{ name: "Executables", extensions: ["exe"] }],
      });
      if (!selected) return;
      const filePath = Array.isArray(selected) ? selected[0] : selected;
      const dir = dirOfExe(filePath);
      setGamePath(dir);
      setDetected(true);
      setSuccess(false);

      let cfg = config;
      if (!cfg) {
        try {
          cfg = await invoke<LauncherConfig>("read_config");
          setConfig(cfg);
        } catch {
          // config unavailable; fall back to the browsed path
        }
      }
      const target = cfg?.modded_install_path || dir;
      const status = await invoke<InstallStatus>("get_install_status", { gamePath: target });
      setBepinexInstalled(status.bepinex_installed);
      setAmongApiInstalled(status.among_api_installed);
      if (status.bepinex_installed && status.among_api_installed) {
        setSuccess(true);
      }
    } catch {
      showToast("Failed to locate game", "error");
    }
  }

  async function handleInstall() {
    if (!gamePath) return;
    setInstalling(true);
    setInstallProgress(null);
    setSuccess(false);
    try {
      await invoke("install_game", { gamePath, storefront });
      await confirmInstalled();
    } catch (e) {
      showToast(String(e), "error");
      setInstalling(false);
    }
  }

  return (
    <div className="h-full flex items-center justify-center bg-background">
      <Card className="w-full max-w-md">
        <CardContent className="flex flex-col items-center gap-4 p-8 text-center">
          <div className="flex h-12 w-12 items-center justify-center rounded-xl bg-primary">
            <Gamepad2 className="h-6 w-6 text-primary-foreground" />
          </div>
          <h1 className="text-3xl font-bold">Set up your game</h1>
          <p className="text-muted-foreground text-sm">
            The launcher copies your Among Us installation into a modded folder and installs
            BepInEx so you can play modded.
          </p>

          {loading ? (
            <div className="w-full space-y-3">
              <Skeleton className="h-9 w-full" />
              <Skeleton className="h-9 w-full" />
            </div>
          ) : (
            <div className="w-full space-y-4 text-left">
              {!detected && (
                <p className="text-sm text-muted-foreground">
                  We couldn't find Among Us — click Browse to locate it manually.
                </p>
              )}

              <div className="space-y-2">
                <div className="flex items-center gap-2">
                  <Input
                    value={gamePath || ""}
                    readOnly
                    placeholder="Path to your Among Us installation"
                    className="flex-1"
                  />
                  <Button variant="outline" onClick={handleBrowse} disabled={installing}>
                    <FolderOpen className="h-4 w-4" />
                    Browse
                  </Button>
                </div>
                {storefront && (
                  <div className="flex items-center justify-between text-sm">
                    <span className="text-muted-foreground">Storefront</span>
                    <span className="font-medium capitalize">{storefront.replace("_", " ")}</span>
                  </div>
                )}
                {gamePath && (
                  <div className="flex items-center justify-between text-sm">
                    <span className="text-muted-foreground">Status</span>
                    <div className="flex items-center gap-2">
                      <Badge variant={bepinexInstalled ? "neutral" : "muted"}>BepInEx</Badge>
                      <Badge variant={amongApiInstalled ? "neutral" : "muted"}>AmongApi</Badge>
                    </div>
                  </div>
                )}
              </div>

              {installProgress && !success && (
                <div className="space-y-2">
                  <div className="flex items-center justify-between text-sm">
                    <span className="font-medium capitalize">
                      {installProgress.stage === "complete"
                        ? "Install Complete"
                        : installProgress.stage}
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
              )}

              {success ? (
                <div className="space-y-2">
                  <p className="flex items-center justify-center gap-2 text-sm font-medium text-emerald-600">
                    <Check className="h-4 w-4" />
                    Your game is set up and ready to play.
                  </p>
                  <Button onClick={onComplete} size="lg" className="w-full">
                    Continue
                  </Button>
                </div>
              ) : (
                <Button
                  onClick={handleInstall}
                  disabled={!gamePath || installing}
                  size="lg"
                  className="w-full"
                >
                  {installing ? (
                    <Loader2 className="h-4 w-4 animate-spin" />
                  ) : (
                    <Play className="h-4 w-4" />
                  )}
                  {installing ? "Installing..." : "Install"}
                </Button>
              )}
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  );
}