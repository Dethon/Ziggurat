import { useCallback, useEffect, useState } from "react";
import { api } from "../api";
import { CycleCalendar } from "../components/CycleCalendar";
import { Icon } from "../components/Icon";
import { cycleStatus, daysLate, forecast, history, localDay, periodProblem, refusedBecause, trends } from "../cycle";
import { CycleTrends } from "../components/CycleTrends";
import { CycleGlucose } from "../components/CycleGlucose";
import { PeriodReminder } from "../components/PeriodReminder";
import { errorMessage } from "../errors";
import { openedPeriod } from "../stale";
import { formatDays } from "../format";
import { useMinute } from "../clock";
import { useSettings } from "../settings";
import { useToast } from "../toast";
import type { CyclePhases, DayNote, Period, SavePeriod } from "../types";
import { DaySheet, NOTHING } from "./DaySheet";
import { PeriodSheet, problemText } from "./PeriodSheet";

// Past this day of a period, the status asks whether it is still going: an end she forgot to note
// is the one thing an open period cannot say for itself.
const LONG_PERIOD_DAY = 10;

type Opened = { period?: Period; initial: SavePeriod };

// `readOnly` is somebody else's cycle: her days, forecast and notes are read, never recorded or changed.
export function CycleScreen({ readOnly = false }: { readOnly?: boolean }) {
  const { t, language } = useSettings();
  const toast = useToast();
  const [periods, setPeriods] = useState<Period[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [sheet, setSheet] = useState<Opened | null>(null);
  const [busy, setBusy] = useState(false);
  const [notes, setNotes] = useState<DayNote[]>([]);
  const [openDay, setOpenDay] = useState<string | null>(null);
  // Her glucose by phase: nothing at all without a sensor, or when it cannot be read.
  const [phases, setPhases] = useState<CyclePhases | null>(null);
  const today = localDay(new Date(useMinute()));
  const thisYear = today.slice(0, 4);
  const [month, setMonth] = useState(() => today.slice(0, 7));

  const load = useCallback(async () => {
    try {
      const [recorded, written] = await Promise.all([api.periods(), api.dayNotes()]);
      setPeriods(recorded);
      setNotes(written);
      setError(null);
      api.cyclePhases().then(setPhases, () => setPhases(null));
    } catch (failure) {
      setError(errorMessage(failure, t));
    }
  }, [t]);

  useEffect(() => {
    void load();
  }, [load]);

  const all = periods ?? [];
  const status = cycleStatus(all, today);
  const noted = new Set(notes.map((note) => note.day));
  const expected = forecast(all, today);
  const late = daysLate(expected, today);
  const cycles = trends(all);

  // One tap records today; the rules are asked first so a clash is said in her language.
  async function quick(values: SavePeriod, period?: Period) {
    const problem = periodProblem(values.start, values.end, today, all.filter((other) => other.id !== period?.id));
    if (problem) {
      setError(problemText(problem, t, language));
      return;
    }
    setBusy(true);
    try {
      await (period ? api.updatePeriod(period.id, values, openedPeriod(period)) : api.createPeriod(values));
      setOpenDay(null);
      toast({ text: t("periodSaved") });
      await load();
    } catch (failure) {
      const refused = await refusedBecause(values, period?.id, today, api.periods);
      if (refused) {
        setPeriods(refused.periods);
      }
      setError(refused?.problem ? problemText(refused.problem, t, language) : errorMessage(failure, t, "save"));
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

      {/* On a desktop where her cycle is stands beside its calendar. */}
      <div className="panes panes-cycle">
        <div className="pane pane-side">
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
              {readOnly ? null : status.kind === "period" && status.period.end === null ? (
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
            <section className="card forecast" aria-labelledby="forecast-title">
              <h2 id="forecast-title" className="section-title">
                {t("forecastTitle")}
              </h2>
              {expected ? (
                <>
                  <p className="forecast-range">{t("forecastRange", { range: formatDays(expected.earliest, expected.latest, language, thisYear) })}</p>
                  {late > 0 && status.kind === "cycle" && (
                    <p className="notice notice-caution forecast-late">
                      <Icon name="clock" size={18} />
                      <span>{late === 1 ? t("forecastLateOne") : t("forecastLate", { n: late })}</span>
                    </p>
                  )}
                  {expected.earliest !== expected.latest && (
                    <p className="muted">{t("forecastLikeliest", { date: formatDays(expected.likeliest, expected.likeliest, language, thisYear) })}</p>
                  )}
                  {expected.fertile && (
                    <p className="forecast-fertile">
                      {t("fertileWindow", { range: formatDays(expected.fertile.start, expected.fertile.end, language, thisYear) })}
                    </p>
                  )}
                  {!readOnly && <PeriodReminder />}
                  <p className="notice forecast-disclaimer">
                    <Icon name="info" size={18} />
                    <span>
                      {expected.cycles === 1 ? t("forecastBasisOne") : t("forecastBasis", { n: expected.cycles })} {t("forecastDisclaimer")}
                    </span>
                  </p>
                </>
              ) : (
                <p className="muted small">{t("forecastNeedsTwo")}</p>
              )}
            </section>
          )}
          {phases && [phases.menstrual, phases.follicular, phases.luteal].some((phase) => phase.mean !== null) && (
            <CycleGlucose phases={phases} />
          )}
        </div>

        <div className="pane">
          {periods !== null && (
            <CycleCalendar
              month={month}
              periods={all}
              noted={noted}
              today={today}
              forecast={expected}
              onMonth={setMonth}
              onDay={setOpenDay}
            />
          )}

          {cycles && <CycleTrends trends={cycles} thisYear={thisYear} />}

          {all.length > 0 && (
            <section className="card" aria-labelledby="periods-title">
              <h2 id="periods-title" className="section-title">
                {t("pastPeriods")}
              </h2>
              <ul className="rows">
                {history(all).map(({ period, days, cycleDays }) => {
                  const said = (
                    <>
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
                    </>
                  );
                  return (
                    <li key={period.id}>
                      {readOnly ? (
                        <div className="row-link period-row row-static">{said}</div>
                      ) : (
                        <button
                          type="button"
                          className="row-link period-row"
                          aria-haspopup="dialog"
                          onClick={() => setSheet({ period, initial: { start: period.start, end: period.end } })}
                        >
                          {said}
                          <Icon name="chevronRight" size={20} />
                        </button>
                      )}
                    </li>
                  );
                })}
              </ul>
            </section>
          )}
        </div>
      </div>

      {openDay && (
        <DaySheet
          day={openDay}
          readOnly={readOnly}
          note={notes.find((note) => note.day === openDay) ?? NOTHING}
          periods={all}
          today={today}
          onClose={() => setOpenDay(null)}
          onNoteSaved={(entry) => {
            setNotes((current) => [...current.filter((note) => note.day !== openDay), ...(entry ? [{ ...entry, day: openDay }] : [])]);
            setOpenDay(null);
            toast({ text: t(entry ? "daySaved" : "dayCleared") });
          }}
          onPeriod={(values, period) => void quick(values, period)}
          onOpenPeriod={(period, initial) => {
            setOpenDay(null);
            setSheet({ period, initial });
          }}
        />
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
