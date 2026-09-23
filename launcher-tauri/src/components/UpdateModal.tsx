import { useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { Modal } from "@/components/Modal";
import { Button } from "@/components/ui/button";
import { Download } from "lucide-react";
import { formatError } from "@/components/Toast";

interface UpdateInfo {
  current: string;
  latest: string;
  changelog: string;
  download_url: string;
}

interface UpdateModalProps {
  isOpen: boolean;
  onClose: () => void;
  updateInfo: UpdateInfo;
  /**
   * Mandatory update: hides "Later" and disables every dismissal route on the
   * base Modal (Esc, backdrop click, and the header X) via `allowDismiss`.
   */
  force?: boolean;
  /** Extra context (e.g. the mod-incompatible reason) shown above the versions. */
  reason?: string | null;
}

interface UpdateProgress {
  stage: "downloading" | "installing" | "complete";
  progress: number;
  total: number;
}

type Status = "idle" | "updating" | "done" | "error";

// `update_among_api` reports a locked target file as a filesystem error whose
// message contains "Close the game and try again: …". There is no dedicated
// `is_game_running` command on the backend, so that message is the only signal
// available to the frontend.
const GAME_RUNNING_PATTERN = /close the game/i;

export function UpdateModal({
  isOpen,
  onClose,
  updateInfo,
  force = false,
  reason = null,
}: UpdateModalProps) {
  const [status, setStatus] = useState<Status>("idle");
  const [progressText, setProgressText] = useState("");
  const [error, setError] = useState("");

  useEffect(() => {
    if (!isOpen) {
      setStatus("idle");
      setProgressText("");
      setError("");
      return;
    }
    const unlisten = listen<UpdateProgress>("update-progress", (event) => {
      const { stage, progress, total } = event.payload;
      if (stage === "downloading") {
        if (total > 0) {
          const pct = Math.round((progress / total) * 100);
          setProgressText(`Downloading... ${pct}%`);
        } else {
          setProgressText("Downloading...");
        }
      } else if (stage === "installing") {
        setProgressText("Installing...");
      } else if (stage === "complete") {
        setProgressText("Complete");
      }
    });
    return () => {
      unlisten.then((fn) => fn());
    };
  }, [isOpen]);

  async function handleUpdate() {
    if (!updateInfo.download_url || status === "updating") return;
    setStatus("updating");
    setError("");
    setProgressText("Downloading...");
    try {
      await invoke("update_among_api", {
        downloadUrl: updateInfo.download_url,
      });
      setStatus("done");
      setProgressText("");
    } catch (e) {
      setStatus("error");
      setError(formatError(e));
    }
  }

  const updating = status === "updating";
  const gameRunning = status === "error" && GAME_RUNNING_PATTERN.test(error);
  // A forced prompt with no download URL has no action the user can take, so
  // trapping them behind it would brick the launcher. This is the ONLY case a
  // forced prompt may be closed before completing.
  const canCloseEarly = force && !updateInfo.download_url;
  const showSecondaryButton = !force || canCloseEarly;

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={force ? "AmongApi update required" : "Update Available"}
      allowDismiss={!force}
    >
      <div className="space-y-4">
        {reason && (
          <p className="rounded-control bg-surface-2 p-3 text-xs text-muted-foreground">
            {reason}
          </p>
        )}

        <div className="flex items-center justify-between text-sm">
          <span className="text-muted-foreground">Current version</span>
          <span className="font-mono font-medium">{updateInfo.current}</span>
        </div>
        <div className="flex items-center justify-between text-sm">
          <span className="text-muted-foreground">Latest version</span>
          <span className="font-mono font-medium text-primary">
            {updateInfo.latest}
          </span>
        </div>

        {updateInfo.changelog && (
          <div className="max-h-40 overflow-y-auto rounded-control bg-surface-2 p-3">
            <p className="text-xs font-medium mb-2 text-muted-foreground">
              Changelog
            </p>
            <div className="space-y-1">
              {updateInfo.changelog
                .split("\n")
                .filter((l) => l.trim())
                .map((line, i) => (
                  <div key={i} className="flex gap-2 text-xs text-muted-foreground">
                    {line.trim().startsWith("-") || line.trim().startsWith("*") ? (
                      <>
                        <span className="text-primary shrink-0">•</span>
                        <span>{line.trim().replace(/^[-*]\s*/, "")}</span>
                      </>
                    ) : (
                      <span>{line.trim()}</span>
                    )}
                  </div>
                ))}
            </div>
          </div>
        )}

        {status === "updating" && (
          <p className="text-xs text-muted-foreground">{progressText}</p>
        )}

        {status === "done" && (
          <p className="text-xs text-muted-foreground">
            Updated — restart your game if it&apos;s running
          </p>
        )}

        {gameRunning && (
          <div className="rounded-control border border-warning/50 bg-warning/10 p-3 text-xs text-foreground">
            Close Among Us to update — the mod file is locked while the game is
            running.
          </div>
        )}

        {status === "error" && error && !gameRunning && (
          <p className="text-xs text-destructive">{error}</p>
        )}

        {force && !updateInfo.download_url && (
          <p className="text-xs text-muted-foreground">
            No download is available right now. Check your connection and try
            again later.
          </p>
        )}

        <div className="flex gap-3 pt-2">
          {status === "done" ? (
            <Button onClick={onClose} className="flex-1">
              Close
            </Button>
          ) : (
            <>
              {showSecondaryButton && (
                <Button onClick={onClose} variant="outline" className="flex-1">
                  {force ? "Close" : "Later"}
                </Button>
              )}
              <Button
                onClick={handleUpdate}
                disabled={updating || !updateInfo.download_url}
                className="flex-1"
              >
                {updating ? (
                  progressText || "Downloading..."
                ) : (
                  <>
                    <Download className="h-4 w-4" />
                    {gameRunning ? "Retry" : "Update"}
                  </>
                )}
              </Button>
            </>
          )}
        </div>
      </div>
    </Modal>
  );
}
