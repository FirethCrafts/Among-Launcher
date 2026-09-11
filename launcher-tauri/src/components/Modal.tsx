import { useEffect, useRef } from 'react';

interface ModalProps {
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
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm animate-in fade-in">
      <div onClick={e => e.stopPropagation()}
        className="bg-card border border-border rounded-xl shadow-2xl w-full max-w-md mx-4 animate-in zoom-in-95 slide-in-from-bottom-4">
        <div className="flex items-center justify-between px-6 py-4 border-b border-border">
          <h2 className="text-lg font-semibold">{title}</h2>
          {allowDismiss && (
            <button onClick={onClose} className="text-muted-foreground hover:text-foreground transition-colors">
              <svg className="w-5 h-5" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                <line x1="6" y1="6" x2="18" y2="18" />
                <line x1="6" y1="18" x2="18" y2="6" />
              </svg>
            </button>
          )}
        </div>
        <div className="px-6 py-4">{children}</div>
      </div>
    </div>
  );
}

interface ConfirmModalProps {
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
      <p className="text-muted-foreground mb-6">{message}</p>
      <div className="flex justify-end gap-3">
        <button onClick={onClose}
          className="px-4 py-2 rounded-lg border border-border hover:bg-muted transition-colors">
          Cancel
        </button>
        <button onClick={() => { onConfirm(); onClose(); }}
          className={`px-4 py-2 rounded-lg font-medium transition-colors
            ${danger
              ? 'bg-destructive text-destructive-foreground hover:bg-destructive/90'
              : 'bg-primary text-primary-foreground hover:bg-primary/90'}`}>
          {confirmText}
        </button>
      </div>
    </Modal>
  );
}
