import { useState, useEffect } from "react";
import { invoke } from "@tauri-apps/api/core";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { CardContainer, CardBody, CardItem } from "@/components/ui/3d-card";
import { Play, Square, RefreshCw, Gamepad2, CheckCircle2, XCircle } from "lucide-react";

interface GameStatus {
  installed: boolean;
  storefront: string | null;
  path: string | null;
}

interface Mod {
  name: string;
  version: string;
  enabled: boolean;
}

export default function HomeView() {
  const [gameStatus, setGameStatus] = useState<GameStatus | null>(null);
  const [mods, setMods] = useState<Mod[]>([]);
  const [isRunning, setIsRunning] = useState(false);
  const [autoPost, setAutoPost] = useState(false);
  const [debugMode, setDebugMode] = useState(false);

  useEffect(() => {
    loadGameStatus();
    loadMods();
  }, []);

  async function loadGameStatus() {
    try {
      const status = await invoke<GameStatus>("get_game_status");
      setGameStatus(status);
    } catch (e) {
      console.error("Failed to get game status:", e);
    }
  }

  async function loadMods() {
    try {
      const installedMods = await invoke<Mod[]>("get_mods");
      setMods(installedMods);
    } catch (e) {
      console.error("Failed to get mods:", e);
    }
  }

  async function launchGame() {
    try {
      await invoke("launch_game");
      setIsRunning(true);
    } catch (e) {
      console.error("Failed to launch game:", e);
    }
  }

  async function stopGame() {
    try {
      await invoke("stop_game");
      setIsRunning(false);
    } catch (e) {
      console.error("Failed to stop game:", e);
    }
  }

  return (
    <div className="min-h-full bg-grid p-6 space-y-6">
      <h1 className="font-display text-3xl font-bold text-primary">Home</h1>

      <div className="grid gap-6 md:grid-cols-2">
        <CardContainer className="w-full">
          <CardBody>
            <CardItem translateZ={20}>
              <Card className="w-full glow-primary">
                <CardHeader>
                  <CardTitle className="flex items-center gap-2">
                    <Gamepad2 className="h-5 w-5" />
                    Game Status
                  </CardTitle>
                </CardHeader>
                <CardContent className="space-y-4">
                  <div className="flex items-center justify-between">
                    <span className="text-sm text-muted-foreground">Status</span>
                    <Badge variant={gameStatus?.installed ? "default" : "outline"}>
                      {gameStatus?.installed ? (
                        <span className="flex items-center gap-1">
                          <CheckCircle2 className="h-3 w-3" /> Installed
                        </span>
                      ) : (
                        <span className="flex items-center gap-1">
                          <XCircle className="h-3 w-3 text-destructive" /> Not Installed
                        </span>
                      )}
                    </Badge>
                  </div>
                  {gameStatus?.storefront && (
                    <div className="flex items-center justify-between">
                      <span className="text-sm text-muted-foreground">Storefront</span>
                      <span className="text-sm font-medium">{gameStatus.storefront}</span>
                    </div>
                  )}
                  {gameStatus?.path && (
                    <div className="flex items-center justify-between">
                      <span className="text-sm text-muted-foreground">Path</span>
                      <span className="text-xs font-mono text-muted-foreground truncate max-w-[200px]">
                        {gameStatus.path}
                      </span>
                    </div>
                  )}
                  <div className="flex gap-2 pt-2">
                    <Button
                      onClick={launchGame}
                      disabled={!gameStatus?.installed || isRunning}
                      className="flex-1"
                    >
                      <Play className="h-4 w-4" />
                      Launch
                    </Button>
                    <Button
                      onClick={stopGame}
                      disabled={!isRunning}
                      variant="destructive"
                      className="flex-1"
                    >
                      <Square className="h-4 w-4" />
                      Stop
                    </Button>
                  </div>
                </CardContent>
              </Card>
            </CardItem>
          </CardBody>
        </CardContainer>

        <CardContainer className="w-full">
          <CardBody>
            <CardItem translateZ={20}>
              <Card className="w-full glow-sky">
                <CardHeader>
                  <CardTitle className="flex items-center gap-2">
                    <RefreshCw className="h-5 w-5" />
                    Installed Mods
                  </CardTitle>
                </CardHeader>
                <CardContent>
                  {mods.length === 0 ? (
                    <p className="text-sm text-muted-foreground">No mods installed.</p>
                  ) : (
                    <ul className="space-y-2">
                      {mods.map((mod) => (
                        <li
                          key={mod.name}
                          className="flex items-center justify-between rounded-lg bg-secondary/50 px-3 py-2"
                        >
                          <div>
                            <span className="text-sm font-medium">{mod.name}</span>
                            <span className="ml-2 text-xs text-muted-foreground">v{mod.version}</span>
                          </div>
                          <Badge variant={mod.enabled ? "default" : "muted"}>
                            {mod.enabled ? "Enabled" : "Disabled"}
                          </Badge>
                        </li>
                      ))}
                    </ul>
                  )}
                </CardContent>
              </Card>
            </CardItem>
          </CardBody>
        </CardContainer>
      </div>

      <Card className="glow-violet">
        <CardHeader>
          <CardTitle>Options</CardTitle>
        </CardHeader>
        <CardContent>
          <div className="space-y-4">
            <label className="flex items-center justify-between cursor-pointer">
              <span className="text-sm font-medium">Auto-post game data</span>
              <button
                onClick={() => setAutoPost(!autoPost)}
                className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${
                  autoPost ? "bg-primary" : "bg-muted"
                }`}
              >
                <span
                  className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${
                    autoPost ? "translate-x-6" : "translate-x-1"
                  }`}
                />
              </button>
            </label>
            <label className="flex items-center justify-between cursor-pointer">
              <span className="text-sm font-medium">Debug mode</span>
              <button
                onClick={() => setDebugMode(!debugMode)}
                className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${
                  debugMode ? "bg-primary" : "bg-muted"
                }`}
              >
                <span
                  className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${
                    debugMode ? "translate-x-6" : "translate-x-1"
                  }`}
                />
              </button>
            </label>
          </div>
        </CardContent>
      </Card>
    </div>
  );
}
