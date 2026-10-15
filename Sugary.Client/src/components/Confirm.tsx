import { useCallback, useState, type ReactNode } from "react";
import { useSettings } from "../settings";
import { Sheet } from "./Sheet";

export interface ConfirmOptions {
  title: string;
  body?: string;
  // The destructive action, named on its button ("Discard", "Delete meal").
  confirm: string;
  // The way back; "Cancel" unless the moment has a better word.
  cancel?: string;
}

// The app's own confirmation, in place of the browser's: `if (await confirm({...}))`. Render the
// returned element anywhere in the component that asks.
export function useConfirm(): [(options: ConfirmOptions) => Promise<boolean>, ReactNode] {
  const [asking, setAsking] = useState<{ options: ConfirmOptions; resolve: (yes: boolean) => void } | null>(null);
  const confirm = useCallback(
    (options: ConfirmOptions) => new Promise<boolean>((resolve) => setAsking({ options, resolve })),
    [],
  );
  const answer = (yes: boolean) => {
    asking?.resolve(yes);
    setAsking(null);
  };
  return [confirm, asking && <ConfirmSheet options={asking.options} onAnswer={answer} />];
}

function ConfirmSheet({ options, onAnswer }: { options: ConfirmOptions; onAnswer: (yes: boolean) => void }) {
  const { t } = useSettings();
  return (
    <Sheet title={options.title} role="alertdialog" onClose={() => onAnswer(false)}>
      <div className="sheet-form">
        {options.body && <p className="confirm-body">{options.body}</p>}
        <div className="sheet-actions">
          <button type="button" className="button button-danger button-big" onClick={() => onAnswer(true)}>
            {options.confirm}
          </button>
          <button type="button" className="button button-quiet button-big" autoFocus onClick={() => onAnswer(false)}>
            {options.cancel ?? t("cancel")}
          </button>
        </div>
      </div>
    </Sheet>
  );
}
