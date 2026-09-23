import { useEffect, useRef } from 'react';
import { X } from 'lucide-react';
import { Button } from '@/components/ui/button';

export interface ModalProps {
  isOpen: boolean;
  onClose: () => void;
  title: string;
  children: React.ReactNode;
  allowDismiss?: boolean;
}

export function Modal({ isOpen, onClose, title, children, allowDismiss = true }: ModalProps) {
  const overlayRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!isOpen) return;
    const handleEsc = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && allowDismiss) onClose();
    };
    window.addEventListener('keydown', handleEsc);
    return () => window.removeEventListener('keydown', handleEsc);
  }, [isOpen, allowDismiss, onClose]);

  if (!isOpen) return null;

  return (
    <div ref={overlayRef}
      onClick={allowDismiss ? onClose : undefined}
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 animate-in fade-in">
      <div
        role="dialog"
        aria-modal="true"
        aria-label={title}
        onClick={e => e.stopPropagation()}
        className="w-full max-w-md mx-4 rounded-card border border-border bg-surface shadow-card animate-in zoom-in-95 slide-in-from-bottom-4">
        <div className="flex items-center justify-between gap-4 border-b border-border px-6 py-4">
          <h2 className="text-base font-semibold text-foreground">{title}</h2>
          {allowDismiss && (
            <button
              onClick={onClose}
              title="Close"
              aria-label="Close dialog"
              className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-control text-muted-foreground transition-colors hover:bg-surface-2 hover:text-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
            >
              <X className="h-4 w-4" aria-hidden="true" />
            </button>
          )}
        </div>
        <div className="px-6 py-4">{children}</div>
      </div>
    </div>
  );
}

export interface ConfirmModalProps {
  isOpen: boolean;
  onClose: () => void;
  onConfirm: () => void;
  title: string;
  message: string;
  danger?: boolean;
  confirmText?: string;
}

export function ConfirmModal({ isOpen, onClose, onConfirm, title, message, danger, confirmText = 'Confirm' }: ConfirmModalProps) {
  return (
    <Modal isOpen={isOpen} onClose={onClose} title={title} allowDismiss={true}>
      <p className="mb-6 text-sm text-muted-foreground">{message}</p>
      <div className="flex justify-end gap-3">
        <Button onClick={onClose} variant="outline">
          Cancel
        </Button>
        <Button
          onClick={() => { onConfirm(); onClose(); }}
          variant={danger ? 'destructive' : 'default'}
        >
          {confirmText}
        </Button>
      </div>
    </Modal>
  );
}
