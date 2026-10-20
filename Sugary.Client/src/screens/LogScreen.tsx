import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "../api";
import { formatGrams, fromLogged, isRange, shownTotal, toMealItem } from "../carbs";
import { useConfirm } from "../components/Confirm";
import { Icon, type IconName } from "../components/Icon";
import { CakeStand } from "../components/Illustrations";
import { ItemRow } from "../components/ItemRow";
import { MealThumb } from "../components/MealThumb";
import { PortionSheet } from "../components/PortionSheet";
import { Sheet } from "../components/Sheet";
import { restored } from "../dose";
import { canStopNow, duration, elapsedMinutes, EXERCISE_ICON, restored as restoredSession, running, sessionMinutes, startNow } from "../exercise";
import { errorMessage } from "../errors";
import { formatDay, formatTime, formatUnits, rationCount, rationText, toLocalInput } from "../format";
import { localDay } from "../cycle";
import { attachedDoses, byDay, dayKey, dayUnits, insulinBelow, matches, mealEdited, onPeriod, outcomeState, pendingOutcomes, rapidUnits, timeline, withDose, withoutDose } from "../log";
import { mealNames, sentence } from "../meal";
import { useMinute } from "../clock";
import { useSettings } from "../settings";
import { useToast } from "../toast";
import type { Dose, DraftItem, Exercise, Meal, Outcome, Period } from "../types";
import { DoseSheet } from "./DoseSheet";
import { ExerciseSheet, type ExerciseOpening } from "./ExerciseSheet";
import { FoodsSheet } from "./FoodsSheet";
import { ManualSheet } from "./ManualSheet";
import { PackagedSheet } from "./PackagedSheet";
import { ReviseSheet } from "./ReviseSheet";
import type { Revision } from "../revise";

const PAGE = 50;
const RECORDED: Exclude<Outcome, "unknown">[] = ["low", "inRange", "high"];
const OUTCOME_ICON: Record<Outcome, IconName> = { low: "arrowDown", inRange: "checkCircle", high: "arrowUp", unknown: "minus" };

const DOSE_ICON = { rapid: "bolt", basal: "clock" } as const;
// The two she does most, a tap each.
const SHORTCUTS = ["walk", "gym"] as const;

// The start of the phone's day a meal was eaten on, so the doses of that whole day are shown with it.
function startOfDay(iso: string): string {
  const date = new Date(iso);
  return new Date(date.getFullYear(), date.getMonth(), date.getDate()).toISOString();
}

const asSaved = (meal: Meal, outcome: Outcome) => ({
  eatenAt: meal.eatenAt,
  notes: meal.notes,
  outcome,
  photo: null,
  firstEstimate: meal.firstEstimate,
  items: meal.items,
});

export function OutcomeBadge({ meal }: { meal: Meal }) {
  const { t } = useSettings();
  const { outcome } = meal;
  const state = outcomeState(meal);
  if (state === "early") {
    return null;
  }
  if (state === "waiting") {
    // Asked for in its card at the top; here it only says so.
    return <span className="outcome outcome-unknown">{t("outcomeWaiting")}</span>;
  }
  return (
    <span className={`outcome outcome-${outcome}`}>
      {outcome !== "unknown" && <Icon name={OUTCOME_ICON[outcome]} size={16} />}
      {t(`outcome_${outcome}`)}
    </span>
  );
}

