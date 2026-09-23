import { useState, useEffect, useRef, type ReactNode } from "react";
import { Routes, Route, useNavigate, Navigate } from "react-router-dom";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
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
  const { config, loggedIn, username, avatarUrl, login } = useLauncher();
  const navigate = useNavigate();

  const [gameConnected, setGameConnected] = useState(false);
  const [needsSetup, setNeedsSetup] = useState<boolean | null>(null);
  const [pendingJoinCode, setPendingJoinCode] = useState<string | null>(null);
  const [showUpdateModal, setShowUpdateModal] = useState(false);
  const [updateInfo, setUpdateInfo] = useState<UpdateInfo | null>(null);
  const updateChecked = useRef(false);
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

  // IPC connection state — registered here at App level, before any page
  // mounts, so pages can take `gameConnected` as pre-mount truth.
  useEffect(() => {
    const unlistenConnected = listen("ipc:client-connected", () => {
      setGameConnected(true);
    });

    const unlistenDisconnected = listen("ipc:client-disconnected", () => {
      setGameConnected(false);
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
  useEffect(() => {
    if (pendingJoinCode && gameConnected) {
      navigate("/ingame");
    }
  }, [pendingJoinCode, gameConnected, navigate]);

  // Update check runs once, after login, in the background AFTER first
  // paint so a slow network (GitHub) can't block login/render.
  useEffect(() => {
    if (!loggedIn || updateChecked.current) return;
    updateChecked.current = true;
    const timer = setTimeout(() => {
      void checkForUpdate();
    }, 0);
    return () => clearTimeout(timer);
    // Intentionally keyed only on `loggedIn`: a one-shot background check.
  }, [loggedIn]);

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

  async function checkForUpdate() {
    try {
      const info = await Promise.race([
        invoke<UpdateInfo | null>("check_for_among_api_update"),
        new Promise<null>((resolve) => setTimeout(() => resolve(null), 8000)),
      ]);
      if (info) {
        setUpdateInfo(info);
        setShowUpdateModal(true);
      }
      // Timeout (resolves null): stay silent — no "up to date" claim is made.
    } catch (e) {
      // Non-critical, but not silent: surface failures so a broken network
      // isn't mistaken for "no update available". App stays usable offline.
      const detail = formatError(e);
      showToast(
        detail ? `Update check failed: ${detail}` : "Update check failed",
        "error"
      );
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
        <div className="flex-1 min-h-0 overflow-hidden flex items-center justify-center">
          <div className="text-muted-foreground text-sm">Loading...</div>
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
        <div className="flex-1 min-h-0 overflow-hidden">
          <WelcomeView onLogin={handleLogin} />
        </div>
        <ToastHost />
      </div>
    );
  } else if (needsSetup) {
    content = (
      <div className="h-screen flex flex-col bg-background text-foreground">
        <Titlebar />
        <div className="flex-1 min-h-0 overflow-hidden">
          <SetupView onComplete={() => setNeedsSetup(false)} />
        </div>
        <ToastHost />
      </div>
    );
  } else {
    content = (
      <div className="h-screen flex flex-col bg-background text-foreground">
        <Titlebar />
        <div className="flex-1 min-h-0 flex overflow-hidden">
          <Sidebar
            gameConnected={gameConnected}
            username={username}
            avatarUrl={avatarUrl}
          />
          <main className="flex-1 overflow-y-auto p-6">
            <Routes>
              <Route path="/" element={<HomeView />} />
              <Route path="/library" element={<LibraryView />} />
              <Route path="/settings" element={<SettingsView />} />
              {!gameConnected && <Route path="/setup" element={<SetupPage />} />}
              {gameConnected && (
                <Route
                  path="/ingame"
                  element={
                    <InGameView
                      connected={gameConnected}
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
          </main>
        </div>

        {updateInfo && (
          <UpdateModal
            // Ordering: the launcher prompt always goes first. The mod
            // prompt is held until the launcher check has settled AND any
            // launcher prompt has been dismissed (Later/Close).
            isOpen={
              showUpdateModal && launcherCheckSettled && !showLauncherModal
            }
            onClose={() => setShowUpdateModal(false)}
            updateInfo={updateInfo}
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
