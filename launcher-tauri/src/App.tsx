import { useState, useEffect } from "react";
import { Routes, Route } from "react-router-dom";
import { listen } from "@tauri-apps/api/event";
import HomeView from "@/pages/HomeView";
import SettingsView from "@/pages/SettingsView";
import InGameView from "@/pages/InGameView";
import { Titlebar } from "./components/Titlebar";

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
        {gameConnected ? (
          <InGameView />
        ) : (
          <Routes>
            <Route path="/" element={<HomeView />} />
            <Route path="/settings" element={<SettingsView />} />
          </Routes>
        )}
      </div>
    </div>
  );
}
