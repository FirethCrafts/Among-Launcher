import { useEffect } from 'react';
import { createPortal } from 'react-dom';
import { X } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { usePresence } from '@/lib/usePresence';

export interface ModalProps {
  isOpen: boolean;
  onClose: () => void;
  title: string;
  children: React.ReactNode;
  allowDismiss?: boolean;
}

export function Modal({ isOpen, onClose, title, children, allowDismiss = true }: ModalProps) {
  // Stay mounted through the exit animation; `state` drives enter vs exit.
  const { mounted, state } = usePresence(isOpen, 150);

  useEffect(() => {
    if (!isOpen) return;
    const handleEsc = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && allowDismiss) onClose();
    };
    window.addEventListener('keydown', handleEsc);
    return () => window.removeEventListener('keydown', handleEsc);
  }, [isOpen, allowDismiss, onClose]);

  if (!mounted) return null;

  // Portal to <body>: an ancestor with a `transform` (e.g. `animate-page-in`
  // on the routed content wrapper) makes `position: fixed` resolve against
  // that ancestor, which would centre the dialog inside the content pane
  // instead of the window.
  return createPortal(
    <div
      data-state={state}
      onClick={allowDismiss ? onClose : undefined}
      className={`fixed inset-0 z-50 flex items-center justify-center bg-black/60 ${
        state === 'enter' ? 'animate-overlay-in' : 'pointer-events-none animate-overlay-out'
      }`}>
      <div
        role="dialog"
        aria-modal="true"
        aria-label={title}
        data-state={state}
        onClick={e => e.stopPropagation()}
        className={`w-full max-w-md mx-4 rounded-card border border-border bg-surface shadow-card ${
          state === 'enter' ? 'animate-modal-in' : 'animate-modal-out'
        }`}>
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
    </div>,
    document.body
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