// `estimates` and `labels` say whether the server estimates a meal and reads a label yet, as on the
// meal screen.
export function LogScreen({ estimates, labels, onChanged }: { estimates: boolean; labels: boolean; onChanged: () => void }) {
  const { t, language, rationSize } = useSettings();
  const toast = useToast();
  const [meals, setMeals] = useState<Meal[] | null>(null);
  const loaded = useRef<Meal[]>([]);
  const [doses, setDoses] = useState<Dose[]>([]);
  const [sessions, setSessions] = useState<Exercise[]>([]);
  const [exerciseSheet, setExerciseSheet] = useState<ExerciseOpening | null>(null);
  // Only to mark her period days; the Log reads as it did without them.
  const [periods, setPeriods] = useState<Period[]>([]);
  // The dose sheet: a new dose, one she opened, or closed.
  const [doseSheet, setDoseSheet] = useState<Dose | "new" | null>(null);
  const [more, setMore] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // The meal open in its sheet, read from the list so a dose changed from the sheet shows in it.
  const [openId, setOpenId] = useState<string | null>(null);
  const [query, setQuery] = useState("");
  const now = new Date(useMinute());

  const loadPage = useCallback(
    async (skip: number) => {
      try {
        const page = await api.meals(skip, PAGE);
        const all = skip === 0 ? page : [...loaded.current, ...page];
        const hasMore = page.length === PAGE;
        // The doses of the days the loaded meals span, and every dose once there are no older meals.
        const oldest = all.at(-1);
        const since = hasMore && oldest ? startOfDay(oldest.eatenAt) : null;
        const [taken, done] = await Promise.all([api.allDoses(since), api.allExercise(since)]);
        loaded.current = all;
        setMeals(all);
        setDoses(taken);
        setSessions(done);
        setMore(hasMore);
        setError(null);
      } catch (failure) {
        setError(errorMessage(failure, t));
      }
    },
    [t],
  );

  useEffect(() => {
    void loadPage(0);
    api.periods().then(setPeriods, () => {});
  }, [loadPage]);

  // One tap records it; the toast offers it back.
  async function record(meal: Meal, outcome: Outcome) {
    const was = meal.outcome;
    const put = (next: Outcome) => {
      setMeals((current) => current?.map((m) => (m.id === meal.id ? { ...m, outcome: next } : m)) ?? null);
      loaded.current = loaded.current.map((m) => (m.id === meal.id ? { ...m, outcome: next } : m));
      return api.updateMeal(meal.id, asSaved(meal, next));
    };
    try {
      await put(outcome);
      onChanged();
      toast({
        text: t("outcomeNoted", { outcome: t(`outcome_${outcome}`) }),
        action: {
          label: t("undo"),
          run: () => void put(was).then(onChanged, (failure) => setError(errorMessage(failure, t, "save"))),
        },
      });
    } catch (failure) {
      setMeals((current) => current?.map((m) => (m.id === meal.id ? { ...m, outcome: was } : m)) ?? null);
      loaded.current = loaded.current.map((m) => (m.id === meal.id ? { ...m, outcome: was } : m));
      setError(errorMessage(failure, t, "save"));
    }
  }

  // A dose saved or deleted moves on or off the meal it is linked to, so the meal's insulin shows
  // without a reload.
  function relink(change: (meals: Meal[]) => Meal[]) {
    loaded.current = change(loaded.current);
    setMeals(loaded.current);
  }

  function doseSaved(dose: Dose) {
    setDoseSheet(null);
    setDoses((current) => [dose, ...current.filter((d) => d.id !== dose.id)]);
    relink((current) => withDose(current, dose));
    toast({ text: t("doseSaved") });
  }

  // Gone at once; the toast offers it back, saved again with every value she recorded.
  async function removeDose(dose: Dose) {
    try {
      await api.deleteDose(dose.id);
      setDoseSheet(null);
      setDoses((current) => current.filter((d) => d.id !== dose.id));
      relink((current) => withoutDose(current, dose.id));
      toast({
        text: t("doseDeleted"),
        action: {
          label: t("undo"),
          run: () =>
            void api.createDose(restored(dose)).then(
              (back) => {
                setDoses((current) => [back, ...current]);
                relink((current) => withDose(current, back));
              },
              (failure) => setError(errorMessage(failure, t, "save")),
            ),
        },
      });
    } catch (failure) {
      setDoseSheet(null);
      setError(errorMessage(failure, t, "save"));
    }
  }

  // One tap starts it, with nothing asked; the toast takes it back.
  async function startExercise(kind: (typeof SHORTCUTS)[number]) {
    try {
      const started = await api.createExercise(startNow(kind));
      setSessions((current) => [started, ...current]);
      toast({
        text: t("exerciseStarted", { kind: exerciseName(started) }),
        action: {
          label: t("undo"),
          run: () =>
            void api.deleteExercise(started.id).then(
              () => setSessions((current) => current.filter((s) => s.id !== started.id)),
              (failure) => setError(errorMessage(failure, t, "save")),
            ),
        },
      });
    } catch (failure) {
      setError(errorMessage(failure, t, "save"));
    }
  }

  // One tap ends it now. A session left running past the longest one can be is not ended now: that
  // would save hours she did not do.
  async function stopExercise(session: Exercise) {
    if (!canStopNow(session)) {
      setExerciseSheet({ mode: "edit", session });
      return;
    }
    try {
      const stopped = await api.updateExercise(session.id, {
        ...session,
        endedAt: new Date().toISOString(),
      });
      setSessions((current) => current.map((s) => (s.id === stopped.id ? stopped : s)));
      toast({
        text: t("exerciseStopped", {
          kind: exerciseName(stopped),
          duration: duration(sessionMinutes(stopped) ?? 0, t),
        }),
      });
    } catch (failure) {
      setError(errorMessage(failure, t, "save"));
    }
  }

  const putSession = (session: Exercise) => setSessions((current) => [session, ...current.filter((s) => s.id !== session.id)]);

  // Gone at once; the toast offers it back, saved again with every value she recorded.
  async function removeExercise(session: Exercise) {
    try {
      await api.deleteExercise(session.id);
      setExerciseSheet(null);
      setSessions((current) => current.filter((s) => s.id !== session.id));
      toast({
        text: t("exerciseDeleted"),
        action: {
          label: t("undo"),
          run: () =>
            void api.createExercise(restoredSession(session)).then(putSession, (failure) => setError(errorMessage(failure, t, "save"))),
        },
      });
    } catch (failure) {
      setExerciseSheet(null);
      setError(errorMessage(failure, t, "save"));
    }
  }

  const exerciseName = (session: Exercise) => session.name ?? t(`exercise_${session.kind}`);
  const current = running(sessions);

  function dayLabel(date: Date): string {
    const today = new Date();
    const yesterday = new Date(today.getFullYear(), today.getMonth(), today.getDate() - 1);
    if (dayKey(date) === dayKey(today)) {
      return t("today");
    }
    if (dayKey(date) === dayKey(yesterday)) {
      return t("yesterday");
    }
    return formatDay(date, language);
  }

  const searching = query.trim() !== "";
  const shown = (meals ?? []).filter((meal) => matches(meal, query));
  const pending = searching ? [] : pendingOutcomes(meals ?? [], now);
  // A search looks through meals by their foods; doses have none, so they step aside while it runs.
  const days = byDay(timeline(shown, searching ? [] : doses, searching ? [] : sessions));
  const empty = meals?.length === 0 && doses.length === 0 && sessions.length === 0;
  const open = openId === null ? null : (meals?.find((meal) => meal.id === openId) ?? null);

  return (
    <div className="screen">
      <h1 className="marked">
        <Icon name="slice" size={28} />
        {t("tabLog")}
      </h1>
      {error && (
        <p className="notice notice-error" role="alert">
          <Icon name="alert" size={18} /> <span>{error}</span>
        </p>
      )}

      {/* The session going now, above all else, until she stops it. */}
      {meals !== null && current && (
        <RunningCard
          session={current}
          now={now}
          onStop={() => void stopExercise(current)}
          onOpen={() => setExerciseSheet({ mode: "edit", session: current })}
        />
      )}

      {meals !== null && (
        <section className="record" aria-labelledby="record-title">
          <h2 id="record-title">{t("recordTitle")}</h2>
          {current ? (
            <button type="button" className="row-button add-dose" aria-haspopup="dialog" onClick={() => setDoseSheet("new")}>
              <Icon name="insulin" size={20} />
              <span>{t("addDose")}</span>
              <Icon name="plus" size={20} />
            </button>
          ) : (
            <>
              {/* Insulin opens its sheet; a walk or the gym starts at once, the toast taking it back. */}
              <div className="tiles tiles-record">
                <button type="button" className="tile tile-dose" aria-haspopup="dialog" onClick={() => setDoseSheet("new")}>
                  <Icon name="insulin" size={26} />
                  {t("recordDose")}
                </button>
                {SHORTCUTS.map((kind) => (
                  <button key={kind} type="button" className="tile tile-exercise" onClick={() => void startExercise(kind)}>
                    <Icon name={EXERCISE_ICON[kind]} size={26} />
                    {t(`exercise_${kind}`)}
                  </button>
                ))}
                <button type="button" className="tile tile-exercise" aria-haspopup="dialog" onClick={() => setExerciseSheet({ mode: "other" })}>
                  <Icon name={EXERCISE_ICON.other} size={26} />
                  {t("exercise_other")}
                </button>
              </div>
              <button
                type="button"
                className="button button-quiet button-small exercise-past"
                aria-haspopup="dialog"
                onClick={() => setExerciseSheet({ mode: "past" })}
              >
                <Icon name="plus" size={18} />
                {t("exerciseAddPast")}
              </button>
            </>
          )}
        </section>
      )}

      {/* Under what she can add, so an empty Log still opens on them. */}
      {empty && (
        <div className="empty">
          <CakeStand />
          <p className="empty-title">{t("logEmpty")}</p>
          <p className="small">{t("logEmptyHint")}</p>
        </div>
      )}

      {meals !== null && meals.length > 0 && (
        <label className="search">
          <Icon name="search" size={20} />
          <input
            type="search"
            value={query}
            placeholder={t("logSearch")}
            aria-label={t("logSearch")}
            onChange={(event) => setQuery(event.target.value)}
          />
        </label>
      )}

      {pending.length > 0 && (
        <section className="pending" aria-labelledby="pending-title">
          <div>
            <h2 id="pending-title" className="section-title">
              {t("logPendingTitle")}
            </h2>
            <p className="muted small">{t("logPendingHint")}</p>
          </div>
          {pending.map((meal) => {
            const carbs = shownTotal(meal.items);
            return (
              <article key={meal.id} className="card pending-card" aria-label={mealNames(meal)}>
                <button type="button" className="meal-row" onClick={() => setOpenId(meal.id)}>
                  <MealThumb meal={meal} />
                  <span className="item-text">
                    <span className="item-name">{mealNames(meal)}</span>
                    <span className="item-meta">{formatTime(meal.eatenAt, language)}</span>
                  </span>
                  <span className="item-grams">{formatGrams(carbs.best)}&nbsp;g</span>
                </button>
                <div className="outcome-buttons" role="group" aria-label={t("outcomeTitle")}>
                  {RECORDED.map((outcome) => (
                    <button
                      key={outcome}
                      type="button"
                      className={`outcome-button outcome-button-${outcome}`}
                      onClick={() => void record(meal, outcome)}
                    >
                      <Icon name={OUTCOME_ICON[outcome]} size={20} />
                      {t(`outcome_${outcome}`)}
                    </button>
                  ))}
                </div>
              </article>
            );
          })}
        </section>
      )}

      {searching && shown.length === 0 && <p className="muted">{t("logSearchNone")}</p>}

      {days.map((day) => {
        const attached = attachedDoses(day.entries);
        const units = dayUnits(day.entries);
        return (
        <section key={day.key} className="day" aria-label={dayLabel(day.date)}>
          <header className="day-header">
            <h2>{dayLabel(day.date)}</h2>
            {onPeriod(day.date, periods, localDay(now)) && (
              <span className="day-period">
                <Icon name="drop" size={14} />
                {t("logPeriodDay")}
              </span>
            )}
            <span className="day-totals">
              <span className="day-total">
                {t("dayTotal", {
                  n: day.entries.reduce((sum, entry) => sum + (entry.type === "meal" ? shownTotal(entry.meal.items).best : 0), 0),
                })}
              </span>
              {units > 0 && <span className="day-units">{t("dayUnits", { n: formatUnits(units, language) })}</span>}
            </span>
          </header>
          <ul className="card meals">
            {day.entries.map((entry) => {
              if (entry.type === "exercise") {
                return (
                  <li key={entry.id}>
                    <ExerciseRow
                      session={entry.session}
                      now={now}
                      onOpen={() => setExerciseSheet({ mode: "edit", session: entry.session })}
                    />
                  </li>
                );
              }
              if (entry.type === "dose") {
                // Under the meal it was taken for, drawn smaller and joined to it, as part of that meal.
                return (
                  <li key={entry.id} className={attached.has(entry.id) ? "dose-attached" : undefined}>
                    <DoseRow dose={entry.dose} onOpen={() => setDoseSheet(entry.dose)} />
                  </li>
                );
              }
              const meal = entry.meal;
              const carbs = shownTotal(meal.items);
              return (
                <li key={meal.id}>
                  <button type="button" className="meal-row" aria-haspopup="dialog" onClick={() => setOpenId(meal.id)}>
                    <MealThumb meal={meal} />
                    <span className="item-text">
                      <span className="item-name">{mealNames(meal)}</span>
                      <span className="item-meta">
                        {formatTime(meal.eatenAt, language)}
                        <OutcomeBadge meal={meal} />
                        {!insulinBelow(meal, attached) && <MealInsulin meal={meal} />}
                      </span>
                    </span>
                    <span className="item-carbs">
                      <span className="item-grams">{formatGrams(carbs.best)}&nbsp;g</span>
                      <span className="item-best">{rationCount(carbs.best, rationSize, language, t)}</span>
                    </span>
                  </button>
                </li>
              );
            })}
          </ul>
        </section>
        );
      })}

      {more && (
        <button type="button" className="button button-secondary" onClick={() => loadPage(meals?.length ?? 0)}>
          {t(searching ? "logSearchOlder" : "loadMore")}
        </button>
      )}

      {doseSheet && (
        <DoseSheet
          dose={doseSheet === "new" ? undefined : doseSheet}
          meals={meals ?? []}
          onClose={() => setDoseSheet(null)}
          onSaved={doseSaved}
          onDelete={(dose) => void removeDose(dose)}
        />
      )}

      {exerciseSheet && (
        <ExerciseSheet
          opening={exerciseSheet}
          onClose={() => setExerciseSheet(null)}
          onSaved={(session, how) => {
            setExerciseSheet(null);
            putSession(session);
            toast({ text: how === "started" ? t("exerciseStarted", { kind: exerciseName(session) }) : t("exerciseSaved") });
          }}
          onDelete={(session) => void removeExercise(session)}
        />
      )}

      {open && (
        <MealSheet
          meal={open}
          estimates={estimates}
          labels={labels}
          meals={meals ?? []}
          doses={doses}
          onDoseSaved={doseSaved}
          onDoseDelete={(dose) => void removeDose(dose)}
          onClose={() => setOpenId(null)}
          onChanged={() => {
            setOpenId(null);
            onChanged();
            void loadPage(0);
          }}
        />
      )}
    </div>
  );
}

