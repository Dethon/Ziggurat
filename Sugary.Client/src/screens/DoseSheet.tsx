import { useId, useState } from "react";
import { api } from "../api";
import { Icon } from "../components/Icon";
import { Sheet } from "../components/Sheet";
import { UnitsControl } from "../components/UnitsControl";
import { doseStart, linkable, MAX_NOTE, MAX_UNITS, readUnits } from "../dose";
import { errorMessage } from "../errors";
import { formatTime, formatUnits, toLocalInput } from "../format";
import { sentence } from "../meal";
import { useSettings } from "../settings";
import type { Dose, DoseKind, Meal } from "../types";

const KINDS: { kind: DoseKind; icon: "bolt" | "clock"; hint: "doseRapidHint" | "doseBasalHint" }[] = [
  { kind: "rapid", icon: "bolt", hint: "doseRapidHint" },
  { kind: "basal", icon: "clock", hint: "doseBasalHint" },
];

// A dose added on its own, or one she recorded opened to change or delete. A new dose's units always
// start empty, because the number recorded is only ever one she typed or stepped to; rapid is the usual kind and
// now the usual time, and both can be changed. An open dose starts from what she recorded.
export function DoseSheet({
  dose,
  forMeal,
  meals,
  onClose,
  onSaved,
  onDelete,
}: {
  dose?: Dose;
  // A new dose added from this meal's sheet: it starts linked to the meal, at its time.
  forMeal?: Meal;
  // Her loaded meals, of which those of about the last day are offered to link the dose to.
  meals: readonly Meal[];
  onClose: () => void;
  onSaved: (dose: Dose) => void;
  onDelete?: (dose: Dose) => void;
}) {
  const { t, language } = useSettings();
  const [units, setUnits] = useState(() => (dose ? formatUnits(dose.units, language) : ""));
  const [kind, setKind] = useState<DoseKind>(dose?.kind ?? "rapid");
  const [start] = useState(() => doseStart(forMeal, new Date()));
  const [when, setWhen] = useState(() => toLocalInput(dose?.takenAt ?? start.takenAt));
  const [note, setNote] = useState(dose?.note ?? "");
  const [mealId, setMealId] = useState(dose ? (dose.mealId ?? "") : (start.mealId ?? ""));
  const [offered] = useState(() => linkable(meals, new Date(), dose ? dose.mealId : start.mealId));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const formId = useId();
  const unitsId = useId();
  const read = readUnits(units);
  // Said once she has typed something, not while the field is still empty.
  const problem = typeof read === "string" && units.trim() !== "" ? t(read, { max: MAX_UNITS }) : null;

  async function save() {
    if (typeof read !== "number" || !when) {
      return;
    }
    setBusy(true);
    setError(null);
    const values = {
      takenAt: new Date(when).toISOString(),
      units: read,
      kind,
      note: note.trim() || null,
      mealId: mealId || null,
    };
    try {
      onSaved(await (dose ? api.updateDose(dose.id, values) : api.createDose(values)));
    } catch (failure) {
      setError(errorMessage(failure, t, "save"));
      setBusy(false);
    }
  }

  return (
    <Sheet
      title={t(dose ? "doseEditTitle" : "doseTitle")}
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
        <div className="field">
          <label className="field-label" htmlFor={unitsId}>
            {t("doseUnits")}
          </label>
          <UnitsControl id={unitsId} text={units} invalid={problem !== null} onChange={setUnits} />
          {problem && (
            <span className="field-problem" role="alert">
              {problem}
            </span>
          )}
        </div>
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
        {offered.length > 0 && (
          <label className="field">
            <span className="field-label">{t("doseMeal")}</span>
            <select value={mealId} onChange={(event) => setMealId(event.target.value)}>
              <option value="">{t("doseNoMeal")}</option>
              {offered.map((meal) => (
                <option key={meal.id} value={meal.id}>
                  {`${formatTime(meal.eatenAt, language)} · ${meal.items.map((item) => sentence(item.name)).join(", ")}`}
                </option>
              ))}
            </select>
          </label>
        )}
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
        {dose && onDelete && (
          <button type="button" className="button button-quiet danger" disabled={busy} onClick={() => onDelete(dose)}>
            <Icon name="trash" size={20} />
            {t("deleteDose")}
          </button>
        )}
      </form>
    </Sheet>
  );
}
