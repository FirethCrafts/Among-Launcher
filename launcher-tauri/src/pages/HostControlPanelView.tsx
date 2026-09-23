import { useState, useEffect, useRef } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { ConfirmModal } from "@/components/Modal";
import { showToast, formatError } from "@/components/Toast";
import { Users, Hash, Copy, Check, Trash2, UserMinus, Globe, Gamepad2 } from "lucide-react";

interface Player {
  name: string;
  level?: number;
  ping?: number;
  color?: string;
  is_host?: boolean;
}

interface LobbyInfo {
  code: string;
  region?: string;
  host?: string;
  maxPlayers?: number;
  map?: string;
  /** Present on the `lobby-created` event: true when THIS machine is host. */
  isHost?: boolean;
}

// Mirrors the `get_lobby_state` command (camelCase — note the top-level
// `isHost` and each player's `isHost` are camelCase; the `player-joined`
// event uses snake_case `is_host`).
interface LobbyStateSnapshot {
  code: string | null;
  posted: boolean;
  players: Array<{
    name: string;
    level: number | null;
    ping: number | null;
    color: string | null;
    isHost: boolean;
  }>;
  hostName: string | null;
  map: string | null;
  maxPlayers: number | null;
  /** Whether THIS machine is the in-game host. */
  isHost: boolean;
}

interface HeartbeatStatus {
  ok: boolean;
  error: string | null;
}