// The rapid insulin she took for a meal, beside its time, drawn as a rapid dose is; a dose she took
// for other meals too is counted whole and said to be shared.
function MealInsulin({ meal }: { meal: Meal }) {
  const { t, language } = useSettings();
  const units = rapidUnits(meal);
  if (units === 0) {
    return null;
  }
  const shown = formatUnits(units, language);
  const shared = meal.doses.some((dose) => dose.kind === "rapid" && dose.shared);
  const spoken = t("mealInsulinSpoken", { n: shown });
  return (
    <span className="meal-insulin" aria-label={shared ? `${spoken}, ${t("doseShared")}` : spoken}>
      <Icon name="bolt" size={16} />
      {t("unitsCount", { n: shown })}
      {shared && <span className="meal-insulin-shared">{t("doseShared")}</span>}
    </span>
  );
}

// Rapid and basal are told apart three ways: the word, a bolt or a clock, and the mark's tint.
function DoseRow({ dose, onOpen }: { dose: Dose; onOpen: () => void }) {
  const { t, language } = useSettings();
  return (
    <button type="button" className={`meal-row dose-row dose-${dose.kind}`} aria-haspopup="dialog" onClick={onOpen}>
      <span className={`meal-thumb dose-mark dose-mark-${dose.kind}`}>
        <Icon name={DOSE_ICON[dose.kind]} />
      </span>
      <span className="item-text">
        <span className="item-name">{t("unitsCount", { n: formatUnits(dose.units, language) })}</span>
        <span className="item-meta">
          {formatTime(dose.takenAt, language)}
          <span className="dose-kind-word">{t(`dose_${dose.kind}`)}</span>
          {dose.note && <span className="dose-note">{dose.note}</span>}
        </span>
      </span>
    </button>
  );
}

