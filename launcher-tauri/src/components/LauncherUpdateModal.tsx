import { useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
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

type Status = "idle" | "opening" | "opened";

export function LauncherUpdateModal({
  isOpen,
  onClose,
  updateInfo,
}: LauncherUpdateModalProps) {
  const [currentVersion, setCurrentVersion] = useState("Unknown");
  const [status, setStatus] = useState<Status>("idle");

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

  async function handleUpdate() {
    if (!updateInfo.downloadUrl || status !== "idle") return;
    setStatus("opening");
    try {
      // Chosen mechanism: hand the release URL to the system browser.
      // Evidence: capabilities/default.json grants only `shell:default`
      // (= `allow-open`, scoped to http(s)/tel/mailto URLs) and `fs:default`
      // (read app dirs + mkdir only — NO write permission), and there is no
      // generic backend download command, so saving the .exe locally from the
      // frontend is not sanctioned. The browser downloads the NSIS installer;
      // the user runs it from their downloads.
      await open(updateInfo.downloadUrl);
      setStatus("opened");
    } catch (e) {
      // Error → toast + back to ready state so Update is usable again.
      setStatus("idle");
      showToast(`Update failed: ${formatError(e)}`, "error");
    }
  }

  const opening = status === "opening";

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

        {opening && (
          <div className="space-y-2">
            <p className="text-xs text-muted-foreground">
              Opening download page…
            </p>
            {/* Indeterminate bar: the actual download happens in the browser. */}
            <div className="h-1.5 w-full overflow-hidden rounded-pill bg-surface-2">
              <div className="h-full w-full animate-pulse rounded-full bg-primary" />
            </div>
          </div>
        )}

        {status === "opened" && (
          <p className="text-xs text-muted-foreground">
            Your browser will download the installer — run it to update.
          </p>
        )}

        <div className="flex gap-3 pt-2">
          {status === "opened" ? (
            <Button onClick={onClose} className="flex-1">
              Close
            </Button>
          ) : (
            <>
              <Button
                onClick={onClose}
                variant="outline"
                className="flex-1"
                disabled={opening}
              >
                Later
              </Button>
              <Button
                onClick={handleUpdate}
                disabled={opening || !updateInfo.downloadUrl}
                className="flex-1"
              >
                {opening ? (
                  "Opening…"
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
