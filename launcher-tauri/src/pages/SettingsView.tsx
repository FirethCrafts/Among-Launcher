import { useState, useEffect } from "react";
import { invoke } from "@tauri-apps/api/core";
import { open } from "@tauri-apps/plugin-dialog";
import { join, localDataDir } from "@tauri-apps/api/path";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { User, FolderOpen, RotateCcw, Info, LogOut, Store, Wand2, Copy, Loader2 } from "lucide-react";
import { showToast } from "@/components/Toast";
import pkg from "../../package.json";

interface GameSearchResult {
  path?: string | null;
  storefront?: string | null;
  detected_but_unavailable?: boolean;
}

interface LauncherConfig {
  storefront: string | null;
  modded_install_path: string;
  avatar_url: string;
  username: string;
  discord_access_token: string;
  profiles: Array<{ name: string; mods: Array<{ name: string }> }>;
  library: Array<{ path: string; storefront: string | null }>;
  debug_mode: boolean;
  auto_post_lobby: boolean;
  last_seen_version: string;
}

interface AccountInfo {
  username: string;
  avatarUrl: string | null;
}

const STOREFRONT_OPTIONS = [
  { value: "steam", label: "Steam" },
  { value: "epic", label: "Epic Games" },
  { value: "microsoft_store", label: "Microsoft Store" },
] as const;

export default function SettingsView() {
  const [account, setAccount] = useState<AccountInfo | null>(null);
  const [gamePath, setGamePath] = useState("");
  const [version, setVersion] = useState("");
  const [storefront, setStorefront] = useState<string>("");
  const [fullConfig, setFullConfig] = useState<LauncherConfig | null>(null);
  const [detectedPath, setDetectedPath] = useState<string | null>(null);
  const [detectedStorefront, setDetectedStorefront] = useState<string | null>(null);
  const [detecting, setDetecting] = useState(false);

  useEffect(() => {
    loadSettings();
  }, []);

  async function loadSettings() {
    try {
      const config = await invoke<LauncherConfig>("read_config");
      setFullConfig(config);

      if (config.username) {
        setAccount({
          username: config.username,
          avatarUrl: config.avatar_url || null,
        });
      } else {
        setAccount(null);
      }

      setGamePath(config.modded_install_path || "");
      setStorefront(config.storefront || "");
      setVersion(pkg.version || "Unknown");

      try {
        const result = await invoke<GameSearchResult>("detect_game", {});
        setDetectedPath(result.path ?? null);
        setDetectedStorefront(result.storefront ?? null);
      } catch {
        // Silent: detection is non-critical
      }
    } catch (e) {
      showToast("Failed to load settings", "error");
    }
  }

  async function browseGamePath() {
    try {
      const selected = await open({
        directory: true,
        multiple: false,
      });
      if (selected) {
        const newPath = Array.isArray(selected) ? selected[0] : selected;
        setGamePath(newPath);
        if (fullConfig) {
          const updatedConfig = { ...fullConfig, modded_install_path: newPath };
          await invoke("write_config", { newConfig: updatedConfig });
          setFullConfig(updatedConfig);
        }
      }
    } catch (e) {
      showToast("Failed to browse", "error");
    }
  }

  async function resetGamePath() {
    try {
      const localData = await localDataDir();
      const defaultPath = await join(localData, "AmongLauncher", "ModdedAmongUs");
      setGamePath(defaultPath);
      if (fullConfig) {
        const updatedConfig = { ...fullConfig, modded_install_path: defaultPath };
        await invoke("write_config", { newConfig: updatedConfig });
        setFullConfig(updatedConfig);
      }
    } catch (e) {
      showToast("Failed to reset path", "error");
    }
  }

  async function logout() {
    try {
      if (fullConfig) {
        const updatedConfig = {
          ...fullConfig,
          discord_access_token: "",
          username: "",
          avatar_url: "",
        };
        await invoke("write_config", { newConfig: updatedConfig });
        setFullConfig(updatedConfig);
        setAccount(null);
        showToast("Logged out", "success");
      }
    } catch (e) {
      showToast("Failed to logout", "error");
    }
  }

  async function setStorefrontValue(value: string) {
    try {
      setStorefront(value);
      if (fullConfig) {
        const updatedConfig = { ...fullConfig, storefront: value };
        await invoke("write_config", { newConfig: updatedConfig });
        setFullConfig(updatedConfig);
      }
    } catch (e) {
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
    } catch (e) {
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
            {account ? (
              <>
                <div className="flex items-center gap-4">
                  {account.avatarUrl ? (
                    <img
                      src={account.avatarUrl}
                      alt="Avatar"
                      className="h-12 w-12 rounded-full border-2 border-primary"
                    />
                  ) : (
                    <div className="h-12 w-12 rounded-full bg-secondary flex items-center justify-center">
                      <User className="h-6 w-6 text-muted-foreground" />
                    </div>
                  )}
                  <div>
                    <p className="font-medium">{account.username}</p>
                    <Badge variant="neutral">Discord</Badge>
                  </div>
                </div>
                <Button onClick={logout} variant="destructive" className="w-full">
                  <LogOut className="h-4 w-4" />
                  Logout
                </Button>
              </>
            ) : (
              <p className="text-sm text-muted-foreground">Not logged in.</p>
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
                <Button onClick={copyDetectedPath} variant="ghost" size="icon" disabled={!detectedPath}>
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
                <Button onClick={browseGamePath} variant="outline" size="icon">
                  <FolderOpen className="h-4 w-4" />
                </Button>
                <Button onClick={resetGamePath} variant="outline" size="icon">
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
                <Button variant="outline" size="sm" onClick={autoDetect} disabled={detecting}>
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
                    onClick={() => setStorefrontValue(option.value)}
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
              <Badge variant="muted">{version || "Unknown"}</Badge>
            </div>
          </CardContent>
        </Card>
      </div>
    </div>
  );
}
