import { useState, useEffect } from "react";
import { Routes, Route } from "react-router-dom";
import { listen } from "@tauri-apps/api/event";
import HomeView from "@/pages/HomeView";
import SettingsView from "@/pages/SettingsView";
import InGameView from "@/pages/InGameView";
import HostControlPanelView from "@/pages/HostControlPanelView";
import { Titlebar } from "./components/Titlebar";
import { Sidebar } from "./components/Sidebar";

export default function App() {
  const [gameConnected, setGameConnected] = useState(false);

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

  return (
    <div className="h-screen flex flex-col bg-background text-foreground">
      <Titlebar />
      <div className="flex-1 flex overflow-hidden">
        <Sidebar gameConnected={gameConnected} />
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
