import { useState, useEffect } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { CardContainer, CardBody, CardItem } from "@/components/ui/3d-card";
import { Users, Gamepad2, Hash } from "lucide-react";

interface Player {
  name: string;
  color: string;
}

interface Mod {
  name: string;
  version: string;
  enabled: boolean;
}

export default function InGameView() {
  const [lobbyCode, setLobbyCode] = useState("");
  const [players, setPlayers] = useState<Player[]>([]);
  const [mods, setMods] = useState<Mod[]>([]);

  useEffect(() => {
    loadMods();

    const unlistenPlayer = listen<Player[]>("players-updated", (event) => {
      setPlayers(event.payload);
    });

    return () => {
      unlistenPlayer.then((fn) => fn());
    };
  }, []);

  async function loadMods() {
    try {
      const installedMods = await invoke<Mod[]>("get_mods");
      setMods(installedMods);
    } catch (e) {
      console.error("Failed to get mods:", e);
    }
  }

  async function joinLobby() {
    if (!lobbyCode.trim()) return;
    try {
      await invoke("join_lobby", { code: lobbyCode.trim() });
    } catch (e) {
      console.error("Failed to join lobby:", e);
    }
  }

  return (
    <div className="min-h-full bg-grid p-6 space-y-6">
      <h1 className="font-display text-3xl font-bold text-primary">In Game</h1>

      <div className="grid gap-6 md:grid-cols-2">
        <CardContainer className="w-full">
          <CardBody>
            <CardItem translateZ={20}>
              <Card className="w-full glow-primary">
                <CardHeader>
                  <CardTitle className="flex items-center gap-2">
                    <Hash className="h-5 w-5" />
                    Join Lobby
                  </CardTitle>
                </CardHeader>
                <CardContent className="space-y-4">
                  <div className="flex gap-2">
                    <Input
                      value={lobbyCode}
                      onChange={(e) => setLobbyCode(e.target.value)}
                      placeholder="Enter lobby code"
                      className="flex-1 font-mono uppercase tracking-widest"
                      maxLength={6}
                    />
                    <Button onClick={joinLobby} disabled={!lobbyCode.trim()}>
                      Join
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
              <Card className="w-full glow-emerald">
                <CardHeader>
                  <CardTitle className="flex items-center gap-2">
                    <Users className="h-5 w-5" />
                    Players
                  </CardTitle>
                </CardHeader>
                <CardContent>
                  {players.length === 0 ? (
                    <p className="text-sm text-muted-foreground">No players detected.</p>
                  ) : (
                    <ul className="space-y-2">
                      {players.map((player) => (
                        <li
                          key={player.name}
                          className="flex items-center justify-between rounded-lg bg-secondary/50 px-3 py-2"
                        >
                          <span className="text-sm font-medium">{player.name}</span>
                          <div
                            className="h-4 w-4 rounded-full border border-border"
                            style={{ backgroundColor: player.color }}
                          />
                        </li>
                      ))}
                    </ul>
                  )}
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
                  <Gamepad2 className="h-5 w-5" />
                  Active Mods
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
                          <span className="ml-2 text-xs text-muted-foreground">v{mod.version}</span>
                        </div>
                        <Badge variant={mod.enabled ? "default" : "muted"}>
                          {mod.enabled ? "Enabled" : "Disabled"}
                        </Badge>
                      </li>
                    ))}
                  </ul>
                )}
              </CardContent>
            </Card>
          </CardItem>
        </CardBody>
      </CardContainer>
    </div>
  );
}
