import { useState, useEffect } from "react";
import { Routes, Route } from "react-router-dom";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import HomeView from "@/pages/HomeView";
import SettingsView from "@/pages/SettingsView";
import InGameView from "@/pages/InGameView";
import HostControlPanelView from "@/pages/HostControlPanelView";
import WelcomeView from "@/pages/WelcomeView";
import { Titlebar } from "./components/Titlebar";
import { Sidebar } from "./components/Sidebar";
import { ErrorBoundary } from "./components/ErrorBoundary";
import { UpdateModal } from "./components/UpdateModal";

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

export default function App() {
  const [gameConnected, setGameConnected] = useState(false);
  const [loggedIn, setLoggedIn] = useState<boolean | null>(null);
  const [username, setUsername] = useState("");
  const [avatarUrl, setAvatarUrl] = useState("");
  const [showUpdateModal, setShowUpdateModal] = useState(false);
  const [updateInfo, setUpdateInfo] = useState<UpdateInfo | null>(null);

  useEffect(() => {
    checkAuth();
  }, []);

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
      const config = await invoke<LauncherConfig>("read_config");
      const hasToken = config.discord_access_token.length > 0;
      setLoggedIn(hasToken);
      if (hasToken) {
        setUsername(config.username);
        setAvatarUrl(config.avatar_url);
      }

      if (hasToken) {
        checkForUpdate(config);
      }
    } catch {
      setLoggedIn(false);
    }
  }

  async function checkForUpdate(_config: LauncherConfig) {
    try {
      const info = await invoke<UpdateInfo | null>("check_for_among_api_update");
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
      <div className="h-screen flex items-center justify-center bg-background">
        <div className="text-muted-foreground text-sm">Loading...</div>
      </div>
    );
  }

  if (!loggedIn) {
    return (
      <>
        <Titlebar />
        <WelcomeView onLogin={handleLogin} />
      </>
    );
  }

  return (
    <div className="h-screen flex flex-col bg-background text-foreground">
      <Titlebar />
      <div className="flex-1 flex overflow-hidden">
        <Sidebar gameConnected={gameConnected} username={username} avatarUrl={avatarUrl} />
        <main className="flex-1 overflow-y-auto p-6">
          <ErrorBoundary>
            <Routes>
              <Route path="/" element={<HomeView />} />
              <Route path="/settings" element={<SettingsView />} />
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
    </div>
  );
}