// The session going now, in place of the shortcuts: what it is, since when, the minutes so far and
// Stop. Past the longest a session can be, it says so instead of offering to stop it now.
function RunningCard({ session, now, onStop, onOpen }: { session: Exercise; now: Date; onStop: () => void; onOpen: () => void }) {
  const { t, language } = useSettings();
  const name = session.name ?? t(`exercise_${session.kind}`);
  return (
    <section className="card exercise-running" aria-label={`${name} · ${t("exerciseRunning")}`}>
      <div className="exercise-running-row">
        <button type="button" className="meal-row" aria-haspopup="dialog" onClick={onOpen}>
          <span className="meal-thumb exercise-mark">
            <Icon name={EXERCISE_ICON[session.kind]} />
          </span>
          <span className="item-text">
            <span className="item-name">{name}</span>
            <span className="item-meta">
              {t("exerciseSince", { time: formatTime(session.startedAt, language) })}
              <span className="exercise-elapsed">
                {canStopNow(session, now) ? duration(elapsedMinutes(session, now), t) : t("exerciseTooLong")}
              </span>
            </span>
          </span>
        </button>
        {canStopNow(session, now) ? (
          <button type="button" className="button button-primary exercise-stop" onClick={onStop}>
            <Icon name="stop" size={20} />
            {t("exerciseStop")}
          </button>
        ) : (
          <button type="button" className="button button-secondary exercise-stop" aria-haspopup="dialog" onClick={onOpen}>
            {t("exerciseSetEnd")}
          </button>
        )}
      </div>
    </section>
  );
}

