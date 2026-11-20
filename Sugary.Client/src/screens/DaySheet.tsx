import { useId, useState } from "react";
import { api } from "../api";
import { Icon } from "../components/Icon";
import { Sheet } from "../components/Sheet";
import { dayAction, dayDate } from "../cycle";
import { errorMessage } from "../errors";
import { formatDay, formatDays } from "../format";
import { sentence } from "../meal";
import { useSettings } from "../settings";
import { FLOWS, nothingSaid, sameDay, SYMPTOMS, toggled } from "../days";
import type { DayEntry, Flow, Period, SavePeriod } from "../types";

export const MAX_DAY_NOTE = 500;

export const NOTHING: DayEntry = { text: "", flow: null, symptoms: [] };

// How many drops a flow is drawn with: spotting one small one, heavy three.
const DROPS: Record<Flow, number> = { spotting: 1, light: 1, medium: 2, heavy: 3 };

// Everything about one day: her flow, her symptoms, her words, and the period action that applies to
// it. `readOnly` is a day of somebody else's cycle: what was said is read, and nothing is offered to
// change.
export function DaySheet({
  day,
  note,
  readOnly = false,
  periods,
  today,
  onClose,
  onNoteSaved,
  onPeriod,
  onOpenPeriod,
}: {
  day: string;
  // What was said of the day when the sheet opened; NOTHING for a day with none.
  note: DayEntry;
  readOnly?: boolean;
  periods: readonly Period[];
  today: string;
  onClose: () => void;
  // What the day now says, or null once nothing is said of it (the server deleted it).
  onNoteSaved: (entry: DayEntry | null) => void;
  // Save these days at once (start on this day, or end the ongoing period on it).
  onPeriod: (values: SavePeriod, period?: Period) => void;
  // Open the period sheet: the one this day belongs to, or a new one starting on it.
  onOpenPeriod: (period: Period | undefined, initial: SavePeriod) => void;
}) {
  const { t, language } = useSettings();
  const [entry, setEntry] = useState<DayEntry>(note);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const formId = useId();
  const action = dayAction(day, periods, today);

  async function save() {
    setBusy(true);
    setError(null);
    try {
      await api.setDayNote(day, entry, note);
      onNoteSaved(nothingSaid(entry) ? null : { ...entry, text: entry.text.trim() });
    } catch (failure) {
      setError(errorMessage(failure, t, "save"));
      setBusy(false);
    }
  }

  if (readOnly) {
    return (
      <Sheet title={sentence(formatDay(dayDate(day), language))} onClose={onClose}>
        <div className="sheet-form">
          {nothingSaid(note) ? (
            <p className="day-note-read">{t("dayNoNote")}</p>
          ) : (
            <>
              {note.flow && (
                <p>
                  <span className="field-label">{t("dayFlow")}</span>
                  <br />
                  {t(`flow_${note.flow}`)}
                </p>
              )}
              {note.symptoms.length > 0 && (
                <p>
                  <span className="field-label">{t("daySymptoms")}</span>
                  <br />
                  {note.symptoms.map((symptom) => t(`symptom_${symptom}`)).join(", ")}
                </p>
              )}
              {note.text.trim() && (
                <>
                  <p className="field-label">{t("dayNote")}</p>
                  <p className="day-note-read">{note.text.trim()}</p>
                </>
              )}
            </>
          )}
        </div>
      </Sheet>
    );
  }

  return (
    <Sheet
      title={sentence(formatDay(dayDate(day), language))}
      onClose={onClose}
      footer={
        <button
          type="submit"
          form={formId}
          className="button button-primary button-big"
          disabled={busy || sameDay(entry, note)}
        >
          {t("saveDay")}
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
        {action.kind === "open" ? (
          <button
            type="button"
            className="row-button period-in-day"
            onClick={() => onOpenPeriod(action.period, { start: action.period.start, end: action.period.end })}
          >
            <Icon name="drop" size={20} />
            <span>
              {t("periodOfDays", {
                days:
                  action.period.end === null
                    ? t("periodSince", { date: formatDays(action.period.start, action.period.start, language) })
                    : formatDays(action.period.start, action.period.end, language),
              })}
            </span>
            <Icon name="chevronRight" size={20} />
          </button>
        ) : (
          <button
            type="button"
            className="button button-secondary"
            onClick={() =>
              action.kind === "end"
                ? onPeriod({ start: action.period.start, end: day }, action.period)
                : action.ongoing
                  ? onPeriod({ start: day, end: null })
                  : onOpenPeriod(undefined, { start: day, end: day })
            }
          >
            <Icon name="drop" size={20} />
            {t(action.kind === "end" ? "periodEndThisDay" : "periodStartThisDay")}
          </button>
        )}
        {/* One flow at most: a tap on the one chosen takes it back. */}
        <div className="field">
          <span className="field-label" id={`${formId}-flow`}>
            {t("dayFlow")}
          </span>
          <div className="chips day-flows" role="group" aria-labelledby={`${formId}-flow`}>
            {FLOWS.map((flow) => (
              <button
                key={flow}
                type="button"
                className={entry.flow === flow ? "chip chip-on day-flow" : "chip day-flow"}
                aria-pressed={entry.flow === flow}
                onClick={() => setEntry((current) => ({ ...current, flow: current.flow === flow ? null : flow }))}
              >
                <span className={`day-flow-drops day-flow-${flow}`} aria-hidden="true">
                  {Array.from({ length: DROPS[flow] }, (_, n) => (
                    <Icon key={n} name="drop" size={flow === "spotting" ? 12 : 16} />
                  ))}
                </span>
                {t(`flow_${flow}`)}
              </button>
            ))}
          </div>
        </div>
        <div className="field">
          <span className="field-label" id={`${formId}-symptoms`}>
            {t("daySymptoms")}
          </span>
          <div className="chips" role="group" aria-labelledby={`${formId}-symptoms`}>
            {SYMPTOMS.map((symptom) => (
              <button
                key={symptom}
                type="button"
                className={entry.symptoms.includes(symptom) ? "chip chip-on" : "chip"}
                aria-pressed={entry.symptoms.includes(symptom)}
                onClick={() => setEntry((current) => ({ ...current, symptoms: toggled(current.symptoms, symptom) }))}
              >
                {t(`symptom_${symptom}`)}
              </button>
            ))}
          </div>
        </div>
        <label className="field">
          <span className="field-label">{t("dayNote")}</span>
          <textarea
            rows={3}
            value={entry.text}
            maxLength={MAX_DAY_NOTE}
            placeholder={t("dayNotePlaceholder")}
            onChange={(event) => setEntry((current) => ({ ...current, text: event.target.value }))}
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
