import { useState, useEffect, useRef, type ReactNode } from "react";
import {
  Routes,
  Route,
  useNavigate,
  useLocation,
  Navigate,
} from "react-router-dom";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { AlertTriangle } from "lucide-react";
import HomeView from "@/pages/HomeView";
import LibraryView from "@/pages/LibraryView";
import SettingsView from "@/pages/SettingsView";
import InGameView from "@/pages/InGameView";
import HostControlPanelView from "@/pages/HostControlPanelView";
import WelcomeView from "@/pages/WelcomeView";
import SetupView from "@/pages/SetupView";
import { Titlebar } from "./components/Titlebar";
import { Sidebar } from "./components/Sidebar";
import { ErrorBoundary } from "./components/ErrorBoundary";
import { UpdateModal } from "./components/UpdateModal";
import {
  LauncherUpdateModal,
  type LauncherUpdateInfo,
} from "./components/LauncherUpdateModal";
import { ToastHost, showToast, formatError } from "./components/Toast";
import { Skeleton } from "./components/ui/skeleton";
import {
  LauncherProvider,
  useLauncher,
  type UserInfo,
} from "./state/LauncherContext";
import { onDeepLinkJoin } from "./state/deepLink";

interface UpdateInfo {
  current: string;
  latest: string;
  changelog: string;
  download_url: string;
}

interface InstallStatus {
  bepinex_installed: boolean;
  among_api_installed: boolean;
}

/** AmongApi version status, as returned by the `get_mod_status` command. */
export type ModStatusCode =
  | "missing"
  | "outdated"
  | "current"
  | "incompatible"
  | "unknown";

export interface ModStatus {
  status: ModStatusCode;
  installedVersion: string | null;
  latestVersion: string | null;
  downloadUrl: string | null;
  notes: string | null;
}

// `unknown` = the check couldn't run (offline / no releases). Used whenever
// the command times out or rejects, so the UI never falsely claims "up to date".
const UNKNOWN_MOD_STATUS: ModStatus = {
  status: "unknown",
  installedVersion: null,
  latestVersion: null,
  downloadUrl: null,
  notes: null,
};

const EMPTY_UPDATE_INFO: UpdateInfo = {
  current: "Unknown",
  latest: "Unknown",
  changelog: "",
  download_url: "",
};

const INCOMPATIBLE_REASON =
  "The installed AmongApi is incompatible with this launcher version. Update it to continue.";

function SetupPage() {
  const navigate = useNavigate();
  return <SetupView onComplete={() => navigate("/")} />;
}

export default function App() {
  return (
    <LauncherProvider>
      <AppShell />
    </LauncherProvider>
  );
}

