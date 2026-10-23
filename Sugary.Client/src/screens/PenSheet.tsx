import { useId, useState } from "react";
import { api } from "../api";
import { Icon } from "../components/Icon";
import { Sheet } from "../components/Sheet";
import { useEdited } from "../edited";
import { errorMessage } from "../errors";
import { MAX_INSULIN, MAX_PEN_DAYS, penProblem } from "../pens";
import { useSettings } from "../settings";
import type { Pen, SavePen } from "../types";

// A kind's first pen, or the open one opened to correct or delete. The day and how long it lasts
// are hers: the hint only says where the number comes from.
export function PenSheet({
  pen,
  initial,
  today,
  onClose,
  onSaved,
  onDelete,
}: {
  pen?: Pen;
  initial: SavePen;
  today: string;
  onClose: () => void;
  onSaved: () => void;
  onDelete?: (pen: Pen) => void;
}) {
  const { t } = useSettings();
  const [insulin, setInsulin] = useState(initial.insulin);
  const [openedOn, setOpenedOn] = useState(initial.openedOn);
  const [days, setDays] = useState(String(initial.days));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const formId = useId();
  const problem = penProblem(insulin, openedOn, days, today);
  const values: SavePen = { kind: initial.kind, insulin: insulin.trim(), openedOn, days: Number(days) };
  const edited = useEdited(values);
  const ready = problem === null && (!pen || edited);

  async function save() {
    if (!ready) {
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await (pen ? api.updatePen(pen.id, values) : api.createPen(values));
      onSaved();
    } catch (failure) {
      setError(errorMessage(failure, t, "save"));
      setBusy(false);
    }
  }

  return (
    <Sheet
      title={`${t(pen ? "penEditTitle" : "penTitle")} · ${t(`dose_${initial.kind}`)}`}
      onClose={onClose}
      footer={
        <button type="submit" form={formId} className="button button-primary button-big" disabled={busy || !ready}>
          {t("savePen")}
        </button>
      }
    >
      <form
        id={formId}
        className="sheet-form"
        onSubmit={(event) => {
          event.preventDefault();
          void save();
        }}
      >
        <label className="field">
          <span className="field-label">{t("penInsulin")}</span>
          <input
            type="text"
            required
            maxLength={MAX_INSULIN}
            autoComplete="off"
            placeholder={t("penInsulinPlaceholder")}
            value={insulin}
            onChange={(event) => setInsulin(event.target.value)}
          />
        </label>
        <label className="field">
          <span className="field-label">{t("penOpenedOn")}</span>
          <input type="date" required value={openedOn} max={today} onChange={(event) => setOpenedOn(event.target.value)} />
        </label>
        <label className="field">
          <span className="field-label">{t("penDaysLabel")}</span>
          <input
            type="number"
            required
            inputMode="numeric"
            min={1}
            max={MAX_PEN_DAYS}
            step={1}
            value={days}
            onChange={(event) => setDays(event.target.value)}
          />
          <span className="muted small">{t("penDaysHint")}</span>
        </label>
        {problem && (insulin !== "" || pen) && (
          <p className="field-problem" role="alert">
            {t(problem, { max: problem === "penInsulinTooLong" ? MAX_INSULIN : MAX_PEN_DAYS })}
          </p>
        )}
        {error && (
          <p className="notice notice-error" role="alert">
            <Icon name="alert" size={18} /> <span>{error}</span>
          </p>
        )}
        {pen && onDelete && (
          <button type="button" className="button button-quiet danger" disabled={busy} onClick={() => onDelete(pen)}>
            <Icon name="trash" size={20} />
            {t("deletePen")}
          </button>
        )}
      </form>
    </Sheet>
  );
}
