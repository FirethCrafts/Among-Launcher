import { useState, useEffect } from "react";
import { invoke } from "@tauri-apps/api/core";
import { open } from "@tauri-apps/plugin-dialog";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { CardContainer, CardBody, CardItem } from "@/components/ui/3d-card";
import { User, FolderOpen, RotateCcw, Info, LogOut, Store } from "lucide-react";
import pkg from "../../package.json";

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
    } catch (e) {
      console.error("Failed to load settings:", e);
    }
  }

  async function browseGamePath() {
    try {
      const selected = await open({
        directory: false,
        filters: [{ name: "Executables", extensions: ["exe"] }],
      });
      if (selected) {
        const newPath = selected as string;
        setGamePath(newPath);
        if (fullConfig) {
          const updatedConfig = { ...fullConfig, modded_install_path: newPath };
          await invoke("write_config", { newConfig: updatedConfig });
          setFullConfig(updatedConfig);
        }
      }
    } catch (e) {
      console.error("Failed to browse:", e);
    }
  }

  async function resetGamePath() {
    try {
      if (fullConfig) {
        const defaultPath = "";
        setGamePath(defaultPath);
        const updatedConfig = { ...fullConfig, modded_install_path: defaultPath };
        await invoke("write_config", { newConfig: updatedConfig });
        setFullConfig(updatedConfig);
      }
    } catch (e) {
      console.error("Failed to reset path:", e);
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
      }
    } catch (e) {
      console.error("Failed to logout:", e);
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
      console.error("Failed to set storefront:", e);
    }
  }

  return (
    <div className="min-h-full bg-grid p-6 space-y-6">
      <h1 className="font-display text-3xl font-bold text-primary">Settings</h1>

      <div className="grid gap-6 md:grid-cols-2">
        <CardContainer className="w-full">
          <CardBody>
            <CardItem translateZ={20}>
              <Card className="w-full glow-emerald">
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
                          <Badge variant="secondary">Discord</Badge>
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
            </CardItem>
          </CardBody>
        </CardContainer>

        <CardContainer className="w-full">
          <CardBody>
            <CardItem translateZ={20}>
              <Card className="w-full glow-amber">
                <CardHeader>
                  <CardTitle className="flex items-center gap-2">
                    <FolderOpen className="h-5 w-5" />
                    Storage
                  </CardTitle>
                </CardHeader>
                <CardContent className="space-y-4">
                  <div className="space-y-2">
                    <label className="text-sm font-medium">Game Path</label>
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
            </CardItem>
          </CardBody>
        </CardContainer>
      </div>

      <div className="grid gap-6 md:grid-cols-2">
        <CardContainer className="w-full">
          <CardBody>
            <CardItem translateZ={20}>
              <Card className="w-full glow-purple">
                <CardHeader>
                  <CardTitle className="flex items-center gap-2">
                    <Store className="h-5 w-5" />
                    Storefront
                  </CardTitle>
                </CardHeader>
                <CardContent className="space-y-4">
                  <div className="space-y-2">
                    <label className="text-sm font-medium">Platform</label>
                    <div className="flex flex-col gap-2">
                      {STOREFRONT_OPTIONS.map((option) => (
                        <Button
                          key={option.value}
                          variant={storefront === option.value ? "default" : "outline"}
                          className="w-full justify-start"
                          onClick={() => setStorefrontValue(option.value)}
                        >
                          {option.label}
                        </Button>
                      ))}
                    </div>
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
                    <Info className="h-5 w-5" />
                    About
                  </CardTitle>
                </CardHeader>
                <CardContent>
                  <div className="flex items-center justify-between">
                    <span className="text-sm text-muted-foreground">Version</span>
                    <Badge variant="outline">{version || "Unknown"}</Badge>
                  </div>
                </CardContent>
              </Card>
            </CardItem>
          </CardBody>
        </CardContainer>
      </div>
    </div>
  );
}
