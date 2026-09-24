import { useState, useEffect, useCallback, useRef } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { Card, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Input } from "@/components/ui/input";
import { EmptyState } from "@/components/ui/empty-state";
import { PlayerRow } from "@/components/ui/player-row";
import { Skeleton } from "@/components/ui/skeleton";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Tooltip } from "@/components/ui/tooltip";
import { PageHeader } from "@/components/ui/page-header";
import { SectionHeader } from "@/components/ui/section-header";
import { showToast, formatError } from "@/components/Toast";
import { Users, Copy, Check } from "lucide-react";

interface Player {
  name: string;
  color: string;
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
// src-tauri/src/ipc_handler.rs — `JoinResult { success, error, code }`).
// `code` is additive: it identifies which join request the result answers.
interface JoinResultPayload {
  success?: boolean;
  error?: string | null;
  code?: string | null;
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
  const [joining, setJoining] = useState(false);
  const [copied, setCopied] = useState(false);
  // Code awaiting confirmation because the user is already in a lobby.
  const [confirmJoinCode, setConfirmJoinCode] = useState<string | null>(null);
  // Code of the most recent join request — used to attribute a
  // `join_lobby_result` that (for safety) omits its own `code`.
  const lastSubmittedCode = useRef<string | null>(null);
  // The join request we are currently awaiting a `join_lobby_result` for.
  // `join_lobby` returns immediately, so THIS — not `joining` — is the real
  // in-flight lock; it is cleared by the matching result (or the timeout).
  const pendingJoinCode = useRef<string | null>(null);
  // Timeout handle that drops `pendingJoinCode` if no result ever arrives.
  const pendingTimeout = useRef<number | null>(null);
  // Deep-link codes must auto-submit at most once each.
  const autoJoinHandled = useRef<string | null>(null);

  const clearPendingJoin = useCallback(() => {
    if (pendingTimeout.current !== null) {
      window.clearTimeout(pendingTimeout.current);
      pendingTimeout.current = null;
    }
    pendingJoinCode.current = null;
  }, []);

  const armPendingJoin = useCallback((code: string) => {
    if (pendingTimeout.current !== null) {
      window.clearTimeout(pendingTimeout.current);
    }
    pendingJoinCode.current = code;
    pendingTimeout.current = window.setTimeout(() => {
      // No result arrived in time — drop the lock (and the working
      // indicator) so a retry is possible instead of wedging forever.
      if (pendingJoinCode.current === code) {
        pendingJoinCode.current = null;
        setJoining(false);
      }
      pendingTimeout.current = null;
    }, 30000);
  }, []);

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
      case "lobby_closed":
        // The mod now reports a real lobby closure. Drop the code and roster
        // so the page falls back to the join input.
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
        // previously invisible. Payload: { success, error, code }.
        const result = payload as JoinResultPayload;
        const resultCode = (result?.code ?? "").trim().toUpperCase();
        const pending = pendingJoinCode.current;
        // Attribute the result to the request it belongs to: a result for a
        // superseded request (e.g. A finishing after the user queued B) must
        // NOT clear the newer pending request or flip the UI. When `code` is
        // absent, fall back to the latest submitted code.
        if (resultCode && (!pending || resultCode !== pending)) {
          break;
        }
        clearPendingJoin();
        setJoining(false);
        const code =
          resultCode || (lastSubmittedCode.current ?? "").trim().toUpperCase();
        if (result?.success) {
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
  }, [clearPendingJoin]);

  useEffect(() => {
    const unlistenMessage = listen<IpcEnvelope>("ipc:message", handleIpcMessage);
    // Connection state itself comes from the `connected` prop (App level);
    // this listener just clears stale lobby data immediately on disconnect
    // (App will unmount this route right after).
    const unlistenDisconnected = listen("ipc:client-disconnected", () => {
      clearPendingJoin();
      setJoining(false);
      setActiveLobbyCode(null);
      setPlayers([]);
    });

    return () => {
      unlistenMessage.then((fn) => fn());
      unlistenDisconnected.then((fn) => fn());
    };
  }, [handleIpcMessage, clearPendingJoin]);

  // Seed the current code on mount: a guest already in a lobby before this
  // page opened has no `activeLobbyCode` from events we missed (mirrors how
  // HostControlPanelView seeds from the same snapshot). Best-effort.
  useEffect(() => {
    let cancelled = false;
    invoke<{ code: string | null }>("get_lobby_state")
      .then((snapshot) => {
        if (!cancelled && snapshot?.code) {
          setActiveLobbyCode(snapshot.code);
        }
      })
      .catch(() => {
        // Snapshot unavailable — the listeners still keep us in sync.
      });
    return () => {
      cancelled = true;
    };
  }, []);

  // Drop any in-flight join bookkeeping on unmount.
  useEffect(() => () => clearPendingJoin(), [clearPendingJoin]);

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

  /**
   * Shared join funnel. `skipConfirm` is used by the confirm dialog: the user
   * has already accepted the "join another lobby" consequence, so we re-run
   * the same guards (connected / already-in-that-lobby) but do not re-open the
   * confirm dialog.
   */
  async function attemptJoin(code: string, skipConfirm: boolean) {
    // Gate on `connected`: the backend now errors honestly with
    // "Not connected" instead of silently queueing the message.
    if (!code || !connected) return;
    // Short-circuit: already in exactly this lobby — inform, no IPC sent.
    if (activeLobbyCode && activeLobbyCode.trim().toUpperCase() === code) {
      showToast(`Already in lobby ${code}`, "neutral");
      return;
    }
    // Guard rail: joining another lobby kicks you from the current one, so
    // confirm first. This is the single funnel for manual join, Enter, deep
    // links, pending-link recovery, and the confirm dialog.
    if (inLobby && !skipConfirm) {
      setConfirmJoinCode(code);
      return;
    }
    await performJoin(code);
  }

