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

interface LauncherConfig {
  storefront?: string | null;
  modded_install_path: string;
  debug_mode: boolean;
  auto_post_lobby: boolean;
  discord_access_token: string;
  username: string;
  avatar_url: string;
}

interface UserInfo {
  id: string;
  username: string;
  global_name: string | null;
  avatar: string | null;
}

export default function App() {
  const [gameConnected, setGameConnected] = useState(false);
  const [loggedIn, setLoggedIn] = useState<boolean | null>(null);
  const [username, setUsername] = useState("");
  const [avatarUrl, setAvatarUrl] = useState("");

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
    } catch {
      setLoggedIn(false);
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
          <Routes>
            <Route path="/" element={<HomeView />} />
            <Route path="/settings" element={<SettingsView />} />
            {gameConnected && <Route path="/ingame" element={<InGameView />} />}
            {gameConnected && <Route path="/host" element={<HostControlPanelView />} />}
          </Routes>
        </main>
      </div>
    </div>
  );
}
