import { useState, useEffect } from "react";
import { invoke } from "@tauri-apps/api/core";
import { Modal } from "@/components/Modal";
import { Button } from "@/components/ui/button";
import { showToast } from "@/components/Toast";
import { Package } from "lucide-react";

interface LibraryEntry {
  path: string;
  storefront?: string | null;
}

interface LibraryPickerModalProps {
  isOpen: boolean;
  onClose: () => void;
  gamePath: string | null;
  onInstalled: () => void;
}

export function filenameFromPath(path: string): string {
  return path.split(/[/\\]/).pop() || path;
}

export function LibraryPickerModal({ isOpen, onClose, gamePath, onInstalled }: LibraryPickerModalProps) {
  const [entries, setEntries] = useState<LibraryEntry[]>([]);
  const [loading, setLoading] = useState(false);
  const [installing, setInstalling] = useState<string | null>(null);

  useEffect(() => {
    if (!isOpen) return;
    setLoading(true);
    invoke<LibraryEntry[]>("list_library")
      .then(setEntries)
      .catch(() => showToast("Failed to load library", "error"))
      .finally(() => setLoading(false));
  }, [isOpen]);

  async function handlePick(entry: LibraryEntry) {
    if (!gamePath) {
      showToast("No game path set", "error");
      return;
    }
    const filename = filenameFromPath(entry.path);
    setInstalling(filename);
    try {
      await invoke("install_from_library", { gamePath, filename });
      showToast(`Installed ${filename} from library`, "success");
      onInstalled();
      onClose();
    } catch {
      showToast("Failed to install from library", "error");
    } finally {
      setInstalling(null);
    }
  }

  return (
    <Modal isOpen={isOpen} onClose={onClose} title="Install from Library">
      {loading ? (
        <p className="text-sm text-muted-foreground">Loading library…</p>
      ) : entries.length === 0 ? (
        <p className="text-sm text-muted-foreground">
          Your library is empty. Copy a mod to the library to reuse it later.
        </p>
      ) : (
        <ul className="space-y-1">
          {entries.map((entry) => {
            const filename = filenameFromPath(entry.path);
            return (
              <li
                key={entry.path}
                className="flex items-center justify-between rounded-xl px-3 py-2 transition-colors hover:bg-white/5"
              >
                <div className="flex min-w-0 items-center gap-2">
                  <Package className="h-4 w-4 shrink-0 text-muted-foreground" />
                  <span className="truncate text-sm font-medium">{filename}</span>
                </div>
                <Button
                  variant="outline"
                  size="sm"
                  disabled={installing !== null}
                  onClick={() => handlePick(entry)}
                >
                  {installing === filename ? "Installing…" : "Pick"}
                </Button>
              </li>
            );
          })}
        </ul>
      )}
    </Modal>
  );
}
