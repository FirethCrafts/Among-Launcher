import { useState, useEffect } from "react";
import { invoke } from "@tauri-apps/api/core";
import { open } from "@tauri-apps/plugin-dialog";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { CardContainer, CardBody, CardItem } from "@/components/ui/3d-card";
import { User, FolderOpen, RotateCcw, Info, LogOut } from "lucide-react";

interface AccountInfo {
  username: string;
  avatarUrl: string | null;
}

export default function SettingsView() {
  const [account, setAccount] = useState<AccountInfo | null>(null);
  const [gamePath, setGamePath] = useState("");
  const [version, setVersion] = useState("");

  useEffect(() => {
    loadSettings();
  }, []);

  async function loadSettings() {
    try {
      const acc = await invoke<AccountInfo | null>("get_account");
      setAccount(acc);
      const path = await invoke<string>("get_game_path");
      setGamePath(path);
      const ver = await invoke<string>("get_version");
      setVersion(ver);
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
        setGamePath(selected as string);
        await invoke("set_game_path", { path: selected });
      }
    } catch (e) {
      console.error("Failed to browse:", e);
    }
  }

  async function resetGamePath() {
    try {
      await invoke("reset_game_path");
      setGamePath("");
    } catch (e) {
      console.error("Failed to reset path:", e);
    }
  }

  async function logout() {
    try {
      await invoke("logout");
      setAccount(null);
    } catch (e) {
      console.error("Failed to logout:", e);
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
  );
}
