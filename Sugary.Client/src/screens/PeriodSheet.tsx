import { useId, useState } from "react";
import { api } from "../api";
import { Icon } from "../components/Icon";
import { Sheet } from "../components/Sheet";
import { MAX_PERIOD_DAYS, periodProblem, type PeriodProblem } from "../cycle";
import { useEdited } from "../edited";
import { errorMessage } from "../errors";
import { formatDays } from "../format";
import type { Language, Translate } from "../i18n";
import { useSettings } from "../settings";
import type { Period, SavePeriod } from "../types";

export function problemText(problem: PeriodProblem, t: Translate, language: Language): string {
  return typeof problem === "string"
    ? t(problem, { max: MAX_PERIOD_DAYS })
    : t(problem.key, { date: formatDays(problem.start, problem.start, language) });
}

// A period started or ended on another day than today, or one she recorded opened to change or
// delete. A new one starts from the days it was opened with; an open one from what she recorded.
export function PeriodSheet({
  period,
  initial,
  periods,
  today,
  onClose,
  onSaved,
  onDelete,
}: {
  period?: Period;
  initial: SavePeriod;
  // All her periods, to say before saving that the days clash with one.
  periods: readonly Period[];
  today: string;
  onClose: () => void;
  onSaved: (period: Period) => void;
  onDelete?: (period: Period) => void;
}) {
  const { t, language } = useSettings();
  const [start, setStart] = useState(initial.start);
  const [end, setEnd] = useState(initial.end ?? today);
  const [ongoing, setOngoing] = useState(initial.end === null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const formId = useId();
  const chosenEnd = ongoing ? null : end;
  const others = periods.filter((other) => other.id !== period?.id);
  const problem = start ? periodProblem(start, chosenEnd, today, others) : null;
  const values = { start, end: chosenEnd };
  // A recorded period is saved again only once its days changed.
  const edited = useEdited(values);
  const ready = start !== "" && (ongoing || end !== "") && problem === null && (!period || edited);

  async function save() {
    if (!ready) {
      return;
    }
    setBusy(true);
    setError(null);
    try {
      onSaved(await (period ? api.updatePeriod(period.id, values) : api.createPeriod(values)));
    } catch (failure) {
      setError(errorMessage(failure, t, "save"));
      setBusy(false);
    }
  }

  return (
    <Sheet
      title={t(period ? "periodEditTitle" : "periodTitle")}
      onClose={onClose}
      footer={
        <button type="submit" form={formId} className="button button-primary button-big" disabled={busy || !ready}>
          {t("savePeriod")}
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
          <span className="field-label">{t("periodStart")}</span>
          <input type="date" required value={start} max={today} onChange={(event) => setStart(event.target.value)} />
        </label>
        <label className="check">
          <input type="checkbox" checked={ongoing} onChange={(event) => setOngoing(event.target.checked)} />
          <span>{t("periodOngoing")}</span>
        </label>
        {!ongoing && (
          <label className="field">
            <span className="field-label">{t("periodEnd")}</span>
            <input
              type="date"
              required
              value={end}
              min={start || undefined}
              max={today}
              onChange={(event) => setEnd(event.target.value)}
            />
          </label>
        )}
        {problem && (
          <p className="field-problem" role="alert">
            {problemText(problem, t, language)}
          </p>
        )}
        {error && (
          <p className="notice notice-error" role="alert">
            <Icon name="alert" size={18} /> <span>{error}</span>
          </p>
        )}
        {period && onDelete && (
          <button type="button" className="button button-quiet danger" disabled={busy} onClick={() => onDelete(period)}>
            <Icon name="trash" size={20} />
            {t("deletePeriod")}
          </button>
        )}
      </form>
    </Sheet>
  );
}
