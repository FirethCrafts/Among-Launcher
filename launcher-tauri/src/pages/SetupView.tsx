import { useState, useEffect, useRef } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { open } from "@tauri-apps/plugin-dialog";
import { Card, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { showToast, formatError } from "@/components/Toast";
import { useLauncher } from "@/state/LauncherContext";
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
  // Config comes from the shared context — no per-page read_config.
  const { config, refreshConfig } = useLauncher();
  const [gamePath, setGamePath] = useState<string | null>(null);
  const [storefront, setStorefront] = useState("");
  const [detected, setDetected] = useState(false);
  const [bepinexInstalled, setBepinexInstalled] = useState(false);
  const [amongApiInstalled, setAmongApiInstalled] = useState(false);
  const [installProgress, setInstallProgress] = useState<InstallProgress | null>(null);
  const [installing, setInstalling] = useState(false);
  const [loading, setLoading] = useState(true);
  const [success, setSuccess] = useState(false);
  const successToastShown = useRef(false);
  // The install-progress listener is registered once on mount; reading config
  // through this ref keeps it from capturing a stale closure (#8).
  const configRef = useRef(config);

  useEffect(() => {
    configRef.current = config;
  }, [config]);

  // Mount: detect the source game (config is owned by the shared context).
  useEffect(() => {
    let cancelled = false;

    (async () => {
      try {
        const result = await invoke<GameSearchResult>("detect_game", {});
        if (cancelled) return;
        setStorefront(result.storefront || "");
        if (result.path) {
          setGamePath(result.path);
          setDetected(true);
        }
      } catch {
        if (!cancelled) showToast("Failed to detect game", "error");
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();

    const unlistenInstall = listen<InstallProgress>("install-progress", (event) => {
      setInstallProgress(event.payload);
      if (event.payload.stage === "complete") {
        // confirmInstalled reads configRef.current — always fresh, even from
        // this mount-registered listener.
        void confirmInstalled();
      }
    });

    return () => {
      cancelled = true;
      unlistenInstall.then((fn) => fn());
    };
  }, []);

  // Storefront fallback from the shared config (may arrive after detection).
  useEffect(() => {
    const sf = config?.storefront;
    if (sf) setStorefront((prev) => prev || sf);
  }, [config]);

  // Install status for the configured modded path (fresh config value).
  useEffect(() => {
    const target = config?.modded_install_path;
    if (!target) return;
    let cancelled = false;
    (async () => {
      try {
        const status = await invoke<InstallStatus>("get_install_status", {
          gamePath: target,
        });
        if (cancelled) return;
        setBepinexInstalled(status.bepinex_installed);
        setAmongApiInstalled(status.among_api_installed);
        if (status.bepinex_installed && status.among_api_installed) {
          setSuccess(true);
        }
      } catch {
        // Non-fatal: status badges simply stay as-is.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [config?.modded_install_path]);

  function moddedPath(): string | null {
    return configRef.current?.modded_install_path || null;
  }

  async function confirmInstalled() {
    // Pick up any config writes install_game persisted backend-side (it can
    // default+save the modded path) before verifying against it.
    await refreshConfig();
    const target = moddedPath();
    if (!target) {
      setInstalling(false);
      return;
    }
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

      const target = configRef.current?.modded_install_path || dir;
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
      showToast(formatError(e), "error");
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
                    title={gamePath || ""}
                    placeholder="Path to your Among Us installation"
                    className="flex-1"
                  />
                  <Button variant="outline" onClick={() => void handleBrowse()} disabled={installing}>
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
                  onClick={() => void handleInstall()}
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
