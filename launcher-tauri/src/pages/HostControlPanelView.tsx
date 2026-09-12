import { useState, useEffect } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { showToast, formatError } from "@/components/Toast";
import { Users, Hash, Copy, Check, Trash2, UserMinus, Globe, Gamepad2 } from "lucide-react";

interface Player {
  name: string;
  level?: number;
  ping?: number;
  is_host?: boolean;
}

interface LobbyInfo {
  code: string;
  region?: string;
  host?: string;
  maxPlayers?: number;
  map?: string;
}

export default function HostControlPanelView() {
  const [lobbyInfo, setLobbyInfo] = useState<LobbyInfo | null>(null);
  const [players, setPlayers] = useState<Player[]>([]);
  const [posted, setPosted] = useState(false);
  const [copied, setCopied] = useState(false);
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    const unlistenLobbyCreated = listen<LobbyInfo>("lobby-created", (event) => {
      setLobbyInfo(event.payload);
      setPlayers([]);
      setPosted(true);
    });

    const unlistenLobbyClosed = listen("lobby-closed", () => {
      setLobbyInfo(null);
      setPlayers([]);
      setPosted(false);
    });

    const unlistenPlayerJoined = listen<Player>("player-joined", (event) => {
      const player = event.payload;
      setPlayers((prev) => {
        if (prev.some((p) => p.name === player.name)) return prev;
        return [...prev, player];
      });
    });

    const unlistenPlayerLeft = listen<{ name: string }>("player-left", (event) => {
      setPlayers((prev) => prev.filter((p) => p.name !== event.payload.name));
    });

    return () => {
      unlistenLobbyCreated.then((fn) => fn());
      unlistenLobbyClosed.then((fn) => fn());
      unlistenPlayerJoined.then((fn) => fn());
      unlistenPlayerLeft.then((fn) => fn());
    };
  }, []);

  async function copyLobbyCode() {
    if (!lobbyInfo?.code) return;
    try {
      await navigator.clipboard.writeText(lobbyInfo.code);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch (e) {
      console.error("Failed to copy:", e);
    }
  }

  async function handlePostLobby() {
    setLoading(true);
    try {
      await invoke("post_lobby");
      setPosted(true);
    } catch (e) {
      showToast(`Failed to post lobby: ${formatError(e)}`, "error");
    } finally {
      setLoading(false);
    }
  }

  async function handleDisbandLobby() {
    setLoading(true);
    try {
      await invoke("disband_lobby");
      setLobbyInfo(null);
      setPlayers([]);
      setPosted(false);
    } catch (e) {
      showToast(`Failed to disband lobby: ${formatError(e)}`, "error");
    } finally {
      setLoading(false);
    }
  }

  async function handleKickPlayer(playerName: string) {
    try {
      await invoke("kick_player", { playerName });
      setPlayers((prev) => prev.filter((p) => p.name !== playerName));
    } catch (e) {
      showToast(`Failed to kick player: ${formatError(e)}`, "error");
    }
  }

  if (!lobbyInfo) {
    return (
      <div className="min-h-full p-6 space-y-6">
        <h1 className="text-3xl font-bold">Host Control Panel</h1>
        <Card>
          <CardContent className="pt-6">
            <p className="text-sm text-muted-foreground text-center">
              No active lobby. Create a lobby in-game to manage it here.
            </p>
          </CardContent>
        </Card>
      </div>
    );
  }

  return (
    <div className="min-h-full p-6 space-y-6">
      <div className="flex items-center justify-between">
        <h1 className="text-3xl font-bold">Host Control Panel</h1>
        <Badge variant={posted ? "neutral" : "muted"} showDot dotColor={posted ? "emerald" : "red"}>
          {posted ? "Posted to Server" : "Local Only"}
        </Badge>
      </div>

      <div className="grid gap-6 md:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Hash className="h-5 w-5" />
              Lobby Code
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            <div className="flex items-center justify-between rounded-lg bg-secondary/50 px-4 py-3">
              <span className="font-mono text-2xl font-bold tracking-widest text-primary">
                {lobbyInfo.code}
              </span>
              <Button
                variant="ghost"
                size="icon"
                onClick={copyLobbyCode}
                className="h-8 w-8"
              >
                {copied ? (
                  <Check className="h-4 w-4 text-emerald-500" />
                ) : (
                  <Copy className="h-4 w-4" />
                )}
              </Button>
            </div>

            {lobbyInfo.region && (
              <div className="flex items-center justify-between">
                <span className="text-sm text-muted-foreground">Region</span>
                <span className="text-sm font-medium flex items-center gap-1">
                  <Globe className="h-3 w-3" />
                  {lobbyInfo.region}
                </span>
              </div>
            )}

            {lobbyInfo.map && (
              <div className="flex items-center justify-between">
                <span className="text-sm text-muted-foreground">Map</span>
                <span className="text-sm font-medium flex items-center gap-1">
                  <Gamepad2 className="h-3 w-3" />
                  {lobbyInfo.map}
                </span>
              </div>
            )}

            {lobbyInfo.maxPlayers && (
              <div className="flex items-center justify-between">
                <span className="text-sm text-muted-foreground">Max Players</span>
                <span className="text-sm font-medium">{lobbyInfo.maxPlayers}</span>
              </div>
            )}

            <div className="flex gap-2 pt-2">
              <Button
                onClick={handlePostLobby}
                disabled={posted || loading}
                className="flex-1"
              >
                {posted ? "Already Posted" : "POST"}
              </Button>
              <Button
                onClick={handleDisbandLobby}
                variant="destructive"
                disabled={loading}
                className="flex-1"
              >
                <Trash2 className="h-4 w-4" />
                Disband
              </Button>
            </div>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Users className="h-5 w-5" />
              Players ({players.length})
            </CardTitle>
          </CardHeader>
          <CardContent>
            {players.length === 0 ? (
              <p className="text-sm text-muted-foreground">No players in lobby.</p>
            ) : (
              <ul className="space-y-2">
                {players.map((player) => (
                  <li
                    key={player.name}
                    className="flex items-center justify-between rounded-lg bg-secondary/50 px-3 py-2"
                  >
                    <div className="flex items-center gap-2">
                      <span className="h-1.5 w-1.5 rounded-full bg-emerald-500" />
                      <span className="text-sm font-medium">{player.name}</span>
                      {player.is_host && (
                        <Badge variant="neutral" className="text-xs">
                          Host
                        </Badge>
                      )}
                      {player.level !== undefined && (
                        <span className="text-xs text-muted-foreground">Lv.{player.level}</span>
                      )}
                      {player.ping !== undefined && (
                        <span className="text-xs text-muted-foreground">{player.ping}ms</span>
                      )}
                    </div>
                    <Button
                      variant="ghost"
                      size="icon"
                      onClick={() => handleKickPlayer(player.name)}
                      className="h-8 w-8"
                      disabled={player.is_host}
                    >
                      <UserMinus className="h-4 w-4 text-destructive" />
                    </Button>
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>
      </div>
    </div>
  );
}
