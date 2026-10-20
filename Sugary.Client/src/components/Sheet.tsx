import { useEffect, useRef, type ReactNode } from "react";
import { useSettings } from "../settings";
import { Icon } from "./Icon";

interface Props {
  title: string;
  onClose: () => void;
  // Shown as a back arrow when the sheet has an earlier step to return to.
  onBack?: () => void;
  role?: "dialog" | "alertdialog";
  // Pinned under the scrolling content, so the sheet's main action is always in reach.
  footer?: ReactNode;
  children: ReactNode;
}

export function Sheet({ title, onClose, onBack, role, footer, children }: Props) {
  const { t } = useSettings();
  const dialog = useRef<HTMLDialogElement>(null);

  useEffect(() => {
    const element = dialog.current;
    element?.showModal();
    return () => element?.close();
  }, []);

  return (
    <dialog
      ref={dialog}
      className="sheet"
      role={role}
      aria-label={title}
      onCancel={(event) => {
        event.preventDefault();
        onClose();
      }}
      onClick={(event) => event.target === dialog.current && onClose()}
    >
      <div className="sheet-panel">
        <header className="sheet-header">
          {onBack && (
            <button type="button" className="icon-button sheet-back" aria-label={t("back")} onClick={onBack}>
              <Icon name="back" />
            </button>
          )}
          <h2>{title}</h2>
          <button type="button" className="icon-button" aria-label={t("close")} onClick={onClose}>
            <Icon name="close" />
          </button>
        </header>
        <div className="sheet-body">{children}</div>
        {footer && <div className="sheet-footer">{footer}</div>}
      </div>
    </dialog>
  );
}
