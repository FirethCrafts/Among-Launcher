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
}

interface UpdateProgress {
  stage: "downloading" | "installing" | "complete";
  progress: number;
  total: number;
}

type Status = "idle" | "updating" | "done" | "error";

export function UpdateModal({ isOpen, onClose, updateInfo }: UpdateModalProps) {
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

  return (
    <Modal isOpen={isOpen} onClose={onClose} title="Update Available">
      <div className="space-y-4">
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
          <div className="rounded-lg bg-secondary/50 p-3 max-h-40 overflow-y-auto">
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

        {status === "error" && error && (
          <p className="text-xs text-destructive">{error}</p>
        )}

        <div className="flex gap-3 pt-2">
          {status === "done" ? (
            <Button onClick={onClose} className="flex-1">
              Close
            </Button>
          ) : (
            <>
              <Button onClick={onClose} variant="outline" className="flex-1">
                Later
              </Button>
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
                    Update
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
