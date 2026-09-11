import { useState } from "react";
import { Modal } from "@/components/Modal";
import { Button } from "@/components/ui/button";
import { Download } from "lucide-react";

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

export function UpdateModal({ isOpen, onClose, updateInfo }: UpdateModalProps) {
  const [updating, setUpdating] = useState(false);

  async function handleUpdate() {
    if (updateInfo.download_url) {
      window.open(updateInfo.download_url, "_blank");
    }
    setUpdating(true);
  }

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

        <div className="flex gap-3 pt-2">
          <Button onClick={onClose} variant="outline" className="flex-1">
            Later
          </Button>
          <Button
            onClick={handleUpdate}
            disabled={updating}
            className="flex-1"
          >
            {updating ? (
              "Opening..."
            ) : (
              <>
                <Download className="h-4 w-4" />
                Update
              </>
            )}
          </Button>
        </div>
      </div>
    </Modal>
  );
}
