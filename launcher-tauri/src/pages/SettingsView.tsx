import { useState, useEffect } from "react";
import { invoke } from "@tauri-apps/api/core";
import { open } from "@tauri-apps/plugin-dialog";
import { join, localDataDir } from "@tauri-apps/api/path";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { Select } from "@/components/ui/select";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Tooltip } from "@/components/ui/tooltip";
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
  const [activeTab, setActiveTab] = useState("account");

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
      // Explicit user action — bypass the 24h detection cache (incl. cached
      // negatives) so a freshly installed game is found.
      const result = await invoke<GameSearchResult>("detect_game", { force: true });
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
    <div className="min-h-full p-6">
      <header className="mb-6 space-y-1">
        <h1 className="text-display font-bold tracking-tight">Settings</h1>
        <p className="text-sm text-muted-foreground">
          Manage your account, install locations, and launcher preferences.
        </p>
      </header>

      <Tabs value={activeTab} onValueChange={setActiveTab}>
        <TabsList className="flex-wrap">
          <TabsTrigger value="account" className="h-8 gap-2">
            <User className="h-4 w-4" />
            Account
          </TabsTrigger>
          <TabsTrigger value="storage" className="h-8 gap-2">
            <FolderOpen className="h-4 w-4" />
            Storage
          </TabsTrigger>
          <TabsTrigger value="storefront" className="h-8 gap-2">
            <Store className="h-4 w-4" />
            Storefront
          </TabsTrigger>
          <TabsTrigger value="about" className="h-8 gap-2">
            <Info className="h-4 w-4" />
            About
          </TabsTrigger>
        </TabsList>

        <TabsContent value="account">
          <Card>
            <CardHeader>
              <CardTitle>Account</CardTitle>
              <CardDescription>Your Discord identity used for lobbies and friends.</CardDescription>
            </CardHeader>
            <CardContent className="space-y-4">
              {loggedIn ? (
                <>
                  <div className="flex items-center gap-4">
                    {avatarUrl ? (
                      <img
                        src={avatarUrl}
                        alt="Avatar"
                        className="h-12 w-12 shrink-0 rounded-full border-2 border-primary"
                      />
                    ) : (
                      <div className="flex h-12 w-12 shrink-0 items-center justify-center rounded-full border border-border bg-surface-2">
                        <User className="h-6 w-6 text-muted-foreground" />
                      </div>
                    )}
                    <div className="min-w-0">
                      <p className="truncate font-medium text-foreground">{username || "Signed in"}</p>
                      <div className="mt-1 flex items-center gap-2">
                        <Badge variant="success" showDot dotColor="success">
                          Connected
                        </Badge>
                        <Badge variant="neutral">Discord</Badge>
                      </div>
                    </div>
                  </div>
                  <Button onClick={() => void handleLogout()} variant="destructive" className="w-full">
                    <LogOut className="h-4 w-4" />
                    Logout
                  </Button>
                </>
              ) : (
                <>
                  <div className="flex items-center gap-4">
                    <div className="flex h-12 w-12 shrink-0 items-center justify-center rounded-full border border-border bg-surface-2">
                      <User className="h-6 w-6 text-muted-foreground" />
                    </div>
                    <div>
                      <p className="font-medium text-foreground">Not logged in</p>
                      <p className="text-sm text-muted-foreground">Sign in to host and join lobbies.</p>
                    </div>
                  </div>
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
        </TabsContent>

        <TabsContent value="storage">
          <Card>
            <CardHeader>
              <CardTitle>Storage</CardTitle>
              <CardDescription>Where the launcher reads and writes Among Us game files.</CardDescription>
            </CardHeader>
            <CardContent className="space-y-5">
              <div className="space-y-2">
                <label className="text-sm font-medium">
                  {storefrontLabel(detectedStorefront || storefront || null)} Game Path
                </label>
                <div className="flex gap-2">
                  <Input
                    value={detectedPath ?? ""}
                    readOnly
                    placeholder="Not detected — install Among Us via Steam/Epic first"
                    className="flex-1 truncate font-mono text-13"
                  />
                  <Tooltip content="Copy detected path">
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
                  </Tooltip>
                </div>
              </div>
              <div className="space-y-2">
                <label className="text-sm font-medium">Modded Game Path</label>
                <div className="flex gap-2">
                  <Input
                    value={gamePath}
                    readOnly
                    placeholder="No path set"
                    className="flex-1 truncate font-mono text-13"
                  />
                  <Tooltip content="Browse for modded game folder">
                    <Button
                      onClick={() => void browseGamePath()}
                      variant="outline"
                      size="icon"
                      title="Browse for modded game folder"
                      aria-label="Browse for modded game folder"
                    >
                      <FolderOpen className="h-4 w-4" />
                    </Button>
                  </Tooltip>
                  <Tooltip content="Reset to default path">
                    <Button
                      onClick={() => void resetGamePath()}
                      variant="danger"
                      size="icon"
                      title="Reset to default path"
                      aria-label="Reset to default path"
                    >
                      <RotateCcw className="h-4 w-4" />
                    </Button>
                  </Tooltip>
                </div>
              </div>
            </CardContent>
          </Card>
        </TabsContent>

        <TabsContent value="storefront">
          <Card>
            <CardHeader>
              <CardTitle>Storefront</CardTitle>
              <CardDescription>Which store your Among Us installation comes from.</CardDescription>
            </CardHeader>
            <CardContent className="space-y-4">
              <div className="space-y-2">
                <div className="flex items-center justify-between gap-2">
                  <label className="text-sm font-medium">Platform</label>
                  <Button variant="outline" size="sm" onClick={() => void autoDetect()} disabled={detecting}>
                    {detecting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Wand2 className="h-4 w-4" />}
                    {detecting ? "Detecting..." : "Auto-detect"}
                  </Button>
                </div>
                <Select
                  value={storefront}
                  onChange={(e) => void setStorefrontValue(e.target.value)}
                >
                  {!STOREFRONT_OPTIONS.some((o) => o.value === storefront) && (
                    <option value="" disabled>
                      Original
                    </option>
                  )}
                  {STOREFRONT_OPTIONS.map((option) => (
                    <option key={option.value} value={option.value}>
                      {option.label}
                    </option>
                  ))}
                </Select>
              </div>
            </CardContent>
          </Card>
        </TabsContent>

        <TabsContent value="about">
          <Card>
            <CardHeader>
              <CardTitle>About</CardTitle>
              <CardDescription>Build and update information.</CardDescription>
            </CardHeader>
            <CardContent className="space-y-4">
              <div className="flex items-center justify-between">
                <span className="text-sm text-muted-foreground">Version</span>
                <Badge variant="neutral">{version || "…"}</Badge>
              </div>
            </CardContent>
          </Card>
        </TabsContent>
      </Tabs>
    </div>
  );
}
