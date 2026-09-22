import { createContext, useContext, useState, useCallback, type ReactNode } from "react";
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

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60" onClick={onClose || close}>
      <div
        className="bg-[var(--panel)] border border-[var(--line)] rounded-lg w-[600px] max-w-[90vw]"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center justify-between px-4 py-3 border-b border-[var(--line)]">
          <h3 className="text-sm font-mono font-medium">{title}</h3>
          <Button variant="default" size="sm" onClick={onClose || close}>
            <X className="h-4 w-4" />
          </Button>
        </div>
        <div className="p-4">{children}</div>
      </div>
    </div>
  );
}