  async function submitJoin(rawCode: string) {
    await attemptJoin(rawCode.trim().toUpperCase(), false);
  }

  async function performJoin(rawCode: string) {
    const code = rawCode.trim().toUpperCase();
    if (!code) return;

    // Duplicate guard: the Join button is no longer gated on `joining`, so a
    // second click on the SAME code must not enqueue another join. The
    // supersede path below only cancels when `prev !== code`, so without this
    // an identical code would start a wasted duplicate cycle. Ignore it
    // silently — the button already reads "Joining..." and the roster shows
    // skeletons, so a toast here would just be noise.
    if (pendingJoinCode.current === code) return;

    lastSubmittedCode.current = code;

    // Serialize requests: if a previous join is still in flight, ask the mod
    // to cancel it and WAIT for the cancellation to finish before dispatching
    // the next one. The await is deliberate — skipping it can crash the game.
    const prev = pendingJoinCode.current;
    if (prev && prev !== code) {
      try {
        await invoke("cancel_join", { code: prev });
      } catch (e) {
        console.error("Failed to cancel pending join:", e);
      }
    }

    // `join_lobby` returns as soon as the mod enqueues the request; the real
    // outcome arrives later via the `join_lobby_result` IPC message, so the
    // pending lock is armed here and cleared by that result (or the timeout).
    armPendingJoin(code);
    setJoining(true);
    try {
      await invoke("join_lobby", { code });
    } catch (e) {
      // The invoke itself failed, so no result message is coming.
      clearPendingJoin();
      setJoining(false);
      showToast(`Failed to join lobby: ${formatError(e)}`, "error");
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

  async function copyInviteLink() {
    if (!activeLobbyCode) return;
    // The app's accepted deep-link shape (see src-tauri/src/lib.rs
    // `parse_deep_link`): amonglauncher://join?code=<CODE>.
    const link = `amonglauncher://join?code=${activeLobbyCode}`;
    try {
      await navigator.clipboard.writeText(link);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch (e) {
      console.error("Failed to copy invite link:", e);
      showToast("Failed to copy invite link", "error");
    }
  }

  return (
    <div className="min-h-full space-y-6 pb-6">
      <PageHeader
        title="In Game"
        actions={
          <Badge
            variant={connected ? "success" : "danger"}
            showDot
            dotColor={connected ? "success" : "danger"}
          >
            {connected ? "Game Connected" : "Game Disconnected"}
          </Badge>
        }
      />

      <div className="space-y-6 px-6">
        {/* Joining is the whole point of this page: the code input and the
            Join button own the hero instead of sharing a row. */}
        <Card className="w-full">
          <CardContent className="pt-6">
            {activeLobbyCode ? (
              <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
                <div className="flex items-center gap-3 rounded-control border border-border bg-surface-2 px-4 py-2.5">
                  <span className="text-13 text-muted-foreground">Current Code</span>
                  <span className="font-mono text-xl font-bold tracking-widest text-primary">
                    {activeLobbyCode}
                  </span>
                  <Tooltip content={copied ? "Copied" : "Copy invite link"}>
                    <Button
                      variant="ghost"
                      size="icon"
                      onClick={() => void copyInviteLink()}
                      title={copied ? "Copied" : "Copy invite link"}
                      aria-label="Copy invite link"
                    >
                      {copied ? (
                        <Check className="h-4 w-4 text-success" />
                      ) : (
                        <Copy className="h-4 w-4" />
                      )}
                    </Button>
                  </Tooltip>
                </div>
                <Button
                  onClick={() => void leaveLobby()}
                  variant="outline"
                  size="sm"
                  className="self-start border-danger/40 text-danger hover:bg-danger/10 hover:text-danger sm:self-auto"
                >
                  Leave Lobby
                </Button>
              </div>
            ) : (
              <div className="space-y-3">
                <p className="text-13 text-muted-foreground">
                  Enter a lobby code to join your friends.
                </p>
                <Input
                  value={lobbyCode}
                  onChange={(e) => setLobbyCode(e.target.value)}
                  placeholder="Enter lobby code"
                  className="h-12 w-full text-center font-mono text-xl uppercase tracking-widest"
                  maxLength={6}
                  onKeyDown={(e) => e.key === "Enter" && joinLobby()}
                  aria-label="Lobby code"
                />
                <Button
                  variant="primary"
                  size="lg"
                  className="w-full"
                  onClick={() => void joinLobby()}
                  disabled={!lobbyCode.trim() || !connected}
                  title={connected ? "Join lobby" : "Connect to the game first"}
                >
                  {joining ? "Joining..." : "Join"}
                </Button>
              </div>
            )}
          </CardContent>
        </Card>

        <section className="space-y-3">
          <SectionHeader title="Players" count={players.length} />
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
        </section>
      </div>

      {/* Guard rail: joining while already in a lobby kicks you from the
          current one, so make the consequence explicit before proceeding.
          Non-danger styling — this is a warning, not a destructive action. */}
      <ConfirmDialog
        isOpen={confirmJoinCode !== null}
        onClose={() => setConfirmJoinCode(null)}
        onConfirm={() => {
          const code = confirmJoinCode;
          setConfirmJoinCode(null);
          // Same funnel/guards as submitJoin, but skip the (already-accepted)
          // confirm branch so "Join anyway" actually joins.
          if (code) void attemptJoin(code.trim().toUpperCase(), true);
        }}
        title="Join another lobby?"
        message="You're already in a lobby. Joining another will kick you from your current lobby."
        confirmText="Join anyway"
      />
    </div>
  );
}
