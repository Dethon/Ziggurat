import { useId, useState } from "react";
import { api } from "../api";
import { Icon } from "../components/Icon";
import { Sheet } from "../components/Sheet";
import { MAX_NOTE, MAX_UNITS, readUnits } from "../dose";
import { errorMessage } from "../errors";
import { toLocalInput } from "../format";
import { useSettings } from "../settings";
import type { Dose, DoseKind } from "../types";

const KINDS: { kind: DoseKind; icon: "bolt" | "clock"; hint: "doseRapidHint" | "doseBasalHint" }[] = [
  { kind: "rapid", icon: "bolt", hint: "doseRapidHint" },
  { kind: "basal", icon: "clock", hint: "doseBasalHint" },
];

// A dose added on its own: the units always start empty, because the number recorded is only ever
// one she typed. Rapid is the usual kind and now the usual time; both can be changed.
export function DoseSheet({ onClose, onSaved }: { onClose: () => void; onSaved: (dose: Dose) => void }) {
  const { t } = useSettings();
  const [units, setUnits] = useState("");
  const [kind, setKind] = useState<DoseKind>("rapid");
  const [when, setWhen] = useState(() => toLocalInput(new Date().toISOString()));
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const formId = useId();
  const read = readUnits(units);
  // Said once she has typed something, not while the field is still empty.
  const problem = typeof read === "string" && units.trim() !== "" ? t(read, { max: MAX_UNITS }) : null;

  async function save() {
    if (typeof read !== "number" || !when) {
      return;
    }
    setBusy(true);
    setError(null);
    try {
      onSaved(
        await api.createDose({
          takenAt: new Date(when).toISOString(),
          units: read,
          kind,
          note: note.trim() || null,
          mealId: null,
        }),
      );
    } catch (failure) {
      setError(errorMessage(failure, t, "save"));
      setBusy(false);
    }
  }

  return (
    <Sheet
      title={t("doseTitle")}
      onClose={onClose}
      footer={
        <button
          type="submit"
          form={formId}
          className="button button-primary button-big"
          disabled={busy || typeof read !== "number" || !when}
        >
          {t("saveDose")}
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
        <p className="muted small">{t("doseRecordHint")}</p>
        <label className="field">
          <span className="field-label">{t("doseUnits")}</span>
          <span className="amount-input">
            <input
              inputMode="decimal"
              autoComplete="off"
              value={units}
              aria-invalid={problem !== null}
              onChange={(event) => setUnits(event.target.value)}
            />
            <span className="amount-unit">{t("unitsShort")}</span>
          </span>
          {problem && (
            <span className="field-problem" role="alert">
              {problem}
            </span>
          )}
        </label>
        <fieldset className="question">
          <legend>{t("doseKind")}</legend>
          <div className="segmented dose-kinds">
            {KINDS.map((option) => (
              <button
                key={option.kind}
                type="button"
                className={`segment dose-kind dose-kind-${option.kind}${kind === option.kind ? " segment-on" : ""}`}
                aria-pressed={kind === option.kind}
                onClick={() => setKind(option.kind)}
              >
                <Icon name={option.icon} size={20} />
                <span className="dose-kind-text">
                  {t(`dose_${option.kind}`)}
                  <small>{t(option.hint)}</small>
                </span>
              </button>
            ))}
          </div>
        </fieldset>
        <label className="field">
          <span className="field-label">{t("when")}</span>
          <input type="datetime-local" required value={when} onChange={(event) => setWhen(event.target.value)} />
        </label>
        <label className="field">
          <span className="field-label">{t("doseNote")}</span>
          <input
            value={note}
            maxLength={MAX_NOTE}
            placeholder={t("doseNotePlaceholder")}
            onChange={(event) => setNote(event.target.value)}
          />
        </label>
        {error && (
          <p className="notice notice-error" role="alert">
            <Icon name="alert" size={18} /> <span>{error}</span>
          </p>
        )}
      </form>
    </Sheet>
  );
}
