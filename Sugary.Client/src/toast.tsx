import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from "react";
import { Icon } from "./components/Icon";

export interface Toast {
  text: string;
  // One action, such as Undo; a toast with one stays longer so there is time to reach it.
  action?: { label: string; run: () => void };
}

const SHORT_MS = 2500;
const WITH_ACTION_MS = 5000;

const Context = createContext<(toast: Toast) => void>(() => {});

export const useToast = () => useContext(Context);

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toast, setToast] = useState<Toast | null>(null);
  const show = useCallback((next: Toast) => setToast(next), []);

  useEffect(() => {
    if (!toast) {
      return;
    }
    const timer = window.setTimeout(() => setToast(null), toast.action ? WITH_ACTION_MS : SHORT_MS);
    return () => window.clearTimeout(timer);
  }, [toast]);

  return (
    <Context.Provider value={show}>
      {children}
      {toast && (
        <div className="toast" role="status">
          <span className="toast-text">
            <Icon name="check" size={18} /> {toast.text}
          </span>
          {toast.action && (
            <button
              type="button"
              className="button"
              onClick={() => {
                toast.action?.run();
                setToast(null);
              }}
            >
              {toast.action.label}
            </button>
          )}
        </div>
      )}
    </Context.Provider>
  );
}