export default function HostControlPanelView() {
  const [lobbyInfo, setLobbyInfo] = useState<LobbyInfo | null>(null);
  const [players, setPlayers] = useState<Player[]>([]);
  const [posted, setPosted] = useState(false);
  // Whether THIS machine is the host. `null` = unknown (no lobby or a
  // snapshot/event that predates the `isHost` field). A known `false` means
  // we are a guest in someone else's lobby — render read-only.
  const [isHost, setIsHost] = useState<boolean | null>(null);
  const [heartbeatOk, setHeartbeatOk] = useState(false);
  const [copied, setCopied] = useState(false);
  const [loading, setLoading] = useState(false);
  const [confirmDisband, setConfirmDisband] = useState(false);
  const [kickTarget, setKickTarget] = useState<string | null>(null);
  // Field-level staleness gates for the mount-time snapshot: a snapshot
  // field is applied ONLY if no event that actually carries (or mutates)
  // that field has landed since mount. Player events carry no lobby/posted
  // data and lobby events carry no player payload, so a single shared flag
  // used to wedge the panel — a `player-joined` racing the snapshot blocked
  // `lobbyInfo`/`posted` forever (permanent "No active lobby" + a still-
  // clickable POST on an already-posted lobby).
  const sawLobbyEvent = useRef(false); // gates `lobbyInfo`
  const sawPostedEvent = useRef(false); // gates `posted`
  const sawPlayerEvent = useRef(false); // gates `players`

  useEffect(() => {
    let cancelled = false;

    // Initial state fetch: events that fired before this page mounted
    // (or before it existed as a route) would otherwise be missed.
    invoke<LobbyStateSnapshot>("get_lobby_state")
      .then((snapshot) => {
        if (cancelled) return;
        // Apply each field only if its own gate hasn't fired — an event on
        // one axis (players) must not block the others (lobby/posted).
        if (!sawPostedEvent.current) setPosted(snapshot.posted);
        if (!sawPlayerEvent.current) {
          setPlayers(
            snapshot.players.map((p) => ({
              name: p.name,
              level: p.level ?? undefined,
              ping: p.ping ?? undefined,
              color: p.color ?? undefined,
              is_host: p.isHost,
            }))
          );
        }
        if (!sawLobbyEvent.current) {
          // Fail-closed: a snapshot from an older backend/contract that omits
          // `isHost` must NOT fall through to the full host panel. The event
          // path already normalizes with `Boolean(...)`; match it here.
          setIsHost(Boolean(snapshot.isHost));
          if (snapshot.code) {
            setLobbyInfo({
              code: snapshot.code,
              host: snapshot.hostName ?? undefined,
              map: snapshot.map ?? undefined,
              maxPlayers: snapshot.maxPlayers ?? undefined,
            });
          } else {
            setLobbyInfo(null);
          }
        }
      })
      .catch(() => {
        // Snapshot unavailable — the listeners below still keep us in sync.
      });

    const unlistenLobbyCreated = listen<LobbyInfo>("lobby-created", (event) => {
      // Carries lobby + posted (backend resets `posted=false` for a new
      // lobby) and this handler resets `players` — claim all three gates.
      sawLobbyEvent.current = true;
      sawPostedEvent.current = true;
      sawPlayerEvent.current = true;
      setLobbyInfo(event.payload);
      setIsHost(Boolean(event.payload?.isHost));
      setPlayers([]);
      // POST must stay clickable (it used to be permanently disabled here).
      setPosted(false);
      setHeartbeatOk(false);
    });

    const unlistenLobbyClosed = listen("lobby-closed", () => {
      // Carries lobby existence (and this handler resets posted/players).
      // The Rust side NOW clears `lobby_state` (code/players/posted +
      // heartbeat abort) BEFORE emitting — on LobbyClosed, pipe
      // disconnect, and stop_game — so a snapshot read after this event is
      // already clean; these resets just mirror that for local React state.
      sawLobbyEvent.current = true;
      sawPostedEvent.current = true;
      sawPlayerEvent.current = true;
      setLobbyInfo(null);
      setIsHost(null);
      setPlayers([]);
      setPosted(false);
      setHeartbeatOk(false);
    });

    const unlistenPlayerJoined = listen<Player>("player-joined", (event) => {
      // Player payload only — must NOT gate lobbyInfo/posted below.
      sawPlayerEvent.current = true;
      const player = event.payload;
      setPlayers((prev) => {
        if (prev.some((p) => p.name === player.name)) return prev;
        return [...prev, player];
      });
    });

    const unlistenPlayerLeft = listen<{ name: string }>("player-left", (event) => {
      // Player payload only — must NOT gate lobbyInfo/posted below.
      sawPlayerEvent.current = true;
      setPlayers((prev) => prev.filter((p) => p.name !== event.payload.name));
    });

    const unlistenPlayersList = listen<Player[]>("players_list", (event) => {
      // Full roster snapshot, emitted right after `lobby-created` (seeded
      // from the mod's playerNames/playerLevels/playerPings). Without this
      // the panel showed an empty roster until the first join/leave or a
      // remount. Player payload only — claim `sawPlayerEvent` so an
      // in-flight mount snapshot can't clobber a fresher list; must NOT
      // gate lobbyInfo/posted. Event order is lobby-created → players_list,
      // so the empty `setPlayers([])` in that handler is replaced here.
      sawPlayerEvent.current = true;
      const list = Array.isArray(event.payload) ? event.payload : [];
      const next: Player[] = list.map((p) => ({
        name: p.name,
        level: p.level ?? undefined,
        ping: p.ping ?? undefined,
        color: p.color ?? undefined,
        // Event is snake_case `is_host`; normalize like the snapshot path.
        is_host: Boolean(p.is_host),
      }));
      // Skip the state update when there is nothing to change.
      setPlayers((prev) => (prev.length === 0 && next.length === 0 ? prev : next));
    });

    const unlistenHeartbeat = listen<HeartbeatStatus>("heartbeat-status", (event) => {
      // Deliberately NOT a snapshot gate: heartbeat only reports HTTP health,
      // it never mutates lobby state (the snapshot can't be made stale by it),
      // and this handler doesn't populate lobbyInfo/posted — gating on it
      // would wedge those fields with no way to ever fill them.
      setHeartbeatOk(Boolean(event.payload?.ok));
    });

    return () => {
      cancelled = true;
      unlistenLobbyCreated.then((fn) => fn());
      unlistenLobbyClosed.then((fn) => fn());
      unlistenPlayerJoined.then((fn) => fn());
      unlistenPlayerLeft.then((fn) => fn());
      unlistenPlayersList.then((fn) => fn());
      unlistenHeartbeat.then((fn) => fn());
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
    // This handler commits `posted` in BOTH outcomes — claim the gate before
    // awaiting so a slow in-flight snapshot can't overwrite the result.
    sawPostedEvent.current = true;
    try {
      await invoke("post_lobby");
      // The command returns Result: success is the source of truth.
      setPosted(true);
    } catch (e) {
      setPosted(false);
      showToast(`Failed to post lobby: ${formatError(e)}`, "error");
    } finally {
      setLoading(false);
    }
  }

  async function handleDisbandLobby() {
    setLoading(true);
    try {
      await invoke("disband_lobby");
      // This handler commits lobbyInfo/posted/players — claim all gates so a
      // late snapshot can't resurrect anything (failure path commits nothing,
      // so the gates stay open for the snapshot to fill).
      sawLobbyEvent.current = true;
      sawPostedEvent.current = true;
      sawPlayerEvent.current = true;
      setLobbyInfo(null);
      setIsHost(null);
      setPlayers([]);
      setPosted(false);
      setHeartbeatOk(false);
    } catch (e) {
      showToast(`Failed to disband lobby: ${formatError(e)}`, "error");
    } finally {
      setLoading(false);
    }
  }

  async function handleKickPlayer(playerName: string) {
    try {
      await invoke("kick_player", { playerName });
      // Commits `players` — a late snapshot must not resurrect the kick.
      sawPlayerEvent.current = true;
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

  // A guest in someone else's lobby: there is a lobby, but this machine has
  // no host authority. Show read-only lobby info instead of the POST/
  // Disband/Kick controls.
  if (isHost === false) {
    return (
      <div className="min-h-full p-6 space-y-6">
        <h1 className="text-3xl font-bold">Host Control Panel</h1>
        <Card>
          <CardContent className="pt-6 space-y-4">
            <p className="text-sm text-muted-foreground text-center">
              You're in a lobby, but you're not the host. Only the host can
              post, kick players, or disband it.
            </p>
            {lobbyInfo.code && (
              <div className="flex items-center justify-between rounded-xl border border-white/10 bg-white/[0.03] px-4 py-3">
                <span className="text-sm text-muted-foreground">Lobby Code</span>
                <span className="font-mono text-lg font-bold tracking-widest text-primary">
                  {lobbyInfo.code}
                </span>
              </div>
            )}
          </CardContent>
        </Card>
      </div>
    );
  }

  return (
    <div className="min-h-full p-6 space-y-6">
      <h1 className="text-3xl font-bold">Host Control Panel</h1>

      <div className="grid gap-6 md:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Hash className="h-5 w-5" />
              Lobby Code
              {/* Heartbeat pill: Online/Offline right next to the code. */}
              <Badge
                variant={heartbeatOk ? "neutral" : "muted"}
                showDot
                dotColor={heartbeatOk ? "emerald" : "red"}
                className="ml-auto"
                title={heartbeatOk ? "Server heartbeat OK" : "No server heartbeat"}
              >
                {heartbeatOk ? "Online" : "Offline"}
              </Badge>
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            <div className="flex items-center justify-between rounded-xl border border-white/10 bg-white/[0.03] px-4 py-3 transition-colors hover:bg-white/[0.07]">
              <span className="font-mono text-2xl font-bold tracking-widest text-primary">
                {lobbyInfo.code}
              </span>
              <Button
                variant="ghost"
                size="icon"
                onClick={copyLobbyCode}
                className="h-8 w-8"
                title={copied ? "Copied" : "Copy lobby code"}
                aria-label="Copy lobby code"
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
                onClick={() => void handlePostLobby()}
                disabled={posted || loading}
                className="flex-1"
              >
                {posted ? "Already Posted" : "POST"}
              </Button>
              <Button
                onClick={() => setConfirmDisband(true)}
                variant="destructive"
                disabled={loading}
                className="flex-1"
              >
                <Trash2 className="h-4 w-4" />
                Disband
              </Button>
            </div>

            {/* Status line moved directly under the action buttons so it
                stays visible in short (~500px) windows. */}
            <div className="flex justify-center pt-1">
              <Badge
                variant={posted ? "neutral" : "muted"}
                showDot
                dotColor={posted ? "emerald" : "red"}
              >
                {posted ? "Posted to Server" : "Local Only"}
              </Badge>
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
                    className="flex items-center justify-between rounded-xl border border-white/10 bg-white/[0.03] px-3 py-2 transition-colors hover:bg-white/[0.07]"
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
                      onClick={() => setKickTarget(player.name)}
                      className="h-8 w-8"
                      disabled={player.is_host}
                      title={`Kick ${player.name}`}
                      aria-label={`Kick ${player.name}`}
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

      <ConfirmModal
        isOpen={confirmDisband}
        onClose={() => setConfirmDisband(false)}
        onConfirm={() => void handleDisbandLobby()}
        title="Disband lobby?"
        message="This removes the lobby listing from the server and disconnects every player in it."
        danger
        confirmText="Disband"
      />
      <ConfirmModal
        isOpen={kickTarget !== null}
        onClose={() => setKickTarget(null)}
        onConfirm={() => {
          const name = kickTarget;
          if (name) void handleKickPlayer(name);
        }}
        title="Kick player?"
        message={`Kick ${kickTarget ?? ""} from this lobby? They can rejoin if they have the code.`}
        danger
        confirmText="Kick"
      />
    </div>
  );
}
