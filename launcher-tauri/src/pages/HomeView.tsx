import { useState, useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { open } from "@tauri-apps/plugin-dialog";
import { Card, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge, type BadgeDotColor } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { EmptyState } from "@/components/ui/empty-state";
import { Tooltip } from "@/components/ui/tooltip";
import { PageHeader } from "@/components/ui/page-header";
import { SectionHeader } from "@/components/ui/section-header";
import { ListRow } from "@/components/ui/list-row";
import { showToast, formatError } from "@/components/Toast";
import { ConfirmModal } from "@/components/Modal";
import { LibraryPickerModal } from "@/components/LibraryPickerModal";
import { useLauncher } from "@/state/LauncherContext";
import type { ModStatus } from "@/App";
import { Play, Square, Gamepad2, FolderOpen, Package, Folder, Trash2, Archive, Library, RefreshCw } from "lucide-react";

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

interface HomeViewProps {
  /** Latest AmongApi status owned by App (null = not checked yet). */
  modStatus?: ModStatus | null;
  /** Opens App's forced AmongApi update prompt. */
  onRequireModUpdate?: () => void;
}

/** Status-strip tone → dot colour (Badge variants reuse the same palette). */
const TONE_DOT: Record<"neutral" | "success" | "warning" | "danger", BadgeDotColor> = {
  neutral: "info",
  success: "success",
  warning: "warning",
  danger: "danger",
};

function formatBytes(bytes: number): string {
  if (bytes === 0) return "0 B";
  const k = 1024;
  const sizes = ["B", "KB", "MB", "GB"];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + " " + sizes[i];
}

export default function HomeView({
  modStatus = null,
  onRequireModUpdate,
}: HomeViewProps) {
  const navigate = useNavigate();
  const { config } = useLauncher();
  const [gamePath, setGamePath] = useState<string | null>(null);
  const [storefront, setStorefront] = useState<string | null>(null);
  const [detected, setDetected] = useState<GameSearchResult | null>(null);
  const [isRunning, setIsRunning] = useState(false);
  const [bepinexInstalled, setBepinexInstalled] = useState(false);
  const [amongApiInstalled, setAmongApiInstalled] = useState(false);
  const [mods, setMods] = useState<ModEntry[]>([]);
  const [installProgress, setInstallProgress] = useState<InstallProgress | null>(null);
  const [loading, setLoading] = useState(true);
  const [modsLoading, setModsLoading] = useState(false);
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
    setModsLoading(true);
    try {
      const installedMods = await invoke<ModEntry[]>("get_mod_list", { gamePath });
      setMods(installedMods);
    } catch {
      showToast("Failed to get mods", "error");
    } finally {
      setModsLoading(false);
    }
  }

  async function browseFiles(path: string) {
    try {
      await invoke("browse_files", { path });
    } catch {
      showToast("Failed to open file browser", "error");
    }
  }

  async function launchGame() {
    if (!gamePath) return;
    try {
      await invoke("launch_game", { gamePath });
      setIsRunning(true);
    } catch (e) {
      // `launch_game` now rejects when the mod isn't current — surface the
      // backend's reason rather than a generic failure.
      const detail = formatError(e);
      showToast(
        detail ? `Failed to launch game: ${detail}` : "Failed to launch game",
        "error"
      );
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
  // `unknown` (or not-yet-checked) must NOT block play — only a definitively
  // non-current mod does. `isReady` stays the presence/setup gate.
  const modReady =
    !modStatus ||
    modStatus.status === "current" ||
    modStatus.status === "unknown";
  const canPlay = isReady && modReady;
  const libraryCount = config?.library.length ?? 0;

  // AmongApi status-strip chip: prefer the richer version status over the
  // presence flag. Until the check resolves (`modStatus === null`) we say
  // "checking" rather than claiming "up to date".
  let amongApiValue = amongApiInstalled ? "Installed" : "Missing";
  let amongApiHint = amongApiInstalled ? "Checking version…" : "Run setup to install";
  let amongApiTone: "neutral" | "success" | "danger" | "warning" = amongApiInstalled
    ? "success"
    : "danger";
  if (modStatus) {
    switch (modStatus.status) {
      case "current":
        amongApiValue = modStatus.installedVersion
          ? `v${modStatus.installedVersion}`
          : "Up to date";
        amongApiHint = "Mod up to date";
        amongApiTone = "success";
        break;
      case "outdated":
        amongApiValue = "Outdated";
        amongApiHint =
          modStatus.installedVersion && modStatus.latestVersion
            ? `v${modStatus.installedVersion} → v${modStatus.latestVersion}`
            : "Update available";
        amongApiTone = "warning";
        break;
      case "incompatible":
        amongApiValue = "Incompatible";
        amongApiHint = "Update required";
        amongApiTone = "danger";
        break;
      case "missing":
        amongApiValue = "Missing";
        amongApiHint = "Run setup to install";
        amongApiTone = "danger";
        break;
      case "unknown":
        amongApiValue = amongApiInstalled ? "Installed" : "Unknown";
        amongApiHint = "Couldn't verify version";
        amongApiTone = "neutral";
        break;
    }
  }

  const statusPill = canPlay ? (
    <Badge variant="success" showDot dotColor="success">
      Ready
    </Badge>
  ) : (
    <Badge variant="warning" showDot dotColor="warning">
      {isReady ? "Update Required" : "Incomplete"}
    </Badge>
  );

  return (
    <div className="min-h-full space-y-6 pb-6">
      <PageHeader title="Home" actions={statusPill} />

      <div className="space-y-6 px-6">
      {loading ? (
        <div className="space-y-6">
          <div className="space-y-3">
            <Skeleton className="h-8 w-56" />
            <Skeleton className="h-4 w-72" />
            <Skeleton className="h-10 w-36" />
          </div>
          <div className="flex flex-wrap gap-2">
            <Skeleton className="h-6 w-24 rounded-pill" />
            <Skeleton className="h-6 w-24 rounded-pill" />
            <Skeleton className="h-6 w-28 rounded-pill" />
          </div>
          <div className="space-y-2">
            <Skeleton className="h-12 w-full" />
            <Skeleton className="h-12 w-full" />
          </div>
        </div>
      ) : (
        <>
          {/* HERO — no card chrome; Play is the dominant primary action. */}
          <section className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
            <div className="space-y-1">
              <h2 className="text-display font-bold tracking-tight">
                {canPlay
                  ? "Game Ready"
                  : isReady
                    ? "Update Required"
                    : "Setup Needed"}
              </h2>
              <p className="text-13 text-muted-foreground">
                {canPlay
                  ? "Everything is set up. Jump into a lobby."
                  : isReady
                    ? "AmongApi needs updating before you can play."
                    : "Finish installing Among Us + BepInEx to play."}
              </p>
            </div>
            <div className="shrink-0">
              {!isReady ? (
                <Button onClick={() => navigate("/setup")} variant="primary" size="lg">
                  <Gamepad2 className="h-5 w-5" />
                  Set Up Game
                </Button>
              ) : isRunning ? (
                <Button onClick={() => setConfirmStop(true)} variant="destructive" size="lg">
                  <Square className="h-5 w-5" />
                  Stop
                </Button>
              ) : !modReady ? (
                <div className="flex flex-col items-stretch gap-2 sm:items-end">
                  <Tooltip content="AmongApi needs updating">
                    <span className="inline-flex">
                      <Button variant="primary" size="lg" disabled>
                        <Play className="h-5 w-5" />
                        Play
                      </Button>
                    </span>
                  </Tooltip>
                  <Button
                    variant="outline"
                    size="sm"
                    onClick={() => onRequireModUpdate?.()}
                  >
                    <RefreshCw className="h-3.5 w-3.5" />
                    Update AmongApi
                  </Button>
                </div>
              ) : (
                <Button onClick={() => void launchGame()} variant="primary" size="lg">
                  <Play className="h-5 w-5" />
                  Play
                </Button>
              )}
            </div>
          </section>

          {/* STATUS STRIP — compact chips, not cards. */}
          <section className="flex flex-wrap items-center gap-2" aria-label="Install status">
            <Badge
              variant={storefront ? "neutral" : "muted"}
              showDot={!!storefront}
              dotColor="info"
              className="capitalize"
              title={storefront ? "Source install" : "Browse to locate the game"}
            >
              {storefront ? storefront.replace("_", " ") : "No storefront"}
            </Badge>
            <Badge
              variant={bepinexInstalled ? "success" : "danger"}
              showDot
              dotColor={bepinexInstalled ? "success" : "danger"}
              title={bepinexInstalled ? "Mod loader ready" : "Run setup to install"}
            >
              BepInEx {bepinexInstalled ? "✓" : "Missing"}
            </Badge>
            <Badge
              variant={amongApiTone}
              showDot
              dotColor={TONE_DOT[amongApiTone]}
              title={amongApiHint}
            >
              AmongApi {amongApiValue}
            </Badge>
          </section>

          {/* INSTALLED MODS — dense full-width rows. */}
          <section className="space-y-3">
            <SectionHeader
              title="Installed Mods"
              count={mods.length}
              actions={
                <>
                  <Button onClick={() => void handleImportMod()} variant="outline" size="sm">
                    <FolderOpen className="h-4 w-4" />
                    Import Mod
                  </Button>
                  <Button
                    onClick={() => setLibraryPickerOpen(true)}
                    variant="outline"
                    size="sm"
                    disabled={libraryCount === 0}
                    title={libraryCount === 0 ? "Library is empty" : "Install a mod from your library"}
                  >
                    <Library className="h-4 w-4" />
                    From Library
                  </Button>
                </>
              }
            />
            {modsLoading ? (
              <div className="space-y-2">
                <Skeleton className="h-12 w-full" />
                <Skeleton className="h-12 w-full" />
                <Skeleton className="h-12 w-full" />
              </div>
            ) : mods.length === 0 ? (
              <EmptyState
                icon={<Package className="h-4 w-4" />}
                title="No mods installed"
                description="Import a .dll or install one from your library to get started."
                className="py-4"
              />
            ) : (
              <ul className="space-y-1">
                {mods.map((mod) => (
                  <ListRow
                    key={mod.filename}
                    leading={<Package className="h-4 w-4" />}
                    title={mod.name}
                    meta={`${mod.version ? `v${mod.version} · ` : ""}${formatBytes(mod.size)}`}
                    actions={
                      <>
                        <Button
                          variant="ghost"
                          size="icon"
                          onClick={() => void copyModToLibrary(mod)}
                          aria-label={`Save ${mod.name} to library`}
                          title="Save to library"
                        >
                          <Archive className="h-3.5 w-3.5" />
                        </Button>
                        <Button
                          variant="ghost"
                          size="icon"
                          onClick={() => void browseFiles(mod.path)}
                          aria-label={`Open ${mod.name} folder`}
                          title="Open folder"
                        >
                          <Folder className="h-3.5 w-3.5" />
                        </Button>
                        <Button
                          variant="ghost"
                          size="icon"
                          onClick={() => setRemoveTarget(mod)}
                          aria-label={`Remove ${mod.name}`}
                          title="Remove mod"
                        >
                          <Trash2 className="h-3.5 w-3.5" />
                        </Button>
                      </>
                    }
                  />
                ))}
              </ul>
            )}
            <LibraryPickerModal
              isOpen={libraryPickerOpen}
              onClose={() => setLibraryPickerOpen(false)}
              gamePath={gamePath}
              onInstalled={loadMods}
            />
          </section>

          {installProgress && (
            <Card className="shadow-card">
              <CardContent className="pt-6">
                <div className="space-y-2">
                  <div className="flex items-center justify-between text-13">
                    <span className="font-medium capitalize">
                      {installProgress.stage === "complete" ? "Install Complete" : installProgress.stage}
                    </span>
                    <span className="text-muted-foreground tabular-nums">
                      {installProgress.total > 0
                        ? `${Math.round((installProgress.progress / installProgress.total) * 100)}%`
                        : "Preparing..."}
                    </span>
                  </div>
                  <div className="h-2 w-full overflow-hidden rounded-pill bg-surface-2">
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
    </div>
  );
}
