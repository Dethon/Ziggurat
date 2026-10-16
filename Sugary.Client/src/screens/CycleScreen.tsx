import { useCallback, useEffect, useState } from "react";
import { api } from "../api";
import { Icon } from "../components/Icon";
import { cycleStatus, history, localDay, periodProblem } from "../cycle";
import { errorMessage } from "../errors";
import { formatDays } from "../format";
import { useMinute } from "../clock";
import { useSettings } from "../settings";
import { useToast } from "../toast";
import type { Period, SavePeriod } from "../types";
import { PeriodSheet, problemText } from "./PeriodSheet";

// Past this day of a period, the status asks whether it is still going: an end she forgot to note
// is the one thing an open period cannot say for itself.
const LONG_PERIOD_DAY = 10;

type Opened = { period?: Period; initial: SavePeriod };

export function CycleScreen() {
  const { t, language } = useSettings();
  const toast = useToast();
  const [periods, setPeriods] = useState<Period[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [sheet, setSheet] = useState<Opened | null>(null);
  const [busy, setBusy] = useState(false);
  const today = localDay(new Date(useMinute()));
  const thisYear = today.slice(0, 4);

  const load = useCallback(async () => {
    try {
      setPeriods(await api.periods());
      setError(null);
    } catch (failure) {
      setError(errorMessage(failure, t));
    }
  }, [t]);

  useEffect(() => {
    void load();
  }, [load]);

  const all = periods ?? [];
  const status = cycleStatus(all, today);

  // One tap records today; the rules are asked first so a clash is said in her language.
  async function quick(values: SavePeriod, period?: Period) {
    const problem = periodProblem(values.start, values.end, today, all.filter((other) => other.id !== period?.id));
    if (problem) {
      setError(problemText(problem, t, language));
      return;
    }
    setBusy(true);
    try {
      await (period ? api.updatePeriod(period.id, values) : api.createPeriod(values));
      toast({ text: t("periodSaved") });
      await load();
    } catch (failure) {
      setError(errorMessage(failure, t, "save"));
    } finally {
      setBusy(false);
    }
  }

  // Gone at once; the toast offers it back with the days she recorded.
  async function remove(period: Period) {
    try {
      await api.deletePeriod(period.id);
      setSheet(null);
      await load();
      toast({
        text: t("periodDeleted"),
        action: {
          label: t("undo"),
          run: () =>
            void api.createPeriod({ start: period.start, end: period.end }).then(load, (failure) => setError(errorMessage(failure, t, "save"))),
        },
      });
    } catch (failure) {
      setSheet(null);
      setError(errorMessage(failure, t, "save"));
    }
  }

  return (
    <div className="screen">
      <h1 className="marked">
        <Icon name="slice" size={28} />
        {t("tabCycle")}
      </h1>
      {error && (
        <p className="notice notice-error" role="alert">
          <Icon name="alert" size={18} /> <span>{error}</span>
        </p>
      )}

      {periods !== null && (
        <section className={`card cycle-status cycle-status-${status.kind}`} aria-labelledby="cycle-status-title">
          {status.kind === "none" ? (
            <div>
              <h2 id="cycle-status-title" className="section-title">
                {t("cycleNone")}
              </h2>
              <p className="muted small">{t("cycleNoneHint")}</p>
            </div>
          ) : (
            <div className="cycle-status-line">
              <span className="cycle-status-mark" aria-hidden="true">
                <Icon name="drop" size={26} />
              </span>
              <div>
                <h2 id="cycle-status-title" className="section-title">
                  {t(status.kind === "period" ? "periodDay" : "cycleDay", { n: status.day })}
                </h2>
                {status.kind === "period" && status.period.end === null && status.day > LONG_PERIOD_DAY && (
                  <p className="muted small">{t("periodLongHint")}</p>
                )}
              </div>
            </div>
          )}
          {status.kind === "period" && status.period.end === null ? (
            <div className="cycle-actions">
              <button
                type="button"
                className="button button-primary"
                disabled={busy}
                onClick={() => void quick({ start: status.period.start, end: today }, status.period)}
              >
                {t("periodEndToday")}
              </button>
              <button
                type="button"
                className="button button-quiet"
                onClick={() => setSheet({ period: status.period, initial: { start: status.period.start, end: today } })}
              >
                {t("anotherDay")}
              </button>
            </div>
          ) : (
            status.kind !== "period" && (
              <div className="cycle-actions">
                <button type="button" className="button button-primary" disabled={busy} onClick={() => void quick({ start: today, end: null })}>
                  {t("periodStartToday")}
                </button>
                <button type="button" className="button button-quiet" onClick={() => setSheet({ initial: { start: today, end: null } })}>
                  {t("anotherDay")}
                </button>
              </div>
            )
          )}
        </section>
      )}

      {all.length > 0 && (
        <section className="card" aria-labelledby="periods-title">
          <h2 id="periods-title" className="section-title">
            {t("pastPeriods")}
          </h2>
          <ul className="rows">
            {history(all).map(({ period, days, cycleDays }) => (
              <li key={period.id}>
                <button
                  type="button"
                  className="row-link period-row"
                  aria-haspopup="dialog"
                  onClick={() => setSheet({ period, initial: { start: period.start, end: period.end } })}
                >
                  <Icon name="drop" />
                  <span className="period-row-text">
                    <span className="item-name">
                      {period.end === null
                        ? t("periodSince", { date: formatDays(period.start, period.start, language, thisYear) })
                        : formatDays(period.start, period.end, language, thisYear)}
                    </span>
                    <span className="item-meta">
                      {days === null ? t("periodOngoing") : days === 1 ? t("periodDaysOne") : t("periodDays", { n: days })}
                      {cycleDays !== null && ` · ${t("cycleDays", { n: cycleDays })}`}
                    </span>
                  </span>
                  <Icon name="chevronRight" size={20} />
                </button>
              </li>
            ))}
          </ul>
        </section>
      )}

      {sheet && (
        <PeriodSheet
          period={sheet.period}
          initial={sheet.initial}
          periods={all}
          today={today}
          onClose={() => setSheet(null)}
          onSaved={() => {
            setSheet(null);
            toast({ text: t("periodSaved") });
            void load();
          }}
          onDelete={(period) => void remove(period)}
        />
      )}
    </div>
  );
}