// A session in the day: its kind or name, when it started and ended, and how long, or that it is
// still going.
function ExerciseRow({ session, now, onOpen }: { session: Exercise; now: Date; onOpen: () => void }) {
  const { t, language } = useSettings();
  const minutes = sessionMinutes(session);
  return (
    <button type="button" className="meal-row exercise-row" aria-haspopup="dialog" onClick={onOpen}>
      <span className="meal-thumb exercise-mark">
        <Icon name={EXERCISE_ICON[session.kind]} />
      </span>
      <span className="item-text">
        <span className="item-name">{session.name ?? t(`exercise_${session.kind}`)}</span>
        <span className="item-meta">
          {session.endedAt === null
            ? formatTime(session.startedAt, language)
            : t("exerciseRange", {
                start: formatTime(session.startedAt, language),
                end: formatTime(session.endedAt, language),
              })}
          {session.note && <span className="dose-note">{session.note}</span>}
        </span>
      </span>
      <span className="item-carbs">
        <span className="item-grams">{duration(minutes ?? elapsedMinutes(session, now), t)}</span>
        {minutes === null && <span className="item-best">{t("exerciseRunning")}</span>}
      </span>
    </button>
  );
}

// The other meals a shared dose was for, by their foods, or by how many where they are not loaded.
function sharedWith(dose: Dose, meal: Meal, meals: readonly Meal[], t: ReturnType<typeof useSettings>["t"]): string {
  const others = dose.mealIds.filter((id) => id !== meal.id);
  const named = others.map((id) => meals.find((m) => m.id === id)).filter((m): m is Meal => m !== undefined);
  return named.length === others.length ? named.map(mealNames).join(" · ")
    : others.length === 1 ? t("doseOtherMeal") : t("doseOtherMeals", { n: others.length });
}

