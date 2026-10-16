import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "../api";
import { formatGrams, isRange, shownTotal } from "../carbs";
import { useConfirm } from "../components/Confirm";
import { Icon, type IconName } from "../components/Icon";
import { CakeStand } from "../components/Illustrations";
import { Sheet } from "../components/Sheet";
import { restored } from "../dose";
import { errorMessage } from "../errors";
import { formatDay, formatTime, formatUnits, rationCount, rationText, toLocalInput } from "../format";
import { byDay, matches, outcomeState, pendingOutcomes, timeline } from "../log";
import { bindUnits, sentence } from "../meal";
import { useMinute } from "../clock";
import { useSettings } from "../settings";
import { useToast } from "../toast";
import type { Dose, Meal, Outcome } from "../types";
import { DoseSheet } from "./DoseSheet";

const PAGE = 50;
const RECORDED: Exclude<Outcome, "unknown">[] = ["low", "inRange", "high"];
const OUTCOME_ICON: Record<Outcome, IconName> = { low: "arrowDown", inRange: "checkCircle", high: "arrowUp", unknown: "minus" };

const dayKey = (date: Date) => `${date.getFullYear()}-${date.getMonth()}-${date.getDate()}`;
const names = (meal: Meal) => meal.items.map((item) => sentence(item.name)).join("\u00a0· ");
const DOSE_ICON = { rapid: "bolt", basal: "clock" } as const;

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

