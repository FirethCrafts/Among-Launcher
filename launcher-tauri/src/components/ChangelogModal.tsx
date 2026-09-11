import { Modal } from "@/components/Modal";
import { Button } from "@/components/ui/button";

interface ChangelogModalProps {
  isOpen: boolean;
  onClose: () => void;
  version: string;
  changelog: string;
}

export function ChangelogModal({
  isOpen,
  onClose,
  version,
  changelog,
}: ChangelogModalProps) {
  return (
    <Modal isOpen={isOpen} onClose={onClose} title={`What's New — v${version}`}>
      <div className="space-y-4 max-h-80 overflow-y-auto pr-2">
        {changelog.split("\n").map((line, i) => {
          const trimmed = line.trim();
          if (!trimmed) return <div key={i} className="h-2" />;
          if (trimmed.startsWith("#")) {
            return (
              <h3
                key={i}
                className="font-semibold text-foreground text-sm"
              >
                {trimmed.replace(/^#+\s*/, "")}
              </h3>
            );
          }
          if (trimmed.startsWith("-") || trimmed.startsWith("*")) {
            return (
              <div key={i} className="flex gap-2 text-sm text-muted-foreground">
                <span className="text-primary shrink-0">•</span>
                <span>{trimmed.replace(/^[-*]\s*/, "")}</span>
              </div>
            );
          }
          return (
            <p key={i} className="text-sm text-muted-foreground">
              {trimmed}
            </p>
          );
        })}
      </div>
      <div className="flex justify-end mt-4">
        <Button onClick={onClose}>Got it</Button>
      </div>
    </Modal>
  );
}