function MealSheet({
  meal,
  estimates,
  labels,
  meals,
  doses,
  onDoseSaved,
  onDoseDelete,
  onClose,
  onChanged,
}: {
  meal: Meal;
  estimates: boolean;
  labels: boolean;
  meals: readonly Meal[];
  doses: readonly Dose[];
  onDoseSaved: (dose: Dose) => void;
  onDoseDelete: (dose: Dose) => void;
  onClose: () => void;
  onChanged: () => void;
}) {
  const { t, language, rationSize } = useSettings();
  // A dose of this meal opened over its sheet, a new one for it, or none.
  const [doseOpen, setDoseOpen] = useState<Dose | "new" | null>(null);
  const linked = meal.doses
    .map((link) => doses.find((dose) => dose.id === link.id))
    .filter((dose): dose is Dose => dose !== undefined)
    .toSorted((a, b) => a.takenAt.localeCompare(b.takenAt));
  const [confirm, confirming] = useConfirm();
  const [when, setWhen] = useState(() => toLocalInput(meal.eatenAt));
  const [outcome, setOutcome] = useState(meal.outcome);
  const [notes, setNotes] = useState(meal.notes ?? "");
  // Its foods as she is changing them, half of what she shared say, kept with the rest on Save.
  const [items, setItems] = useState(() => meal.items.map(fromLogged));
  const [editing, setEditing] = useState<string | null>(null);
  // Where a food she forgot is being added from, or none.
  const [adding, setAdding] = useState<"foods" | "packaged" | "manual" | null>(null);
  // The model asked about the meal again, and what about.
  const [revising, setRevising] = useState<Revision | null>(null);
  const add = (item: DraftItem) => {
    setItems((current) => [...current, item]);
    setAdding(null);
  };
  const editingItem = items.find((item) => item.id === editing) ?? null;
  const toast = useToast();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const carbs = shownTotal(items);

  async function run(work: () => Promise<unknown>) {
    setBusy(true);
    try {
      await work();
      onChanged();
    } catch (failure) {
      setError(errorMessage(failure, t, "save"));
      setBusy(false);
    }
  }

  const save = () =>
    run(async () => {
      await api.updateMeal(meal.id, {
        ...asSaved(meal, outcome),
        eatenAt: when ? new Date(when).toISOString() : meal.eatenAt,
        notes: notes.trim() || null,
        items: items.map(toMealItem),
      });
      toast({ text: t("mealChangesSaved") });
    });

  // Save waits for a change; closed with one not saved, a food halved or a correction taken, it
  // asks before it goes.
  const unsaved = mealEdited(meal, { items, notes, when, outcome });
  async function leave() {
    if (
      !unsaved ||
      (await confirm({ title: t("editDiscardTitle"), body: t("editDiscardBody"), confirm: t("editDiscardYes"), cancel: t("editDiscardNo") }))
    ) {
      onClose();
    }
  }

  return (
    <Sheet
      title={`${sentence(formatDay(new Date(meal.eatenAt), language))} · ${formatTime(meal.eatenAt, language)}`}
      onClose={() => void leave()}
      footer={
        <button type="button" className="button button-primary button-big" disabled={busy || !unsaved} onClick={save}>
          {t("saveChanges")}
        </button>
      }
    >
      <div className="sheet-form">
        {/* What was logged, and the range it came with, each said for what it is. */}
        <dl className="meal-figures">
          <div>
            <dt>{t("mealLogged")}</dt>
            <dd className="meal-logged">
              {formatGrams(carbs.best)}&nbsp;g
            </dd>
          </div>
          {isRange(carbs) && (
            <div>
              <dt>{t("mealRange")}</dt>
              <dd className="meal-range">
                {formatGrams(carbs.low)}–{formatGrams(carbs.high)}&nbsp;g
              </dd>
            </div>
          )}
          <div>
            <dt>{t("mealRations")}</dt>
            <dd>{rationText({ ...carbs, low: carbs.best, high: carbs.best }, rationSize, language, t)}</dd>
          </div>
        </dl>

        {/* Low, in range and high side by side, in that order, as on the pending cards. */}
        <fieldset className="question">
          <legend>{t("outcomeTitle")}</legend>
          <div className="outcome-buttons">
            {RECORDED.map((option) => (
              <button
                key={option}
                type="button"
                className={`outcome-button outcome-button-${option}`}
                aria-pressed={outcome === option}
                onClick={() => setOutcome(option)}
              >
                <Icon name={OUTCOME_ICON[option]} size={20} />
                {t(`outcome_${option}`)}
              </button>
            ))}
          </div>
          {outcome === "unknown" ? (
            <p className="muted small outcome-none">{t("outcome_unknown")}</p>
          ) : (
            <button type="button" className="button button-quiet button-small outcome-none" onClick={() => setOutcome("unknown")}>
              {t("outcomeClear")}
            </button>
          )}
        </fieldset>

        {/* Her insulin for this meal: each dose opens to change it, and one can be added later. */}
        <section className="meal-doses" aria-labelledby="meal-doses-title">
          <h3 id="meal-doses-title" className="field-label">
            {t("mealDosesTitle")}
          </h3>
          {linked.length > 0 && (
            <ul className="rows">
              {linked.map((dose) => (
                <li key={dose.id}>
                  <button type="button" className="row-link meal-dose" aria-haspopup="dialog" onClick={() => setDoseOpen(dose)}>
                    <span className={`dose-mark dose-mark-small dose-mark-${dose.kind}`}>
                      <Icon name={DOSE_ICON[dose.kind]} size={18} />
                    </span>
                    <span className="item-text">
                      <span className="item-name">{t("unitsCount", { n: formatUnits(dose.units, language) })}</span>
                      <span className="item-meta">
                        {formatTime(dose.takenAt, language)} · {t(`dose_${dose.kind}`)}
                      </span>
                      {dose.mealIds.length > 1 && (
                        <span className="item-meta">{t("doseSharedWith", { meals: sharedWith(dose, meal, meals, t) })}</span>
                      )}
                    </span>
                    <Icon name="chevronRight" size={20} />
                  </button>
                </li>
              ))}
            </ul>
          )}
          <button type="button" className="button button-secondary meal-dose-add" aria-haspopup="dialog" onClick={() => setDoseOpen("new")}>
            <Icon name="plus" size={20} />
            {t("mealAddDose")}
          </button>
        </section>

        {/* Her foods, each opening to change how much she had, with what was off and what she forgot. */}
        <section className="meal-foods" aria-labelledby="meal-foods-title">
          <h3 id="meal-foods-title" className="field-label">
            {t("itemsTitle")}
          </h3>
          <ul className="items">
            {items.map((item) => (
              <ItemRow key={item.id} item={item} onOpen={() => setEditing(item.id)} />
            ))}
          </ul>
          {estimates && items.some((item) => item.source === "estimate") && (
            <button type="button" className="row-button" aria-haspopup="dialog" onClick={() => setRevising({ kind: "meal" })}>
              <Icon name="pencil" size={20} />
              <span>{t("correctionToggle")}</span>
              <Icon name="chevronRight" size={20} />
            </button>
          )}
        </section>
        {/* What she forgot, added the ways a meal is built, with the same sheets. */}
        <section className="add-more" aria-labelledby="meal-add-title">
          <h3 id="meal-add-title" className="field-label">
            {t("addMore")}
          </h3>
          {estimates && (
            <button type="button" className="row-button" aria-haspopup="dialog" onClick={() => setRevising({ kind: "add" })}>
              <Icon name="camera" size={20} />
              <span>{t("reviseAdd")}</span>
              <Icon name="chevronRight" size={20} />
            </button>
          )}
          <div className="tiles">
            <button type="button" className="tile" aria-haspopup="dialog" onClick={() => setAdding("foods")}>
              <Icon name="jar" size={26} />
              {t("myFoods")}
            </button>
            <button type="button" className="tile" aria-haspopup="dialog" onClick={() => setAdding("packaged")}>
              <Icon name="barcode" size={26} />
              {t(labels ? "packaged" : "packagedHintBarcode")}
            </button>
            <button type="button" className="tile" aria-haspopup="dialog" onClick={() => setAdding("manual")}>
              <Icon name="pencil" size={26} />
              {t("manual")}
            </button>
          </div>
        </section>

        {meal.hasPhoto && <img className="meal-photo" src={api.photoUrl(meal.id)} alt="" />}

        <label className="field">
          <span className="field-label">{t("notes")}</span>
          <textarea
            rows={3}
            value={notes}
            placeholder={t("notesPlaceholder")}
            onChange={(event) => setNotes(event.target.value)}
          />
        </label>
        <label className="field">
          <span className="field-label">{t("when")}</span>
          <input type="datetime-local" value={when} onChange={(event) => setWhen(event.target.value)} />
        </label>

        {error && (
          <p className="notice notice-error" role="alert">
            <Icon name="alert" size={18} /> <span>{error}</span>
          </p>
        )}
        <button
          type="button"
          className="button button-quiet danger"
          disabled={busy}
          onClick={async () => {
            const body = meal.doses.length > 0 ? t("deleteBodyDoses") : t("deleteBody");
            if (await confirm({ title: t("deleteTitle"), body, confirm: t("deleteMeal") })) {
              void run(() => api.deleteMeal(meal.id));
            }
          }}
        >
          <Icon name="trash" size={20} />
          {t("deleteMeal")}
        </button>
      </div>
      {confirming}
      {editingItem && (
        <PortionSheet
          item={editingItem}
          onChange={(next) => setItems((current) => current.map((item) => (item.id === next.id ? next : item)))}
          // A meal keeps one food at least; the whole meal goes from Delete.
          onRemove={
            items.length > 1
              ? () => {
                  setItems((current) => current.filter((item) => item.id !== editingItem.id));
                  setEditing(null);
                }
              : undefined
          }
          // Only the model's own foods: a label's or her pantry's numbers are exact, and would be lost.
          onAsk={
            estimates && editingItem.source === "estimate" ? () => setRevising({ kind: "food", id: editingItem.id }) : undefined
          }
          onClose={() => setEditing(null)}
        />
      )}
      {revising && (
        <ReviseSheet
          revision={revising}
          meal={meal}
          items={items}
          onApply={(next) => {
            setItems(next);
            setRevising(null);
            setEditing(null);
          }}
          onClose={() => setRevising(null)}
        />
      )}
      {adding === "foods" && <FoodsSheet labels={labels} onClose={() => setAdding(null)} onAdd={add} />}
      {adding === "packaged" && <PackagedSheet labels={labels} onClose={() => setAdding(null)} onAdd={add} />}
      {adding === "manual" && <ManualSheet onClose={() => setAdding(null)} onAdd={add} />}
      {doseOpen && (
        <DoseSheet
          dose={doseOpen === "new" ? undefined : doseOpen}
          forMeal={doseOpen === "new" ? meal : undefined}
          meals={meals}
          onClose={() => setDoseOpen(null)}
          onSaved={(dose) => {
            setDoseOpen(null);
            onDoseSaved(dose);
          }}
          onDelete={(dose) => {
            setDoseOpen(null);
            onDoseDelete(dose);
          }}
        />
      )}
    </Sheet>
  );
}
