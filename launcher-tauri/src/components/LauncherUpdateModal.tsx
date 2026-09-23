import { useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { open } from "@tauri-apps/plugin-shell";
import { Modal } from "@/components/Modal";
import { Button } from "@/components/ui/button";
import { Download } from "lucide-react";
import { formatError, showToast } from "@/components/Toast";

export interface LauncherUpdateInfo {
  version: string;
  downloadUrl: string;
  notes: string | null;
}

interface LauncherUpdateModalProps {
  isOpen: boolean;
  onClose: () => void;
  updateInfo: LauncherUpdateInfo;
}

interface UpdateProgress {
  stage: "downloading" | "installing";
  progress: number;
  total: number;
}

type Status = "idle" | "downloading" | "installing" | "error";

export function LauncherUpdateModal({
  isOpen,
  onClose,
  updateInfo,
}: LauncherUpdateModalProps) {
  const [currentVersion, setCurrentVersion] = useState("Unknown");
  const [status, setStatus] = useState<Status>("idle");
  const [progressText, setProgressText] = useState("");

  // Current version for the prompt; stays "Unknown" if get_version rejects
  // (e.g. backend command not available yet).
  useEffect(() => {
    if (!isOpen) {
      setStatus("idle");
      return;
    }
    let cancelled = false;
    invoke<string>("get_version")
      .then((v) => {
        if (!cancelled && typeof v === "string" && v) setCurrentVersion(v);
      })
      .catch(() => {
        /* keep "Unknown" */
      });
    return () => {
      cancelled = true;
    };
  }, [isOpen]);

  // Progress events from `install_launcher_update`, same shape as the mod
  // updater's `update-progress`. Subscribed only while the modal is open.
  useEffect(() => {
    if (!isOpen) {
      setProgressText("");
      return;
    }
    const unlisten = listen<UpdateProgress>("launcher-update-progress", (event) => {
      const { stage, progress, total } = event.payload;
      if (stage === "downloading") {
        setStatus("downloading");
        if (total > 0) {
          const pct = Math.round((progress / total) * 100);
          setProgressText(`Downloading… ${pct}%`);
        } else {
          setProgressText("Downloading…");
        }
      } else if (stage === "installing") {
        setStatus("installing");
        setProgressText("Installing…");
      }
    });
    return () => {
      unlisten.then((fn) => fn());
    };
  }, [isOpen]);

  async function handleUpdate() {
    if (!updateInfo.downloadUrl || status !== "idle") return;
    setStatus("downloading");
    setProgressText("Downloading…");
    try {
      // Backend streams the installer to %TEMP%\AmongLauncher and runs it
      // silently, then exits the app (~800 ms later) so the installer can
      // replace files. This invoke resolves before that exit.
      await invoke("install_launcher_update", {
        downloadUrl: updateInfo.downloadUrl,
      });
      // The app is about to exit — show a final status instead of buttons.
      setStatus("installing");
      setProgressText("Restarting…");
    } catch (e) {
      // Error → toast AND fall back to the browser download so the user is
      // not stuck (e.g. non-GitHub URL, network failure, install error).
      setStatus("error");
      showToast(`Update failed: ${formatError(e)}`, "error");
      try {
        await open(updateInfo.downloadUrl);
      } catch {
        /* browser fallback failed too; toast already shown */
      }
    }
  }

  const busy = status === "downloading" || status === "installing";

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title="Update available"
      // No accidental backdrop/Esc dismissal while the action is running.
      allowDismiss={status === "idle"}
    >
      <div className="space-y-4">
        <div className="flex items-center justify-between text-sm">
          <span className="text-muted-foreground">Version</span>
          <span className="font-mono font-medium">
            {currentVersion} <span className="text-muted-foreground">→</span>{" "}
            <span className="text-primary">{updateInfo.version}</span>
          </span>
        </div>

        <div className="max-h-40 overflow-y-auto rounded-control bg-surface-2 p-3">
          <p className="text-xs font-medium mb-2 text-muted-foreground">
            Release notes
          </p>
          <p className="text-xs text-muted-foreground whitespace-pre-wrap">
            {updateInfo.notes && updateInfo.notes.trim().length > 0
              ? updateInfo.notes
              : "See the release page for details."}
          </p>
        </div>

        {busy && (
          <div className="space-y-2">
            <p className="text-xs text-muted-foreground">{progressText}</p>
            {/* Indeterminate pulse bar (the byte total may be unknown). */}
            <div className="h-1.5 w-full overflow-hidden rounded-pill bg-surface-2">
              <div className="h-full w-full animate-pulse rounded-full bg-primary" />
            </div>
          </div>
        )}

        {status === "error" && (
          <p className="text-xs text-muted-foreground">
            Downloading in your browser instead — run the installer to update.
          </p>
        )}

        <div className="flex gap-3 pt-2">
          {busy ? (
            <>
              <Button variant="outline" className="flex-1" disabled>
                Later
              </Button>
              <Button className="flex-1" disabled>
                {status === "installing" ? "Restarting…" : "Updating…"}
              </Button>
            </>
          ) : (
            <>
              <Button onClick={onClose} variant="outline" className="flex-1">
                Later
              </Button>
              <Button
                onClick={handleUpdate}
                disabled={!updateInfo.downloadUrl}
                className="flex-1"
              >
                <Download className="h-4 w-4" />
                Update
              </Button>
            </>
          )}
        </div>
      </div>
    </Modal>
  );
}
