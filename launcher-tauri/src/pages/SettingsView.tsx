import { useState, useEffect } from "react";
import { invoke } from "@tauri-apps/api/core";
import { open } from "@tauri-apps/plugin-dialog";
import { join, localDataDir } from "@tauri-apps/api/path";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { User, FolderOpen, RotateCcw, Info, LogOut, LogIn, Store, Wand2, Copy, Loader2 } from "lucide-react";
import { showToast, formatError } from "@/components/Toast";
import { useLauncher, type UserInfo } from "@/state/LauncherContext";

interface GameSearchResult {
  path?: string | null;
  storefront?: string | null;
  detected_but_unavailable?: boolean;
}

const STOREFRONT_OPTIONS = [
  { value: "steam", label: "Steam" },
  { value: "epic", label: "Epic Games" },
  { value: "microsoft_store", label: "Microsoft Store" },
] as const;

export default function SettingsView() {
  const { config, updateConfig, loggedIn, username, avatarUrl, login, logout } =
    useLauncher();
  const [version, setVersion] = useState("");
  const [detectedPath, setDetectedPath] = useState<string | null>(null);
  const [detectedStorefront, setDetectedStorefront] = useState<string | null>(null);
  const [detecting, setDetecting] = useState(false);
  const [signingIn, setSigningIn] = useState(false);

  // Config I/O lives in the shared context — no local read_config /
  // whole-object write_config copies that can go stale and clobber
  // concurrent updates from other pages.
  const gamePath = config?.modded_install_path ?? "";
  const storefront = config?.storefront ?? "";

  useEffect(() => {
    // Runtime version from the Rust side — package.json's build-time
    // version goes stale (historic builds shipped 1.0.x while the release
    // was 1.2.x). Rejection (command not available) falls back to "Unknown".
    invoke<string>("get_version")
      .then((v) => setVersion(v || "Unknown"))
      .catch(() => setVersion("Unknown"));
    invoke<GameSearchResult>("detect_game", {})
      .then((result) => {
        setDetectedPath(result.path ?? null);
        setDetectedStorefront(result.storefront ?? null);
      })
      .catch(() => {
        // Silent: detection is non-critical
      });
  }, []);

  async function browseGamePath() {
    try {
      const selected = await open({
        directory: true,
        multiple: false,
      });
      if (selected) {
        const newPath = Array.isArray(selected) ? selected[0] : selected;
        await updateConfig({ modded_install_path: newPath });
      }
    } catch {
      showToast("Failed to browse", "error");
    }
  }

  async function resetGamePath() {
    try {
      const localData = await localDataDir();
      const defaultPath = await join(localData, "AmongLauncher", "ModdedAmongUs");
      await updateConfig({ modded_install_path: defaultPath });
    } catch {
      showToast("Failed to reset path", "error");
    }
  }

  async function handleLogout() {
    try {
      // Context logout clears credentials in the backend config AND flips
      // app auth state, so App switches straight to the Welcome screen.
      await logout();
      showToast("Logged out", "success");
    } catch (e) {
      showToast(`Failed to logout: ${formatError(e)}`, "error");
    }
  }

  async function signIn() {
    setSigningIn(true);
    try {
      const user = await invoke<UserInfo>("login_discord");
      await login(user);
      showToast("Signed in", "success");
    } catch (e) {
      showToast(formatError(e), "error");
    } finally {
      setSigningIn(false);
    }
  }

  async function setStorefrontValue(value: string) {
    try {
      await updateConfig({ storefront: value });
    } catch {
      showToast("Failed to set storefront", "error");
    }
  }

  function storefrontLabel(value: string | null): string {
    const match = STOREFRONT_OPTIONS.find((o) => o.value === value);
    return match ? match.label : "Original";
  }

  async function autoDetect() {
    setDetecting(true);
    try {
      const result = await invoke<GameSearchResult>("detect_game", {});
      setDetectedPath(result.path ?? null);
      setDetectedStorefront(result.storefront ?? null);
      if (result.storefront) {
        await setStorefrontValue(result.storefront);
        const label = storefrontLabel(result.storefront);
        showToast(`Detected: ${label}`, "success");
      } else {
        showToast("No Among Us installation found", "error");
      }
    } catch {
      showToast("No Among Us installation found", "error");
    } finally {
      setDetecting(false);
    }
  }

  async function copyDetectedPath() {
    if (!detectedPath) return;
    try {
      await navigator.clipboard.writeText(detectedPath);
      showToast("Path copied to clipboard", "success");
    } catch {
      showToast("Failed to copy path", "error");
    }
  }

  return (
    <div className="min-h-full p-6 space-y-6">
      <h1 className="text-3xl font-bold tracking-tight">Settings</h1>

      <div className="grid gap-6 md:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <User className="h-5 w-5" />
              Account
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            {loggedIn ? (
              <>
                <div className="flex items-center gap-4">
                  {avatarUrl ? (
                    <img
                      src={avatarUrl}
                      alt="Avatar"
                      className="h-12 w-12 rounded-full border-2 border-primary"
                    />
                  ) : (
                    <div className="h-12 w-12 rounded-full border border-white/10 bg-white/[0.06] flex items-center justify-center">
                      <User className="h-6 w-6 text-muted-foreground" />
                    </div>
                  )}
                  <div>
                    <p className="font-medium">{username || "Signed in"}</p>
                    <Badge variant="neutral">Discord</Badge>
                  </div>
                </div>
                <Button onClick={() => void handleLogout()} variant="destructive" className="w-full">
                  <LogOut className="h-4 w-4" />
                  Logout
                </Button>
              </>
            ) : (
              <>
                <p className="text-sm text-muted-foreground">Not logged in.</p>
                <Button onClick={() => void signIn()} className="w-full" disabled={signingIn}>
                  {signingIn ? (
                    <Loader2 className="h-4 w-4 animate-spin" />
                  ) : (
                    <LogIn className="h-4 w-4" />
                  )}
                  Sign in
                </Button>
              </>
            )}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <FolderOpen className="h-5 w-5" />
              Storage
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            <div className="space-y-2">
              <label className="text-sm font-medium">{storefrontLabel(detectedStorefront || storefront || null)} Game Path</label>
              <div className="flex gap-2">
                <Input
                  value={detectedPath ?? ""}
                  readOnly
                  placeholder="Not detected — install Among Us via Steam/Epic first"
                  className="flex-1 font-mono text-xs"
                />
                <Button
                  onClick={() => void copyDetectedPath()}
                  variant="ghost"
                  size="icon"
                  disabled={!detectedPath}
                  title="Copy detected path"
                  aria-label="Copy detected path"
                >
                  <Copy className="h-4 w-4" />
                </Button>
              </div>
            </div>
            <div className="space-y-2">
              <label className="text-sm font-medium">Modded Game Path</label>
              <div className="flex gap-2">
                <Input
                  value={gamePath}
                  readOnly
                  placeholder="No path set"
                  className="flex-1 font-mono text-xs"
                />
                <Button
                  onClick={() => void browseGamePath()}
                  variant="outline"
                  size="icon"
                  title="Browse for modded game folder"
                  aria-label="Browse for modded game folder"
                >
                  <FolderOpen className="h-4 w-4" />
                </Button>
                <Button
                  onClick={() => void resetGamePath()}
                  variant="outline"
                  size="icon"
                  title="Reset to default path"
                  aria-label="Reset to default path"
                >
                  <RotateCcw className="h-4 w-4" />
                </Button>
              </div>
            </div>
          </CardContent>
        </Card>
      </div>

      <div className="grid gap-6 md:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Store className="h-5 w-5" />
              Storefront
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            <div className="space-y-2">
              <div className="flex items-center justify-between">
                <label className="text-sm font-medium">Platform</label>
                <Button variant="outline" size="sm" onClick={() => void autoDetect()} disabled={detecting}>
                  {detecting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Wand2 className="h-4 w-4" />}
                  {detecting ? "Detecting..." : "Auto-detect"}
                </Button>
              </div>
              <div className="flex gap-2">
                {STOREFRONT_OPTIONS.map((option) => (
                  <Button
                    key={option.value}
                    variant={storefront === option.value ? "default" : "outline"}
                    size="sm"
                    className="flex-1"
                    onClick={() => void setStorefrontValue(option.value)}
                  >
                    {option.label}
                  </Button>
                ))}
              </div>
            </div>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Info className="h-5 w-5" />
              About
            </CardTitle>
          </CardHeader>
          <CardContent>
            <div className="flex items-center justify-between">
              <span className="text-sm text-muted-foreground">Version</span>
              <Badge variant="muted">{version || "…"}</Badge>
            </div>
          </CardContent>
        </Card>
      </div>
    </div>
  );
}
