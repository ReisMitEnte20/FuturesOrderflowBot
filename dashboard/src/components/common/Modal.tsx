import { createContext, useContext, useState, useCallback, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { Button } from "./Button";
import { X } from "lucide-react";

interface ModalContextValue {
  isOpen: boolean;
  open: () => void;
  close: () => void;
  toggle: () => void;
}

const ModalContext = createContext<ModalContextValue>({
  isOpen: false,
  open: () => {},
  close: () => {},
  toggle: () => {},
});

export function useModal() {
  return useContext(ModalContext);
}

interface ModalProviderProps {
  children: ReactNode;
}

export function ModalProvider({ children }: ModalProviderProps) {
  const [isOpen, setIsOpen] = useState(false);

  const open = useCallback(() => setIsOpen(true), []);
  const close = useCallback(() => setIsOpen(false), []);
  const toggle = useCallback(() => setIsOpen((v) => !v), []);

  return (
    <ModalContext.Provider value={{ isOpen, open, close, toggle }}>
      {children}
    </ModalContext.Provider>
  );
}

interface ModalProps {
  title: string;
  children: ReactNode;
  onClose?: () => void;
}

export function Modal({ title, children, onClose }: ModalProps) {
  const { close } = useModal();

  // Per Portal in <body>, damit kein Vorfahr (backdrop-filter, transform) das Overlay einschränkt.
  return createPortal(
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-[rgba(1,8,20,0.72)] backdrop-blur-sm p-4 overflow-y-auto" onClick={onClose || close}>
      <div
        className="relative overflow-hidden bg-[var(--panel)] border border-[var(--line-2)] rounded-xl w-[600px] max-w-[90vw] shadow-[0_30px_80px_-30px_rgba(0,0,0,0.9),0_0_40px_-18px_rgba(60,240,160,0.5)]"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="hairline absolute inset-x-0 top-0" />
        <div className="flex items-center justify-between px-4 py-3 border-b border-[var(--line)]">
          <h3 className="display text-sm">{title}</h3>
          <Button variant="default" size="sm" onClick={onClose || close}>
            <X className="h-4 w-4" />
          </Button>
        </div>
        <div className="p-4">{children}</div>
      </div>
    </div>,
    document.body,
  );
}