export function LogScreen({ onChanged }: { onChanged: () => void }) {
  const { t, language, rationSize } = useSettings();
  const toast = useToast();
  const [meals, setMeals] = useState<Meal[] | null>(null);
  const loaded = useRef<Meal[]>([]);
  const [doses, setDoses] = useState<Dose[]>([]);
  // The dose sheet: a new dose, one she opened, or closed.
  const [doseSheet, setDoseSheet] = useState<Dose | "new" | null>(null);
  const [more, setMore] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [open, setOpen] = useState<Meal | null>(null);
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
        const taken = await api.allDoses(hasMore && oldest ? startOfDay(oldest.eatenAt) : null);
        loaded.current = all;
        setMeals(all);
        setDoses(taken);
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

  // Gone at once; the toast offers it back, saved again with every value she recorded.
  async function removeDose(dose: Dose) {
    try {
      await api.deleteDose(dose.id);
      setDoseSheet(null);
      setDoses((current) => current.filter((d) => d.id !== dose.id));
      toast({
        text: t("doseDeleted"),
        action: {
          label: t("undo"),
          run: () =>
            void api.createDose(restored(dose)).then(
              (back) => setDoses((current) => [back, ...current]),
              (failure) => setError(errorMessage(failure, t, "save")),
            ),
        },
      });
    } catch (failure) {
      setDoseSheet(null);
      setError(errorMessage(failure, t, "save"));
    }
  }

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
  const days = byDay(timeline(shown, searching ? [] : doses));
  const empty = meals?.length === 0 && doses.length === 0;

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

      {empty && (
        <div className="empty">
          <CakeStand />
          <p className="empty-title">{t("logEmpty")}</p>
          <p className="small">{t("logEmptyHint")}</p>
        </div>
      )}

      {meals !== null && (
        <button type="button" className="row-button add-dose" onClick={() => setDoseSheet("new")}>
          <Icon name="insulin" size={20} />
          <span>{t("addDose")}</span>
          <Icon name="plus" size={20} />
        </button>
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
              <article key={meal.id} className="card pending-card" aria-label={names(meal)}>
                <button type="button" className="meal-row" onClick={() => setOpen(meal)}>
                  <Thumb meal={meal} />
                  <span className="item-text">
                    <span className="item-name">{names(meal)}</span>
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

      {days.map((day) => (
        <section key={day.key} className="day" aria-label={dayLabel(day.date)}>
          <header className="day-header">
            <h2>{dayLabel(day.date)}</h2>
            <span className="day-total">
              {t("dayTotal", {
                n: day.entries.reduce((sum, entry) => sum + (entry.type === "meal" ? shownTotal(entry.meal.items).best : 0), 0),
              })}
            </span>
          </header>
          <ul className="card meals">
            {day.entries.map((entry) => {
              if (entry.type === "dose") {
                return (
                  <li key={entry.id}>
                    <DoseRow dose={entry.dose} onOpen={() => setDoseSheet(entry.dose)} />
                  </li>
                );
              }
              const meal = entry.meal;
              const carbs = shownTotal(meal.items);
              return (
                <li key={meal.id}>
                  <button type="button" className="meal-row" aria-haspopup="dialog" onClick={() => setOpen(meal)}>
                    <Thumb meal={meal} />
                    <span className="item-text">
                      <span className="item-name">{names(meal)}</span>
                      <span className="item-meta">
                        {formatTime(meal.eatenAt, language)}
                        <OutcomeBadge meal={meal} />
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
      ))}

      {more && (
        <button type="button" className="button button-secondary" onClick={() => loadPage(meals?.length ?? 0)}>
          {t(searching ? "logSearchOlder" : "loadMore")}
        </button>
      )}

      {doseSheet && (
        <DoseSheet
          dose={doseSheet === "new" ? undefined : doseSheet}
          onClose={() => setDoseSheet(null)}
          onSaved={(dose) => {
            setDoseSheet(null);
            setDoses((current) => [dose, ...current.filter((d) => d.id !== dose.id)]);
            toast({ text: t("doseSaved") });
          }}
          onDelete={(dose) => void removeDose(dose)}
        />
      )}

      {open && (
        <MealSheet
          meal={open}
          onClose={() => setOpen(null)}
          onChanged={() => {
            setOpen(null);
            onChanged();
            void loadPage(0);
          }}
        />
      )}
    </div>
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

function Thumb({ meal }: { meal: Meal }) {
  return meal.hasPhoto ? (
    <img className="meal-thumb" src={api.photoUrl(meal.id)} alt="" loading="lazy" />
  ) : (
    <span className="meal-thumb meal-thumb-empty">
      <Icon name="plate" />
    </span>
  );
}

function MealSheet({ meal, onClose, onChanged }: { meal: Meal; onClose: () => void; onChanged: () => void }) {
  const { t, language, rationSize } = useSettings();
  const [confirm, confirming] = useConfirm();
  const [when, setWhen] = useState(() => toLocalInput(meal.eatenAt));
  const [outcome, setOutcome] = useState(meal.outcome);
  const [notes, setNotes] = useState(meal.notes ?? "");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const carbs = shownTotal(meal.items);

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
    run(() =>
      api.updateMeal(meal.id, {
        ...asSaved(meal, outcome),
        eatenAt: when ? new Date(when).toISOString() : meal.eatenAt,
        notes: notes.trim() || null,
      }),
    );

  return (
    <Sheet
      title={`${sentence(formatDay(new Date(meal.eatenAt), language))} · ${formatTime(meal.eatenAt, language)}`}
      onClose={onClose}
      footer={
        <button type="button" className="button button-primary button-big" disabled={busy} onClick={save}>
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

        <ul className="items compact">
          {meal.items.map((item, index) => (
            <li key={index} className="item-line">
              <span className="item-text">
                <span className="item-name">{sentence(item.name)}</span>
                {item.portion && <span className="item-meta">{bindUnits(item.portion)}</span>}
              </span>
              <span className="item-grams">{formatGrams(item.carbs)}&nbsp;g</span>
            </li>
          ))}
        </ul>

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
            if (await confirm({ title: t("deleteTitle"), body: t("deleteBody"), confirm: t("deleteMeal") })) {
              void run(() => api.deleteMeal(meal.id));
            }
          }}
        >
          <Icon name="trash" size={20} />
          {t("deleteMeal")}
        </button>
      </div>
      {confirming}
    </Sheet>
  );
}
