import { useState, useEffect, useCallback, useRef } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Input } from "@/components/ui/input";
import { EmptyState } from "@/components/ui/empty-state";
import { PlayerRow } from "@/components/ui/player-row";
import { Skeleton } from "@/components/ui/skeleton";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { showToast, formatError } from "@/components/Toast";
import { Users, Gamepad2, Hash } from "lucide-react";

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

// Payload of the game's `join_lobby_result` IPC message (see
// src-tauri/src/ipc_handler.rs — `JoinResult { success, error }`).
interface JoinResultPayload {
  success?: boolean;
  error?: string | null;
}

interface InGameViewProps {
  /**
   * Game connection state owned by App — App registers its listener before
   * this page can mount, so this reflects pre-mount truth (a local listener
   * alone would miss events that fired before mount).
   */
  connected: boolean;
  /**
   * App-level lobby membership (true while in a lobby, as host or guest).
   * Used to guard a re-join: joining another lobby kicks you from the
   * current one, so we confirm first.
   */
  inLobby?: boolean;
  /** Pending lobby code from an `amonglauncher://join` deep link, if any. */
  initialJoinCode?: string | null;
  /** Called once the pending deep-link code has been handed to the join flow. */
  onJoinCodeConsumed?: () => void;
}

export default function InGameView({
  connected,
  inLobby = false,
  initialJoinCode,
  onJoinCodeConsumed,
}: InGameViewProps) {
  const [lobbyCode, setLobbyCode] = useState("");
  const [activeLobbyCode, setActiveLobbyCode] = useState<string | null>(null);
  const [players, setPlayers] = useState<Player[]>([]);
  const [mods, setMods] = useState<ModEntry[]>([]);
  const [joining, setJoining] = useState(false);
  // Code awaiting confirmation because the user is already in a lobby.
  const [confirmJoinCode, setConfirmJoinCode] = useState<string | null>(null);
  // Code of the most recent join request — used to set the active lobby on
  // `join_lobby_result` success (the payload carries no code).
  const lastSubmittedCode = useRef<string | null>(null);
  // Deep-link codes must auto-submit at most once each.
  const autoJoinHandled = useRef<string | null>(null);

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
      case "join_lobby_result": {
        // The game's answer to our join request — success/failure was
        // previously invisible. Payload: { success, error }.
        const result = payload as JoinResultPayload;
        if (result?.success) {
          const code = (lastSubmittedCode.current ?? "").trim().toUpperCase();
          if (code) {
            setActiveLobbyCode(code);
          }
          showToast(`Joined lobby ${code || "lobby"}`, "success");
        } else {
          showToast(
            `Failed to join lobby: ${result?.error || "unknown error"}`,
            "error"
          );
        }
        break;
      }
    }
  }, []);

  useEffect(() => {
    loadMods();

    const unlistenMessage = listen<IpcEnvelope>("ipc:message", handleIpcMessage);
    // Connection state itself comes from the `connected` prop (App level);
    // this listener just clears stale lobby data immediately on disconnect
    // (App will unmount this route right after).
    const unlistenDisconnected = listen("ipc:client-disconnected", () => {
      setActiveLobbyCode(null);
      setPlayers([]);
    });

    return () => {
      unlistenMessage.then((fn) => fn());
      unlistenDisconnected.then((fn) => fn());
    };
  }, [handleIpcMessage]);

  // Deep-link join: prefill the input and auto-submit once connected.
  useEffect(() => {
    if (!initialJoinCode) return;
    const code = initialJoinCode.trim().toUpperCase();
    if (!code) {
      onJoinCodeConsumed?.();
      return;
    }
    setLobbyCode(code);
    // Consume the pending code in EVERY path that handles it: a repeat
    // same-code deep link used to skip this branch and strand App's
    // `pendingJoinCode` non-null until this view remounted. `autoJoinHandled`
    // alone now gates the submit-once behavior (submitJoin still self-gates
    // on `connected`).
    onJoinCodeConsumed?.();
    if (autoJoinHandled.current !== code) {
      autoJoinHandled.current = code;
      void submitJoin(code);
    }
    // `onJoinCodeConsumed` intentionally omitted: it is re-created on every
    // App render and must not retrigger this effect.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [initialJoinCode, connected]);

  async function loadMods() {
    try {
      const installedMods = await invoke<ModEntry[]>("get_mods");
      setMods(installedMods);
    } catch (e) {
      console.error("Failed to get mods:", e);
    }
  }

  async function submitJoin(rawCode: string) {
    const code = rawCode.trim().toUpperCase();
    // Gate on `connected`: the backend now errors honestly with
    // "Not connected" instead of silently queueing the message.
    if (!code || !connected || joining) return;
    // Guard rail: joining another lobby kicks you from the current one, so
    // confirm first. This is the single funnel for manual join, Enter,
    // deep links, and pending-link recovery — one check covers all.
    if (inLobby) {
      setConfirmJoinCode(code);
      return;
    }
    await performJoin(code);
  }

  async function performJoin(code: string) {
    lastSubmittedCode.current = code;
    setJoining(true);
    try {
      await invoke("join_lobby", { code });
    } catch (e) {
      showToast(`Failed to join lobby: ${formatError(e)}`, "error");
    } finally {
      setJoining(false);
    }
  }

  async function joinLobby() {
    await submitJoin(lobbyCode);
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
    <div className="min-h-full space-y-6 p-6">
      <div className="flex items-center justify-between gap-4">
        <h1 className="text-display font-bold tracking-tight">In Game</h1>
        <Badge
          variant={connected ? "success" : "danger"}
          showDot
          dotColor={connected ? "success" : "danger"}
        >
          {connected ? "Game Connected" : "Game Disconnected"}
        </Badge>
      </div>

      <div className="grid gap-6 md:grid-cols-2">
        <Card className="w-full">
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Hash className="h-5 w-5 text-muted-foreground" />
              {activeLobbyCode ? "Lobby" : "Join Lobby"}
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            {activeLobbyCode ? (
              <div className="space-y-3">
                <div className="flex items-center justify-between rounded-control border border-border bg-surface-2 px-3 py-2">
                  <span className="text-13 text-muted-foreground">Current Code</span>
                  <span className="font-mono text-lg font-bold tracking-widest text-primary">
                    {activeLobbyCode}
                  </span>
                </div>
                <Button onClick={() => void leaveLobby()} variant="destructive" className="w-full">
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
                <Button
                  variant="primary"
                  onClick={() => void joinLobby()}
                  disabled={!lobbyCode.trim() || joining || !connected}
                  title={connected ? "Join lobby" : "Connect to the game first"}
                >
                  {joining ? "Joining..." : "Join"}
                </Button>
              </div>
            )}
          </CardContent>
        </Card>

        <Card className="w-full">
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Users className="h-5 w-5 text-muted-foreground" />
              Players ({players.length})
            </CardTitle>
          </CardHeader>
          <CardContent>
            {!connected ? (
              <EmptyState
                icon={<Users className="h-5 w-5" />}
                title="Game not connected"
                description="Connect to the game first to see the lobby roster."
              />
            ) : joining && players.length === 0 ? (
              // Pending join: roster is still settling, show placeholders
              // instead of a misleading "no players" empty state.
              <div className="space-y-2">
                <Skeleton className="h-10 w-full" />
                <Skeleton className="h-10 w-full" />
              </div>
            ) : players.length === 0 ? (
              <EmptyState
                icon={<Users className="h-5 w-5" />}
                title="No players detected"
                description="Players will appear here once they join the lobby."
              />
            ) : (
              <ul className="space-y-2">
                {players.map((player) => (
                  <PlayerRow key={player.name} name={player.name} color={player.color} />
                ))}
              </ul>
            )}
          </CardContent>
        </Card>
      </div>

      <Card className="w-full">
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Gamepad2 className="h-5 w-5 text-muted-foreground" />
            Active Mods
          </CardTitle>
        </CardHeader>
        <CardContent>
          {mods.length === 0 ? (
            <EmptyState
              icon={<Gamepad2 className="h-5 w-5" />}
              title="No mods installed"
              description="Installed mods will be listed here."
            />
          ) : (
            <ul className="space-y-2">
              {mods.map((mod) => (
                <li
                  key={mod.name}
                  className="flex items-center justify-between rounded-control border border-border bg-surface-2 px-3 py-2"
                >
                  <span className="text-sm font-medium text-foreground">{mod.name}</span>
                  {mod.version && (
                    <span className="text-2xs text-muted-foreground">v{mod.version}</span>
                  )}
                </li>
              ))}
            </ul>
          )}
        </CardContent>
      </Card>

      {/* Guard rail: joining while already in a lobby kicks you from the
          current one, so make the consequence explicit before proceeding.
          Non-danger styling — this is a warning, not a destructive action. */}
      <ConfirmDialog
        isOpen={confirmJoinCode !== null}
        onClose={() => setConfirmJoinCode(null)}
        onConfirm={() => {
          const code = confirmJoinCode;
          if (code) void performJoin(code);
        }}
        title="Join another lobby?"
        message="You're already in a lobby. Joining another will kick you from your current lobby."
        confirmText="Join anyway"
      />
    </div>
  );
}
