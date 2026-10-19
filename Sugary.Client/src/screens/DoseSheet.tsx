import { useId, useMemo, useState } from "react";
import { api } from "../api";
import { Icon } from "../components/Icon";
import { MealThumb } from "../components/MealThumb";
import { Sheet } from "../components/Sheet";
import { UnitsControl } from "../components/UnitsControl";
import { doseStart, linkable, MAX_NOTE, MAX_UNITS, readUnits } from "../dose";
import { useEdited } from "../edited";
import { errorMessage } from "../errors";
import { formatGrams, shownTotal } from "../carbs";
import { localDay } from "../cycle";
import { formatShortDay, formatTime, formatUnits, toLocalInput } from "../format";
import { mealNames } from "../meal";
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
  // Her loaded meals, of which those of about a day either side of the dose are offered to link it to.
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
  const linked = dose ? dose.mealId : start.mealId;
  // Follows the time field, so changing when the dose was taken offers the meals around then.
  const offered = useMemo(() => linkable(meals, when ? new Date(when) : new Date(), linked), [meals, when, linked]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const formId = useId();
  const unitsId = useId();
  const read = readUnits(units);
  // Said once she has typed something, not while the field is still empty.
  const problem = typeof read === "string" && units.trim() !== "" ? t(read, { max: MAX_UNITS }) : null;
  const values = {
    takenAt: when ? new Date(when).toISOString() : null,
    units: read,
    kind,
    note: note.trim() || null,
    mealId: mealId || null,
  };
  // A recorded dose is saved again only once something about it changed.
  const edited = useEdited(values);
  const ready = typeof read === "number" && !!when && (!dose || edited);

  async function save() {
    if (!ready || typeof read !== "number" || !when) {
      return;
    }
    setBusy(true);
    setError(null);
    const saving = { ...values, units: read, takenAt: new Date(when).toISOString() };
    try {
      onSaved(await (dose ? api.updateDose(dose.id, saving) : api.createDose(saving)));
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
          disabled={busy || !ready}
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
          <MealChoice meals={offered} chosen={mealId} takenAt={when} onChoose={setMealId} />
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

// How many of the closest meals are on show before she asks for the rest.
const CLOSEST = 3;

// The meal a dose is for, picked from the meals around it, the closest first: each as the Log draws
// it, with its photo, its foods and its time, and "no meal" above them all.
function MealChoice({
  meals,
  chosen,
  takenAt,
  onChoose,
}: {
  meals: readonly Meal[];
  chosen: string;
  takenAt: string;
  onChoose: (mealId: string) => void;
}) {
  const { t, language } = useSettings();
  const [all, setAll] = useState(false);
  // The chosen meal stays on show however far down the list it is.
  const shown = all ? meals : meals.filter((meal, index) => index < CLOSEST || meal.id === chosen);
  const doseDay = takenAt ? localDay(new Date(takenAt)) : null;

  return (
    <fieldset className="question">
      <legend>{t("doseMeal")}</legend>
      <ul className="meal-choices">
        <li>
          <button
            type="button"
            className={`meal-choice${chosen === "" ? " meal-choice-on" : ""}`}
            aria-pressed={chosen === ""}
            onClick={() => onChoose("")}
          >
            <span className="meal-thumb meal-thumb-empty">
              <Icon name="minus" />
            </span>
            <span className="item-text">
              <span className="item-name">{t("doseNoMeal")}</span>
            </span>
            <span className="meal-choice-tick" aria-hidden="true">
              <Icon name="check" size={22} />
            </span>
          </button>
        </li>
        {shown.map((meal) => {
          const on = chosen === meal.id;
          const day = localDay(new Date(meal.eatenAt)) === doseDay ? null : formatShortDay(meal.eatenAt, language);
          return (
            <li key={meal.id}>
              <button
                type="button"
                className={`meal-choice${on ? " meal-choice-on" : ""}`}
                aria-pressed={on}
                onClick={() => onChoose(meal.id)}
              >
                <MealThumb meal={meal} />
                <span className="item-text">
                  <span className="item-name">{mealNames(meal)}</span>
                  <span className="item-meta">
                    {day ? `${day} · ` : ""}
                    {formatTime(meal.eatenAt, language)} · {formatGrams(shownTotal(meal.items).best)}&nbsp;g
                  </span>
                </span>
                <span className="meal-choice-tick" aria-hidden="true">
              <Icon name="check" size={22} />
            </span>
              </button>
            </li>
          );
        })}
      </ul>
      {shown.length < meals.length && (
        <button type="button" className="button button-quiet" onClick={() => setAll(true)}>
          <Icon name="chevron" size={20} />
          {t("doseMealMore", { n: meals.length - shown.length })}
        </button>
      )}
    </fieldset>
  );
}
