import { useState, useEffect, useCallback } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { CardContainer, CardBody, CardItem } from "@/components/ui/3d-card";
import { Users, Gamepad2, Hash, Wifi, WifiOff } from "lucide-react";

interface Player {
  name: string;
  color: string;
}

interface ModEntry {
  name: string;
  version?: string;
}

interface IpcEnvelope {
  type: string;
  id: string;
  timestamp: number;
  payload?: unknown;
}

interface LobbyPayload {
  code: string;
}

interface PlayerPayload {
  name: string;
  color: string;
}

export default function InGameView() {
  const [lobbyCode, setLobbyCode] = useState("");
  const [activeLobbyCode, setActiveLobbyCode] = useState<string | null>(null);
  const [players, setPlayers] = useState<Player[]>([]);
  const [mods, setMods] = useState<ModEntry[]>([]);
  const [connected, setConnected] = useState(false);
  const [joining, setJoining] = useState(false);

  const handleIpcMessage = useCallback((event: { payload: IpcEnvelope }) => {
    const { type, payload } = event.payload;
    switch (type) {
      case "lobby_created":
      case "lobby_joined": {
        const lobby = payload as LobbyPayload;
        if (lobby?.code) {
          setActiveLobbyCode(lobby.code);
        }
        break;
      }
      case "lobby_left":
        setActiveLobbyCode(null);
        setPlayers([]);
        break;
      case "player_joined": {
        const player = payload as PlayerPayload;
        if (player?.name) {
          setPlayers((prev) => {
            if (prev.some((p) => p.name === player.name)) return prev;
            return [...prev, { name: player.name, color: player.color || "#888" }];
          });
        }
        break;
      }
      case "player_left": {
        const player = payload as PlayerPayload;
        if (player?.name) {
          setPlayers((prev) => prev.filter((p) => p.name !== player.name));
        }
        break;
      }
      case "players_list": {
        const list = payload as Player[];
        if (Array.isArray(list)) {
          setPlayers(list.map((p) => ({ name: p.name, color: p.color || "#888" })));
        }
        break;
      }
    }
  }, []);

  useEffect(() => {
    loadMods();

    const unlistenMessage = listen<IpcEnvelope>("ipc:message", handleIpcMessage);
    const unlistenConnected = listen("ipc:client-connected", () => setConnected(true));
    const unlistenDisconnected = listen("ipc:client-disconnected", () => {
      setConnected(false);
      setActiveLobbyCode(null);
      setPlayers([]);
    });

    return () => {
      unlistenMessage.then((fn) => fn());
      unlistenConnected.then((fn) => fn());
      unlistenDisconnected.then((fn) => fn());
    };
  }, [handleIpcMessage]);

  async function loadMods() {
    try {
      const installedMods = await invoke<ModEntry[]>("get_mods");
      setMods(installedMods);
    } catch (e) {
      console.error("Failed to get mods:", e);
    }
  }

  async function joinLobby() {
    if (!lobbyCode.trim()) return;
    setJoining(true);
    try {
      await invoke("send_ipc_message", {
        msgType: "join_lobby",
        payload: { code: lobbyCode.trim().toUpperCase() },
      });
    } catch (e) {
      console.error("Failed to join lobby:", e);
    } finally {
      setJoining(false);
    }
  }

  async function leaveLobby() {
    try {
      await invoke("send_ipc_message", {
        msgType: "leave_lobby",
        payload: null,
      });
      setActiveLobbyCode(null);
      setPlayers([]);
    } catch (e) {
      console.error("Failed to leave lobby:", e);
    }
  }

  return (
    <div className="min-h-full bg-grid p-6 space-y-6">
      <div className="flex items-center justify-between">
        <h1 className="font-display text-3xl font-bold text-primary">In Game</h1>
        <div className="flex items-center gap-2">
          {connected ? (
            <Wifi className="h-4 w-4 text-emerald-500" />
          ) : (
            <WifiOff className="h-4 w-4 text-muted-foreground" />
          )}
          <span className="text-sm text-muted-foreground">
            {connected ? "Game Connected" : "Game Disconnected"}
          </span>
        </div>
      </div>

      <div className="grid gap-6 md:grid-cols-2">
        <CardContainer className="w-full">
          <CardBody>
            <CardItem translateZ={20}>
              <Card className="w-full glow-primary">
                <CardHeader>
                  <CardTitle className="flex items-center gap-2">
                    <Hash className="h-5 w-5" />
                    {activeLobbyCode ? "Lobby" : "Join Lobby"}
                  </CardTitle>
                </CardHeader>
                <CardContent className="space-y-4">
                  {activeLobbyCode ? (
                    <div className="space-y-3">
                      <div className="flex items-center justify-between rounded-lg bg-secondary/50 px-3 py-2">
                        <span className="text-sm text-muted-foreground">Current Code</span>
                        <span className="font-mono text-lg font-bold tracking-widest text-primary">
                          {activeLobbyCode}
                        </span>
                      </div>
                      <Button onClick={leaveLobby} variant="destructive" className="w-full">
                        Leave Lobby
                      </Button>
                    </div>
                  ) : (
                    <div className="flex gap-2">
                      <Input
                        value={lobbyCode}
                        onChange={(e) => setLobbyCode(e.target.value)}
                        placeholder="Enter lobby code"
                        className="flex-1 font-mono uppercase tracking-widest"
                        maxLength={6}
                        onKeyDown={(e) => e.key === "Enter" && joinLobby()}
                      />
                      <Button onClick={joinLobby} disabled={!lobbyCode.trim() || joining}>
                        {joining ? "Joining..." : "Join"}
                      </Button>
                    </div>
                  )}
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
                    Players ({players.length})
                  </CardTitle>
                </CardHeader>
                <CardContent>
                  {!connected ? (
                    <p className="text-sm text-muted-foreground">Connect to game first.</p>
                  ) : players.length === 0 ? (
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
                          {mod.version && (
                            <span className="ml-2 text-xs text-muted-foreground">v{mod.version}</span>
                          )}
                        </div>
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
