/*
 * Modal primitive surface. The implementation lives in
 * `@/components/Modal` (shared shell component); this module exposes it under
 * the `ui/` primitive convention so new views import from one place.
 */
export { Modal, ConfirmModal } from "@/components/Modal";
export type { ModalProps, ConfirmModalProps } from "@/components/Modal";
