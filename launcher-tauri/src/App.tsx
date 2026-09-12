import { useState, useEffect } from "react";
import { Routes, Route, useNavigate } from "react-router-dom";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import HomeView from "@/pages/HomeView";
import SettingsView from "@/pages/SettingsView";
import InGameView from "@/pages/InGameView";
import HostControlPanelView from "@/pages/HostControlPanelView";
import WelcomeView from "@/pages/WelcomeView";
import SetupView from "@/pages/SetupView";
import { Titlebar } from "./components/Titlebar";
import { Sidebar } from "./components/Sidebar";
import { ErrorBoundary } from "./components/ErrorBoundary";
import { UpdateModal } from "./components/UpdateModal";
import { ToastHost } from "./components/Toast";

interface LauncherConfig {
  storefront?: string | null;
  modded_install_path: string;
  debug_mode: boolean;
  auto_post_lobby: boolean;
  discord_access_token: string;
  username: string;
  avatar_url: string;
  last_seen_version: string;
}

interface UserInfo {
  id: string;
  username: string;
  global_name: string | null;
  avatar: string | null;
}

interface UpdateInfo {
  current: string;
  latest: string;
  changelog: string;
  download_url: string;
}

interface GameSearchResult {
  path?: string | null;
  storefront?: string | null;
  detected_but_unavailable?: boolean;
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
  const [gameConnected, setGameConnected] = useState(false);
  const [loggedIn, setLoggedIn] = useState<boolean | null>(null);
  const [needsSetup, setNeedsSetup] = useState<boolean | null>(null);
  const [username, setUsername] = useState("");
  const [avatarUrl, setAvatarUrl] = useState("");
  const [showUpdateModal, setShowUpdateModal] = useState(false);
  const [updateInfo, setUpdateInfo] = useState<UpdateInfo | null>(null);

  useEffect(() => {
    checkAuth();
  }, []);

  useEffect(() => {
    if (loggedIn) {
      checkSetup();
    }
  }, [loggedIn]);

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

  async function checkAuth() {
    try {
      // Don't let a slow config read hang first paint.
      const config = await Promise.race([
        invoke<LauncherConfig>("read_config"),
        new Promise<never>((_, reject) =>
          setTimeout(() => reject(new Error("config timeout")), 3000)
        ),
      ]);
      const hasToken = config.discord_access_token.length > 0;
      setLoggedIn(hasToken);
      if (hasToken) {
        setUsername(config.username);
        setAvatarUrl(config.avatar_url);
      }

      if (hasToken) {
        // Run update check in the background AFTER first paint so a slow
        // network (GitHub) can't block login/render.
        setTimeout(() => {
          checkForUpdate();
        }, 0);
      }
    } catch {
      setLoggedIn(false);
    }
  }

  async function checkSetup() {
    try {
      const setupRequired = await Promise.race([
        (async () => {
          const detected = await invoke<GameSearchResult>("detect_game", {});
          if (!detected.path) return true;
          const status = await invoke<InstallStatus>("get_install_status", {
            gamePath: detected.path,
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
    } catch {
      // Version check is non-critical
    }
  }

  function handleLogin(user: UserInfo) {
    setUsername(user.username);
    if (user.avatar) {
      setAvatarUrl(
        `https://cdn.discordapp.com/avatars/${user.id}/${user.avatar}.png`
      );
    }
    setLoggedIn(true);
  }

  if (loggedIn === null) {
    return (
      <div className="h-screen flex flex-col bg-background">
        <Titlebar />
        <div className="flex-1 min-h-0 overflow-hidden flex items-center justify-center">
          <div className="text-muted-foreground text-sm">Loading...</div>
        </div>
      </div>
    );
  }

  if (!loggedIn) {
    return (
      <div className="h-screen flex flex-col">
        <Titlebar />
        <div className="flex-1 min-h-0 overflow-hidden">
          <WelcomeView onLogin={handleLogin} />
        </div>
      </div>
    );
  }

  if (needsSetup === null) {
    return (
      <div className="h-screen flex flex-col bg-background">
        <Titlebar />
        <div className="flex-1 min-h-0 overflow-hidden flex items-center justify-center">
          <div className="text-muted-foreground text-sm">Loading...</div>
        </div>
      </div>
    );
  }

  if (needsSetup) {
    return (
      <div className="h-screen flex flex-col bg-background text-foreground">
        <Titlebar />
        <div className="flex-1 min-h-0 overflow-hidden">
          <SetupView onComplete={() => setNeedsSetup(false)} />
        </div>
        <ToastHost />
      </div>
    );
  }

  return (
    <div className="h-screen flex flex-col bg-background text-foreground">
      <Titlebar />
      <div className="flex-1 min-h-0 flex overflow-hidden">
        <Sidebar gameConnected={gameConnected} username={username} avatarUrl={avatarUrl} />
        <main className="flex-1 overflow-y-auto p-6">
          <ErrorBoundary>
            <Routes>
              <Route path="/" element={<HomeView />} />
              <Route path="/settings" element={<SettingsView />} />
              {!gameConnected && <Route path="/setup" element={<SetupPage />} />}
              {gameConnected && <Route path="/ingame" element={<InGameView />} />}
              {gameConnected && <Route path="/host" element={<HostControlPanelView />} />}
            </Routes>
          </ErrorBoundary>
        </main>
      </div>

      {updateInfo && (
        <UpdateModal
          isOpen={showUpdateModal}
          onClose={() => setShowUpdateModal(false)}
          updateInfo={updateInfo}
        />
      )}
      <ToastHost />
    </div>
  );
}