function AppShell() {
  const { config, loggedIn, username, avatarUrl, login, refreshNonce, bumpRefresh } =
    useLauncher();
  const navigate = useNavigate();
  const location = useLocation();

  const [gameConnected, setGameConnected] = useState(false);
  const [needsSetup, setNeedsSetup] = useState<boolean | null>(null);
  const [pendingJoinCode, setPendingJoinCode] = useState<string | null>(null);
  // Sidebar presentation only: `false` = expanded (icon + label). Never affects
  // routing/behavior. Persisted so the chosen width survives a reload.
  const [navCollapsed, setNavCollapsed] = useState(() => {
    try {
      return localStorage.getItem("among-launcher.sidebar-collapsed") === "1";
    } catch {
      return false;
    }
  });

  function toggleNavCollapsed() {
    setNavCollapsed((c) => {
      const next = !c;
      try {
        localStorage.setItem("among-launcher.sidebar-collapsed", next ? "1" : "0");
      } catch {
        // Persistence is best-effort; presentation state still applies.
      }
      return next;
    });
  }
  // App-level lobby membership, sourced from the backend's lobby lifecycle
  // events: `null` = not in a lobby; `true`/`false` = in a lobby as
  // host/guest. Owned here (not in a page) so navigation can react to it and
  // InGameView can guard re-joins even before its own ipc listeners mount.
  const [lobbyIsHost, setLobbyIsHost] = useState<boolean | null>(null);
  const [showUpdateModal, setShowUpdateModal] = useState(false);
  const [updateInfo, setUpdateInfo] = useState<UpdateInfo | null>(null);
  // Mandatory-mod-update flow: `force` makes the prompt non-dismissable and
  // `reason` carries extra context (e.g. the `mod-incompatible` event's reason).
  const [modForce, setModForce] = useState(false);
  const [modReason, setModReason] = useState<string | null>(null);
  // Latest known AmongApi status. `null` = not checked yet; `unknown` = the
  // check couldn't run and MUST NOT be rendered as "up to date".
  const [modStatus, setModStatus] = useState<ModStatus | null>(null);
  const modStatusChecked = useRef(false);
  // Launcher self-update state — deliberately separate from the mod-update
  // state above so neither check can interfere with the other.
  const [launcherUpdate, setLauncherUpdate] =
    useState<LauncherUpdateInfo | null>(null);
  const [showLauncherModal, setShowLauncherModal] = useState(false);
  // Settled = launcher check finished (update found, none, failed, or timed
  // out). The AmongApi prompt waits on this so the launcher prompt always
  // goes first when both are detected.
  const [launcherCheckSettled, setLauncherCheckSettled] = useState(false);
  const launcherUpdateChecked = useRef(false);
  // Tracks the previous `gameConnected` value so the pending-deep-link
  // recovery fetch fires exactly on the false→true transition.
  const prevGameConnected = useRef(false);
  // Latest path, mirrored into a ref so the lobby-closed listener (registered
  // once) can tell whether the user is currently on /host without having to
  // re-subscribe on every navigation.
  const locationRef = useRef(location.pathname);
  useEffect(() => {
    locationRef.current = location.pathname;
  }, [location.pathname]);

  // `useNavigate()` is NOT a stable identity under <HashRouter> (it is not a
  // data router): the returned function changes on every navigation. Effects
  // that should navigate only on a STATE transition must not list it as a
  // dependency, or they re-fire after every route change — which bounced a
  // host straight back off /ingame and swallowed a deep-link's confirm modal.
  // Mirror it into a ref and depend only on the state that should trigger the
  // navigation.
  const navigateRef = useRef(navigate);
  useEffect(() => {
    navigateRef.current = navigate;
  });

  // IPC connection state — registered here at App level, before any page
  // mounts, so pages can take `gameConnected` as pre-mount truth.
  useEffect(() => {
    const unlistenConnected = listen("ipc:client-connected", () => {
      setGameConnected(true);
    });

    // Defense-in-depth alongside the Rust `lobby-closed`-on-disconnect fix:
    // a host who exits/restarts the game goes through the Rust disconnect
    // path, which clears `lobby_state` but historically did NOT emit
    // `lobby-closed`. Without clearing `lobbyIsHost` here, the next
    // false→true `gameConnected` transition would auto-open a dead /host
    // panel ("No active lobby"). Clearing both edges guarantees the state
    // is gone regardless of which event arrives.
    const unlistenDisconnected = listen("ipc:client-disconnected", () => {
      setGameConnected(false);
      setLobbyIsHost(null);
    });

    return () => {
      unlistenConnected.then((fn) => fn());
      unlistenDisconnected.then((fn) => fn());
    };
  }, []);

  // Deep-link join listener. The underlying `listen("deep-link")` is
  // registered at module import time (state/deepLink.ts) — before React
  // renders and before any await — and buffers events until we subscribe.
  useEffect(() => {
    const unsubscribe = onDeepLinkJoin(handleJoinDeepLink);
    // Mount-time recovery fetch: Rust stores a pending join deep-link
    // BEFORE its delayed (~1500ms) cold-start emit, and that event can be
    // lost if the webview initializes slower than the delay. The command is
    // one-shot (`take` on the Rust side), so this either replays the missed
    // join or resolves null — and feeds the EXACT same path as the event.
    fetchPendingDeepLink();
    return unsubscribe;
  }, []);

  // Second recovery fetch at the moment navigation stops being gated
  // (gameConnected false→true): covers the timing-inversion case where the
  // mount fetch ran before Rust had stored the value. The event handler
  // DRAINS the Rust store when it fires (see `handleJoinDeepLink` below),
  // so by the time this fallback runs the store only ever still holds a
  // link whose event never arrived — it can no longer re-take a stale code
  // that was already handled (the old "harmless" claim was false: the
  // store survives event handling, and this fetch used to pick it up
  // after InGameView's submit-once ref had died with an unmount).
  useEffect(() => {
    if (gameConnected && !prevGameConnected.current) {
      fetchPendingDeepLink();
    }
    prevGameConnected.current = gameConnected;
  }, [gameConnected]);

  // Single shared handling path for join deep-links — called by BOTH the
  // event listener (above) and the two recovery fetches. Feeding the same
  // code twice is harmless: `pendingJoinCode` is a single stored value
  // (same-value overwrite), and InGameView's `autoJoinHandled` ref gates
  // submit-once per code while `onJoinCodeConsumed` clears the value.
  // After setting the code we drain Rust's pending store (fire-and-forget,
  // result discarded): the event path is store-THEN-emit, so without the
  // drain the store outlives its own event and a later gameConnected
  // transition-fetch could re-take the stale code after InGameView
  // unmounted. When this handler was triggered BY a fetch the store was
  // already emptied by that fetch's `take`, so this resolves null —
  // harmless either way.
  function handleJoinDeepLink(code: string) {
    setPendingJoinCode(code);
    invoke("get_pending_deep_link")
      .then(() => {})
      .catch(() => {});
  }

  // One-shot recovery fetch. Always `.catch`-safe: a missing command or a
  // rejection here just means "nothing pending" (background recovery — log
  // quietly, never toast, never crash).
  function fetchPendingDeepLink() {
    invoke<{ kind: string; code: string } | null>("get_pending_deep_link")
      .then((payload) => {
        if (
          payload &&
          payload.kind === "join" &&
          typeof payload.code === "string" &&
          payload.code.length > 0
        ) {
          handleJoinDeepLink(payload.code);
        }
      })
      .catch((e) => {
        console.debug("get_pending_deep_link failed (nothing pending)", e);
      });
  }

  // The /ingame route only exists while the game pipe is up, so defer
  // navigation until connected (otherwise the catch-all redirects home).
  // Keyed on the code/connection transition only — `navigateRef` keeps this
  // from re-firing on every unrelated route change.
  useEffect(() => {
    if (pendingJoinCode && gameConnected) {
      navigateRef.current("/ingame");
    }
  }, [pendingJoinCode, gameConnected]);

  // Lobby lifecycle listeners, registered at App level (before any page can
  // mount) so lobby membership is known app-wide. `lobby-created` carries
  // `isHost` (true when THIS machine created the lobby, false for a guest);
  // `lobby-closed` clears membership and, if the host is looking at the Host
  // Panel, drops them back to the in-game view (the panel is meaningless
  // without a lobby). Registered exactly ONCE (empty deps): `navigate` goes
  // through `navigateRef` so an unstable router identity can't re-subscribe
  // the listeners on every navigation.
  useEffect(() => {
    const unlistenLobbyCreated = listen<{ isHost?: boolean } | null>(
      "lobby-created",
      (event) => {
        setLobbyIsHost(Boolean(event.payload?.isHost));
      }
    );

    const unlistenLobbyClosed = listen<{ isHost?: boolean } | null>(
      "lobby-closed",
      () => {
        setLobbyIsHost(null);
        if (locationRef.current === "/host") {
          navigateRef.current("/ingame");
        }
      }
    );

    return () => {
      unlistenLobbyCreated.then((fn) => fn());
      unlistenLobbyClosed.then((fn) => fn());
    };
  }, []);

  // Hosts auto-open the Host Panel. Deferred until `gameConnected` because
  // the /host route only exists while the pipe is up — same reason the
  // /ingame navigation above is gated. Guests (lobbyIsHost === false) are
  // deliberately NOT redirected: they stay on InGameView.
  //
  // ONE-SHOT per lobby transition, guarded by `autoOpenedForLobby` rather
  // than by the dependency array. Listing the unstable `navigate` re-fired
  // this on every navigation, so a host could never stay on /ingame and a
  // deep-link confirm modal was bounced away immediately after mounting.
  // Listing `pendingJoinCode` re-introduced that same class of bug a second
  // way: InGameView CONSUMES the code on prefill (clearing it to null), and
  // that null transition re-ran this effect — with `lobbyIsHost === true` and
  // no pending code it navigated /host, unmounting InGameView and destroying
  // the just-opened confirm modal (silently swallowing the join). So
  // `pendingJoinCode` is read here at transition time but is NOT a dependency;
  // the ref fires exactly once per lobby and only resets when the lobby closes.
  //
  // The `!pendingJoinCode` guard resolves a same-commit race: if a deep-link
  // join lands in the exact commit that `lobbyIsHost` flips true, the
  // pending-join effect above (declared earlier) navigates /ingame and this
  // effect (declared later) would then navigate /host — last-write-wins,
  // dropping the confirm modal. Deferring to the pending join lets /ingame
  // win. A normal host lobby creation has a null code, so the auto-open
  // still fires.
  const autoOpenedForLobby = useRef(false);
  useEffect(() => {
    if (lobbyIsHost !== true) {
      autoOpenedForLobby.current = false; // reset for the next lobby
      return;
    }
    if (!gameConnected || autoOpenedForLobby.current) return;
    autoOpenedForLobby.current = true;
    // Read (not depend on) pendingJoinCode: consuming it must NOT re-fire.
    // If a join is pending at the transition, defer to it (the deep-link wins).
    if (!pendingJoinCode) navigateRef.current("/host");
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [lobbyIsHost, gameConnected]);

  // AmongApi status check runs once, after login, in the background AFTER
  // first paint so a slow network (GitHub) can't block login/render.
  useEffect(() => {
    if (!loggedIn || modStatusChecked.current) return;
    modStatusChecked.current = true;
    const timer = setTimeout(() => {
      void checkModStatus();
    }, 0);
    return () => clearTimeout(timer);
    // Intentionally keyed only on `loggedIn`: a one-shot background check.
  }, [loggedIn]);

  // Forced prompt when the in-game mod reports a protocol version this
  // launcher doesn't expect. Registered once at App level so it fires
  // regardless of which route is mounted.
  useEffect(() => {
    const unlisten = listen<{ reason?: string } | null>(
      "mod-incompatible",
      (event) => {
        const reason =
          typeof event.payload?.reason === "string" &&
          event.payload.reason.trim().length > 0
            ? event.payload.reason
            : INCOMPATIBLE_REASON;
        void handleModIncompatible(reason);
      }
    );
    return () => {
      unlisten.then((fn) => fn());
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Shared refresh signal: any surface that mutated backend state (UpdateModal
  // finishing an AmongApi update, an install completing) bumps
  // `refreshNonce`. Re-check the mod status so Home's version row and Play
  // gate reflect reality without waiting for a modal to close. The ref skips
  // the initial value so this only fires on an actual bump.
  const handledRefreshNonce = useRef(0);
  useEffect(() => {
    if (refreshNonce === handledRefreshNonce.current) return;
    handledRefreshNonce.current = refreshNonce;
    void refreshModStatus();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [refreshNonce]);

  // A completed install changes installed mods / config backend-side. Bump the
  // shared signal so any mounted page re-fetches. `update_among_api` reports on
  // its own `update-progress` event (UpdateModal bumps on success), so this
  // deliberately does not duplicate that.
  useEffect(() => {
    const unlisten = listen<{ stage?: string } | null>(
      "install-progress",
      (event) => {
        if (event.payload?.stage === "complete") bumpRefresh();
      }
    );
    return () => {
      unlisten.then((fn) => fn());
    };
  }, [bumpRefresh]);

  // Launcher self-update check: runs once at mount REGARDLESS of login,
  // fully independent of the mod check above (own ref, own effect, own
  // try/catch) — one rejecting can never skip or abort the other. The ref
  // guard lives inside the timer so StrictMode's double effect-mount still
  // fires exactly once.
  useEffect(() => {
    const timer = setTimeout(() => {
      if (launcherUpdateChecked.current) return;
      launcherUpdateChecked.current = true;
      void checkForLauncherUpdate();
    }, 0);
    return () => clearTimeout(timer);
    // One-shot background check, post-first-paint so a slow network can't
    // block render.
  }, []);

  const moddedPath = config?.modded_install_path.trim() ?? "";
  useEffect(() => {
    if (!loggedIn) return;
    void checkSetup();
  }, [loggedIn, moddedPath]);

  async function checkSetup() {
    try {
      const setupRequired = await Promise.race([
        (async () => {
          const path = config?.modded_install_path.trim();
          if (!path) return true;
          const status = await invoke<InstallStatus>("get_install_status", {
            gamePath: path,
          });
          return !(status.bepinex_installed && status.among_api_installed);
        })(),
        new Promise<boolean>((resolve) => setTimeout(() => resolve(true), 3000)),
      ]);
      setNeedsSetup(setupRequired);
    } catch {
      setNeedsSetup(true);
    }
  }

  // Fetch the AmongApi status once. Returns null when the check times out or
  // rejects — callers map that to the non-blocking "unknown" state.
  async function fetchModStatus(): Promise<ModStatus | null> {
    try {
      return await Promise.race([
        invoke<ModStatus>("get_mod_status"),
        new Promise<null>((resolve) => setTimeout(() => resolve(null), 8000)),
      ]);
    } catch (e) {
      // get_mod_status "never throws for the normal cases"; a rejection is an
      // unexpected failure. Never treat it as "up to date".
      console.debug("get_mod_status failed", e);
      return null;
    }
  }

  function openForcedModUpdate(status: ModStatus, reason: string | null) {
    setUpdateInfo({
      current: status.installedVersion ?? "Not installed",
      latest: status.latestVersion ?? "Unknown",
      changelog: status.notes ?? "",
      download_url: status.downloadUrl ?? "",
    });
    setModReason(reason);
    setModForce(true);
    setShowUpdateModal(true);
  }

  // Startup check: opens a NON-DISMISSABLE update prompt when the mod is
  // missing/outdated/incompatible. `unknown` only warns (never blocks, never
  // claims up to date).
  async function checkModStatus() {
    const status = (await fetchModStatus()) ?? UNKNOWN_MOD_STATUS;
    setModStatus(status);
    if (
      status.status === "missing" ||
      status.status === "outdated" ||
      status.status === "incompatible"
    ) {
      openForcedModUpdate(
        status,
        status.status === "incompatible" ? INCOMPATIBLE_REASON : null
      );
    }
  }

  // Refresh WITHOUT prompting — used after the update prompt closes so
  // HomeView's PLAY gate reflects the post-update state without immediately
  // re-opening the modal (which would trap a user who closed it early).
  async function refreshModStatus() {
    const status = (await fetchModStatus()) ?? UNKNOWN_MOD_STATUS;
    setModStatus(status);
    // A successful update (or an externally fixed install) must never leave a
    // stale forced prompt covering the UI. Only CLEAR on `current`; this path
    // never opens the modal, so it cannot loop with the update check.
    if (status.status === "current") {
      setShowUpdateModal(false);
      setModForce(false);
      setModReason(null);
    }
  }

  // `mod-incompatible` is emitted when the in-game mod reports an unexpected
  // protocol version. Always open the forced prompt; a best-effort refresh
  // supplies the download URL but never blocks the prompt.
  async function handleModIncompatible(reason: string) {
    setModReason(reason);
    setModForce(true);
    setUpdateInfo((prev) => prev ?? EMPTY_UPDATE_INFO);
    setShowUpdateModal(true);
    const status = await fetchModStatus();
    if (status) {
      setModStatus(status);
      setUpdateInfo({
        current: status.installedVersion ?? "Unknown",
        latest: status.latestVersion ?? "Unknown",
        changelog: status.notes ?? "",
        download_url: status.downloadUrl ?? "",
      });
    }
  }

  async function checkForLauncherUpdate() {
    try {
      const info = await Promise.race([
        invoke<LauncherUpdateInfo | null>("check_for_launcher_update"),
        new Promise<null>((resolve) => setTimeout(() => resolve(null), 8000)),
      ]);
      if (info) {
        setLauncherUpdate(info);
        setShowLauncherModal(true);
      }
      // Timeout (resolves null) or null (up to date): stay silent.
    } catch (e) {
      // Rejections — network/API failure OR "command not found" while the
      // backend command isn't registered yet — are check failures: toast
      // them, never treat them as "up to date". App stays usable offline.
      const detail = formatError(e);
      showToast(
        detail ? `Update check failed: ${detail}` : "Update check failed",
        "error"
      );
    } finally {
      // Always settle so the queued AmongApi prompt is never blocked.
      setLauncherCheckSettled(true);
    }
  }

  async function handleLogin(user: UserInfo) {
    // Shared context login: sets user info and refreshes persisted creds,
    // so Settings/logout and every config consumer stay in sync.
    await login(user);
  }

  let content: ReactNode;

  if (loggedIn === null || (loggedIn && needsSetup === null)) {
    content = (
      <div className="h-screen flex flex-col bg-background">
        <Titlebar />
        <div className="flex-1 min-h-0 overflow-hidden p-6 animate-page-in">
          <div className="mx-auto w-full max-w-3xl space-y-6">
            <Skeleton className="h-7 w-40" />
            <Skeleton className="h-40 w-full rounded-card" />
            <div className="grid gap-6 md:grid-cols-2">
              <Skeleton className="h-56 w-full rounded-card" />
              <Skeleton className="h-56 w-full rounded-card" />
            </div>
          </div>
        </div>
        {/* ToastHost so the at-mount launcher update check can surface a
            failure even before login finishes rendering. */}
        <ToastHost />
      </div>
    );
  } else if (!loggedIn) {
    content = (
      <div className="h-screen flex flex-col">
        <Titlebar />
        <div className="flex-1 min-h-0 overflow-hidden animate-page-in">
          <WelcomeView onLogin={handleLogin} />
        </div>
        <ToastHost />
      </div>
    );
  } else if (needsSetup) {
    content = (
      <div className="h-screen flex flex-col bg-background text-foreground">
        <Titlebar />
        <div className="flex-1 min-h-0 overflow-hidden animate-page-in">
          <SetupView onComplete={() => setNeedsSetup(false)} />
        </div>
        <ToastHost />
      </div>
    );
  } else {
    content = (
      <div className="h-screen flex flex-col bg-background text-foreground">
        <Titlebar />
        <div className="flex-1 min-h-0 flex overflow-hidden animate-page-in">
          <Sidebar
            gameConnected={gameConnected}
            username={username}
            avatarUrl={avatarUrl}
            lobbyIsHost={lobbyIsHost}
            collapsed={navCollapsed}
            onToggleCollapsed={toggleNavCollapsed}
          />
          {/* Banner lives in a non-scrolling region ABOVE the page scroller so
              it stays visible without being covered by each page's sticky
              `PageHeader` (which sticks to the top of the inner scroller). */}
          <main className="flex-1 min-w-0 flex flex-col overflow-hidden">
            {modStatus?.status === "unknown" && (
              <div className="shrink-0 px-6 pt-4">
                <div className="flex items-start gap-2 rounded-control border border-warning/40 bg-warning/10 px-3 py-2 text-2xs text-foreground">
                  <AlertTriangle
                    className="mt-0.5 h-3.5 w-3.5 shrink-0 text-warning"
                    aria-hidden="true"
                  />
                  <span>
                    Couldn&apos;t verify the AmongApi version. Check your
                    connection — it may need updating.
                  </span>
                </div>
              </div>
            )}
            <div className="flex-1 min-h-0 overflow-y-auto">
              {/* Enter-only page transition: keyed by pathname so a route
                  change replays the fade/rise. No exit animation (keeps nav
                  snappy and never delays a route swap). */}
              <div key={location.pathname} className="h-full animate-page-in">
                <Routes>
                <Route
                  path="/"
                  element={
                    <HomeView
                      modStatus={modStatus}
                      onRequireModUpdate={() => {
                        if (modStatus) {
                          openForcedModUpdate(
                            modStatus,
                            modStatus.status === "incompatible"
                              ? INCOMPATIBLE_REASON
                              : null
                          );
                        } else {
                          void checkModStatus();
                        }
                      }}
                    />
                  }
                />
                <Route path="/library" element={<LibraryView />} />
                <Route path="/settings" element={<SettingsView />} />
                {!gameConnected && <Route path="/setup" element={<SetupPage />} />}
                {gameConnected && (
                  <Route
                    path="/ingame"
                    element={
                      <InGameView
                        connected={gameConnected}
                        inLobby={lobbyIsHost !== null}
                        initialJoinCode={pendingJoinCode}
                        onJoinCodeConsumed={() => setPendingJoinCode(null)}
                      />
                    }
                  />
                )}
                {gameConnected && (
                  <Route path="/host" element={<HostControlPanelView />} />
                )}
                {/* Gated routes that don't exist for the current state (e.g.
                    /ingame while disconnected) fall through to home. */}
                <Route path="*" element={<Navigate to="/" replace />} />
                </Routes>
              </div>
            </div>
          </main>
        </div>

        {updateInfo && (
          <UpdateModal
            // Ordering: the launcher prompt always goes first. The mod
            // prompt is held until the launcher check has settled AND any
            // launcher prompt has been dismissed (Later/Close). A FORCED mod
            // prompt is likewise non-dismissable once it appears.
            isOpen={
              showUpdateModal && launcherCheckSettled && !showLauncherModal
            }
            onClose={() => {
              setShowUpdateModal(false);
              // Reflect the post-update state in HomeView's PLAY gate without
              // re-opening the prompt (see refreshModStatus).
              void refreshModStatus();
            }}
            updateInfo={updateInfo}
            force={modForce}
            reason={modReason}
            onUpdated={() => {
              // The AmongApi update landed: close the (possibly forced) prompt
              // and clear the force flag immediately, then re-check so Home's
              // version row / Play gate are correct without a restart.
              setShowUpdateModal(false);
              setModForce(false);
              setModReason(null);
              void refreshModStatus();
            }}
          />
        )}
        <ToastHost />
      </div>
    );
  }

  // ErrorBoundary wraps the ENTIRE shell (titlebar/sidebar/toasts), so any
  // render error anywhere in the app degrades to the recovery screen.
  // The launcher prompt renders at the top level so it works on ANY screen
  // (including welcome/setup — its check runs regardless of login).
  return (
    <ErrorBoundary>
      {content}
      {launcherUpdate && (
        <LauncherUpdateModal
          isOpen={showLauncherModal}
          onClose={() => setShowLauncherModal(false)}
          updateInfo={launcherUpdate}
        />
      )}
    </ErrorBoundary>
  );
}
