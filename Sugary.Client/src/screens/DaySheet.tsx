import { useId, useState } from "react";
import { api } from "../api";
import { Icon } from "../components/Icon";
import { Sheet } from "../components/Sheet";
import { dayAction, dayDate } from "../cycle";
import { errorMessage } from "../errors";
import { formatDay, formatDays } from "../format";
import { sentence } from "../meal";
import { useSettings } from "../settings";
import type { Period, SavePeriod } from "../types";

export const MAX_DAY_NOTE = 500;

// Everything about one day: its note, and the period action that applies to it.
export function DaySheet({
  day,
  note,
  periods,
  today,
  onClose,
  onNoteSaved,
  onPeriod,
  onOpenPeriod,
}: {
  day: string;
  note: string;
  periods: readonly Period[];
  today: string;
  onClose: () => void;
  onNoteSaved: (text: string) => void;
  // Save these days at once (start on this day, or end the ongoing period on it).
  onPeriod: (values: SavePeriod, period?: Period) => void;
  // Open the period sheet: the one this day belongs to, or a new one starting on it.
  onOpenPeriod: (period: Period | undefined, initial: SavePeriod) => void;
}) {
  const { t, language } = useSettings();
  const [text, setText] = useState(note);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const formId = useId();
  const action = dayAction(day, periods, today);

  async function save() {
    setBusy(true);
    setError(null);
    try {
      await api.setDayNote(day, text.trim());
      onNoteSaved(text.trim());
    } catch (failure) {
      setError(errorMessage(failure, t, "save"));
      setBusy(false);
    }
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
          disabled={busy || text.trim() === note.trim()}
        >
          {t("saveNote")}
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
        <label className="field">
          <span className="field-label">{t("dayNote")}</span>
          <textarea
            rows={4}
            value={text}
            maxLength={MAX_DAY_NOTE}
            placeholder={t("dayNotePlaceholder")}
            onChange={(event) => setText(event.target.value)}
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
