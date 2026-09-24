import { useState, useEffect } from "react";
import { invoke } from "@tauri-apps/api/core";
import { open } from "@tauri-apps/plugin-dialog";
import { join, localDataDir } from "@tauri-apps/api/path";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Select } from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Tooltip } from "@/components/ui/tooltip";
import { PageHeader } from "@/components/ui/page-header";
import { SettingsRow } from "@/components/ui/settings-row";
import { User, FolderOpen, RotateCcw, Info, LogOut, LogIn, Store, Wand2, Copy, Loader2, SlidersHorizontal } from "lucide-react";
import { showToast, formatError } from "@/components/Toast";
import { useLauncher, type UserInfo, type LauncherConfig } from "@/state/LauncherContext";

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

/** Bordered, compact container for a `divide-y` list of SettingsRows. */
const ROW_LIST_CLASS = "rounded-card border border-border bg-surface divide-y divide-border px-4";

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

  async function handleToggle(field: "auto_post_lobby" | "debug_mode") {
    // Guard BEFORE any optimistic flip: without config there is nothing to
    // merge into or persist, and flipping first shows a phantom toggle.
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

  const accountIdentity = (
    <div className="flex items-center gap-3">
      {avatarUrl ? (
        <img
          src={avatarUrl}
          alt="Avatar"
          className="h-10 w-10 shrink-0 rounded-full border border-border"
        />
      ) : (
        <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-full border border-border bg-surface-2">
          <User className="h-5 w-5 text-muted-foreground" />
        </div>
      )}
      <div className="min-w-0">
        <div className="truncate font-medium text-foreground">
          {username || "Signed in"}
        </div>
        <div className="mt-1 flex items-center gap-2">
          <Badge variant="success" showDot dotColor="success">
            Connected
          </Badge>
          <Badge variant="neutral">Discord</Badge>
        </div>
      </div>
    </div>
  );

  return (
    <div className="min-h-full space-y-6 pb-6">
      <PageHeader
        title="Settings"
        description="Manage your account, install locations, and launcher preferences."
      />

      <div className="space-y-6 px-6">
      <Tabs value={activeTab} onValueChange={setActiveTab}>
        <TabsList className="flex-wrap">
          <TabsTrigger value="account" className="h-8 gap-2">
            <User className="h-4 w-4" />
            Account
          </TabsTrigger>
          <TabsTrigger value="general" className="h-8 gap-2">
            <SlidersHorizontal className="h-4 w-4" />
            General
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
          <div className={ROW_LIST_CLASS}>
            {loggedIn ? (
              <SettingsRow
                className="py-4"
                label={accountIdentity}
                control={
                  <Button
                    onClick={() => void handleLogout()}
                    variant="destructive"
                    size="sm"
                  >
                    <LogOut className="h-4 w-4" />
                    Logout
                  </Button>
                }
              />
            ) : (
              <SettingsRow
                className="py-4"
                label={
                  <div className="flex items-center gap-3">
                    <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-full border border-border bg-surface-2">
                      <User className="h-5 w-5 text-muted-foreground" />
                    </div>
                    <div>
                      <div className="font-medium text-foreground">Not logged in</div>
                      <div className="text-13 text-muted-foreground">
                        Sign in to host and join lobbies.
                      </div>
                    </div>
                  </div>
                }
                control={
                  <Button
                    onClick={() => void signIn()}
                    variant="primary"
                    size="sm"
                    disabled={signingIn}
                  >
                    {signingIn ? (
                      <Loader2 className="h-4 w-4 animate-spin" />
                    ) : (
                      <LogIn className="h-4 w-4" />
                    )}
                    Sign in
                  </Button>
                }
              />
            )}
          </div>
        </TabsContent>

        <TabsContent value="general">
          <div className={ROW_LIST_CLASS}>
            <SettingsRow
              id="settings-auto-post-lobby"
              label="Auto-post game data"
              description="Automatically post your hosted lobby to the public lobby list."
              control={
                <Switch
                  checked={config?.auto_post_lobby ?? false}
                  disabled={!config}
                  onCheckedChange={() => void handleToggle("auto_post_lobby")}
                />
              }
            />
            <SettingsRow
              id="settings-debug-mode"
              label="Debug mode"
              description="Enable verbose diagnostics in the launcher log."
              control={
                <Switch
                  checked={config?.debug_mode ?? false}
                  disabled={!config}
                  onCheckedChange={() => void handleToggle("debug_mode")}
                />
              }
            />
          </div>
        </TabsContent>

        <TabsContent value="storage">
          <div className={ROW_LIST_CLASS}>
            <SettingsRow
              className="py-4"
              label={`${storefrontLabel(detectedStorefront || storefront || null)} Game Path`}
              description={
                detectedPath ? (
                  <span className="block truncate font-mono text-13">{detectedPath}</span>
                ) : (
                  "Not detected — install Among Us via Steam/Epic first"
                )
              }
              control={
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
              }
            />
            <SettingsRow
              className="py-4"
              label="Modded Game Path"
              description={
                gamePath ? (
                  <span className="block truncate font-mono text-13">{gamePath}</span>
                ) : (
                  "No path set"
                )
              }
              control={
                <>
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
                </>
              }
            />
          </div>
        </TabsContent>

        <TabsContent value="storefront">
          <div className={ROW_LIST_CLASS}>
            <SettingsRow
              label="Platform"
              description="Which store your Among Us installation comes from."
              control={
                <div className="flex items-center gap-2">
                  <Select
                    wrapperClassName="w-40"
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
                  <Button
                    variant="outline"
                    size="sm"
                    onClick={() => void autoDetect()}
                    disabled={detecting}
                  >
                    {detecting ? (
                      <Loader2 className="h-4 w-4 animate-spin" />
                    ) : (
                      <Wand2 className="h-4 w-4" />
                    )}
                    {detecting ? "Detecting..." : "Auto-detect"}
                  </Button>
                </div>
              }
            />
          </div>
        </TabsContent>

        <TabsContent value="about">
          <div className={ROW_LIST_CLASS}>
            <SettingsRow
              label="Version"
              control={<Badge variant="neutral">{version || "…"}</Badge>}
            />
          </div>
        </TabsContent>
      </Tabs>
      </div>
    </div>
  );
}
