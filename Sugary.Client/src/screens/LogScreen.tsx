import { useCallback, useEffect, useState } from "react";
import { api } from "../api";
import { formatGrams, formatRations, total } from "../carbs";
import { useConfirm } from "../components/Confirm";
import { Icon } from "../components/Icon";
import { Sheet } from "../components/Sheet";
import { errorMessage } from "../errors";
import { formatDay, formatTime } from "../format";
import { useSettings } from "../settings";
import type { Meal, Outcome } from "../types";

const PAGE = 50;
const OUTCOMES: Outcome[] = ["low", "inRange", "high", "unknown"];

const dayKey = (date: Date) => `${date.getFullYear()}-${date.getMonth()}-${date.getDate()}`;

// datetime-local works in the device's own time zone and has no zone suffix.
function toLocalInput(iso: string): string {
  const date = new Date(iso);
  return new Date(date.getTime() - date.getTimezoneOffset() * 60_000).toISOString().slice(0, 16);
}

export function LogScreen() {
  const { t, language, rationSize } = useSettings();
  const [meals, setMeals] = useState<Meal[] | null>(null);
  const [more, setMore] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [open, setOpen] = useState<Meal | null>(null);

  const loadPage = useCallback(
    async (skip: number) => {
      try {
        const page = await api.meals(skip, PAGE);
        setMeals((current) => (skip === 0 ? page : [...(current ?? []), ...page]));
        setMore(page.length === PAGE);
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

  const days = (meals ?? []).reduce<{ key: string; date: Date; meals: Meal[] }[]>((groups, meal) => {
    const date = new Date(meal.eatenAt);
    const last = groups.at(-1);
    if (last?.key === dayKey(date)) {
      last.meals.push(meal);
    } else {
      groups.push({ key: dayKey(date), date, meals: [meal] });
    }
    return groups;
  }, []);

  return (
    <div className="screen">
      <h1>{t("tabLog")}</h1>
      {error && (
        <p className="notice notice-error" role="alert">
          {error}
        </p>
      )}
      {meals?.length === 0 && (
        <div className="empty">
          <Icon name="book" size={36} />
          <p className="empty-title">{t("logEmpty")}</p>
          <p className="muted small">{t("logEmptyHint")}</p>
        </div>
      )}

      {days.map((day) => (
        <section key={day.key} className="day">
          <header className="day-header">
            <h2>{dayLabel(day.date)}</h2>
            <span className="day-total">
              {t("dayTotal", { n: formatGrams(total(day.meals.flatMap((meal) => meal.items)).best) })}
            </span>
          </header>
          <ul className="card meals">
            {day.meals.map((meal) => {
              const carbs = total(meal.items);
              return (
                <li key={meal.id}>
                  <button type="button" className="meal-row" onClick={() => setOpen(meal)}>
                    {meal.hasPhoto ? (
                      <img className="meal-thumb" src={api.photoUrl(meal.id)} alt="" loading="lazy" />
                    ) : (
                      <span className="meal-thumb meal-thumb-empty">
                        <Icon name="plate" />
                      </span>
                    )}
                    <span className="item-text">
                      <span className="item-name">{meal.items.map((item) => item.name).join(", ")}</span>
                      <span className="item-meta">
                        {formatTime(meal.eatenAt, language)}
                        {meal.outcome !== "unknown" && (
                          <span className={`outcome outcome-${meal.outcome}`}>{t(`outcome_${meal.outcome}`)}</span>
                        )}
                      </span>
                    </span>
                    <span className="item-carbs">
                      <span className="item-grams">{formatGrams(carbs.best)} g</span>
                      <span className="item-meta">
                        {t("rations", { n: formatRations(carbs.best, rationSize, language) })}
                      </span>
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
          {t("loadMore")}
        </button>
      )}

      {open && (
        <MealSheet
          meal={open}
          onClose={() => setOpen(null)}
          onChanged={() => {
            setOpen(null);
            void loadPage(0);
          }}
        />
      )}
    </div>
  );
}

function MealSheet({ meal, onClose, onChanged }: { meal: Meal; onClose: () => void; onChanged: () => void }) {
  const { t } = useSettings();
  const [confirm, confirming] = useConfirm();
  const [when, setWhen] = useState(() => toLocalInput(meal.eatenAt));
  const [outcome, setOutcome] = useState(meal.outcome);
  const [notes, setNotes] = useState(meal.notes ?? "");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const carbs = total(meal.items);

  async function run(work: () => Promise<unknown>) {
    setBusy(true);
    try {
      await work();
      onChanged();
    } catch (failure) {
      setError(errorMessage(failure, t));
      setBusy(false);
    }
  }

  const save = () =>
    run(() =>
      api.updateMeal(meal.id, {
        eatenAt: when ? new Date(when).toISOString() : meal.eatenAt,
        notes: notes.trim() || null,
        outcome,
        photo: null,
        firstEstimate: meal.firstEstimate,
        items: meal.items,
      }),
    );

  return (
    <Sheet title={t("mealTitle")} onClose={onClose}>
      <div className="sheet-form">
        {meal.hasPhoto && <img className="meal-photo" src={api.photoUrl(meal.id)} alt="" />}
        <p className="meal-total">
          {formatGrams(carbs.best)} <span className="total-unit">{t("carbsUnit")}</span>
          {Math.round(carbs.high) > Math.round(carbs.low) && (
            <span className="muted small">
              {" "}
              · {formatGrams(carbs.low)}–{formatGrams(carbs.high)}
            </span>
          )}
        </p>
        <ul className="items compact">
          {meal.items.map((item, index) => (
            <li key={index} className="item-line">
              <span className="item-text">
                <span className="item-name">{item.name}</span>
                {item.portion && <span className="item-meta">{item.portion}</span>}
              </span>
              <span className="item-grams">{formatGrams(item.carbs)} g</span>
            </li>
          ))}
        </ul>

        <fieldset className="question">
          <legend>{t("outcomeTitle")}</legend>
          <div className="chips">
            {OUTCOMES.map((option) => (
              <button
                key={option}
                type="button"
                className={outcome === option ? "chip chip-on" : "chip"}
                aria-pressed={outcome === option}
                onClick={() => setOutcome(option)}
              >
                {t(`outcome_${option}`)}
              </button>
            ))}
          </div>
        </fieldset>

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
            {error}
          </p>
        )}
        <button type="button" className="button button-primary button-big" disabled={busy} onClick={save}>
          {t("saveChanges")}
        </button>
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
          {t("deleteMeal")}
        </button>
      </div>
      {confirming}
    </Sheet>
  );
}
