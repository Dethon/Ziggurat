import { covers, localDay } from "./cycle";
import { plain } from "./foods";
import { fromLogged, toMealItem } from "./carbs";
import { toLocalInput } from "./format";
import type { Dose, DraftItem, Exercise, LinkedDose, Meal, Outcome, Period } from "./types";

const HOUR = 3_600_000;
// Glucose says little about a meal before about two hours, so a meal is asked about from then on.
export const OUTCOME_AFTER_HOURS = 2;
// How long after a meal its outcome is still asked for at the top of the log.
export const PENDING_HOURS = 48;

// Where a meal's outcome stands: noted, asked for now, too soon to ask, or never noted.
export type OutcomeState = "noted" | "waiting" | "early" | "unnoted";

export function outcomeState(meal: Meal, now = new Date()): OutcomeState {
  if (meal.outcome !== "unknown") {
    return "noted";
  }
  const age = now.getTime() - new Date(meal.eatenAt).getTime();
  if (age < OUTCOME_AFTER_HOURS * HOUR) {
    return "early";
  }
  return age <= PENDING_HOURS * HOUR ? "waiting" : "unnoted";
}

// Meals from two to forty-eight hours ago whose glucose she has not noted yet, newest first.
export function pendingOutcomes(meals: readonly Meal[], now = new Date()): Meal[] {
  return meals
    .filter((meal) => outcomeState(meal, now) === "waiting")
    .toSorted((a, b) => b.eatenAt.localeCompare(a.eatenAt));
}

// A meal is found by any of its foods, without minding accents or case: "platano" finds "Plátano".
export function matches(meal: Meal, query: string): boolean {
  const wanted = plain(query);
  return wanted === "" || meal.items.some((item) => plain(item.name).includes(wanted));
}

// One row of the Log's day: a meal, a dose or a session of exercise, at the instant it happened
// (a session at its start).
export type Entry =
  | { type: "meal"; id: string; at: number; meal: Meal }
  | { type: "dose"; id: string; at: number; dose: Dose }
  | { type: "exercise"; id: string; at: number; session: Exercise };

// Meals, doses and exercise as one day reads, newest first. A dose taken at the same moment as a
// meal (one saved with it) comes under it, as the dose taken for it.
export function timeline(meals: readonly Meal[], doses: readonly Dose[], sessions: readonly Exercise[] = []): Entry[] {
  return [
    ...meals.map((meal): Entry => ({ type: "meal", id: meal.id, at: Date.parse(meal.eatenAt), meal })),
    ...doses.map((dose): Entry => ({ type: "dose", id: dose.id, at: Date.parse(dose.takenAt), dose })),
    ...sessions.map((session): Entry => ({ type: "exercise", id: session.id, at: Date.parse(session.startedAt), session })),
  ].toSorted((a, b) => b.at - a.at || (a.type === b.type ? 0 : a.type === "meal" ? -1 : 1));
}

// The doses drawn as part of a meal they were taken for, each with the meal it is drawn under: linked
// to the meal right above them, or to the same meal as the linked dose right above them. A dose for
// several meals is drawn under whichever of them it sits right under. Each is still a row of its own.
export function attachedDoses(entries: readonly Entry[]): Map<string, string> {
  const attached = new Map<string, string>();
  let meal: string | null = null;
  for (const entry of entries) {
    if (entry.type === "meal") {
      meal = entry.meal.id;
    } else if (entry.type === "dose" && meal !== null && entry.dose.mealIds.includes(meal)) {
      attached.set(entry.id, meal);
    } else {
      meal = null;
    }
  }
  return attached;
}

// Whether every rapid dose of a meal, the ones its own count adds up, is drawn right under it, so the
// meal need not count them itself. A shared dose drawn under the other meal is not under this one.
export function insulinBelow(meal: Meal, attached: ReadonlyMap<string, string>): boolean {
  const rapid = meal.doses.filter((dose) => dose.kind === "rapid");
  return rapid.length > 0 && rapid.every((dose) => attached.get(dose.id) === meal.id);
}

// Every unit of insulin in these rows, rapid and basal, summed in hundredths as rapidUnits does.
export function dayUnits(entries: readonly Entry[]): number {
  return entries.reduce((sum, entry) => sum + (entry.type === "dose" ? Math.round(entry.dose.units * 100) : 0), 0) / 100;
}

export interface Day {
  key: string;
  date: Date;
  entries: Entry[];
}

export const dayKey = (date: Date) => `${date.getFullYear()}-${date.getMonth()}-${date.getDate()}`;

// The timeline cut into the days of the phone's own calendar, in the order it came.
export function byDay(entries: readonly Entry[]): Day[] {
  return entries.reduce<Day[]>((days, entry) => {
    const date = new Date(entry.at);
    const last = days.at(-1);
    if (last?.key === dayKey(date)) {
      last.entries.push(entry);
    } else {
      days.push({ key: dayKey(date), date, entries: [entry] });
    }
    return days;
  }, []);
}

// The rapid insulin she took for a meal, however many doses it came in. Basal belongs to no meal's
// count, even when linked to one. Summed in hundredths, the smallest step a dose is recorded in.
export function rapidUnits(meal: Meal): number {
  return meal.doses.filter((dose) => dose.kind === "rapid").reduce((sum, dose) => sum + Math.round(dose.units * 100), 0) / 100;
}

// The meals as they stand once a dose is saved: off whichever meals it was on, onto each it names,
// whole on each and marked shared where it names more than one.
export function withDose(meals: readonly Meal[], dose: Dose): Meal[] {
  const linked: LinkedDose = { id: dose.id, units: dose.units, kind: dose.kind, shared: dose.mealIds.length > 1 };
  return withoutDose(meals, dose.id).map((meal) =>
    dose.mealIds.includes(meal.id) ? { ...meal, doses: [...meal.doses, linked] } : meal,
  );
}

export function withoutDose(meals: readonly Meal[], id: string): Meal[] {
  return meals.map((meal) => (meal.doses.some((dose) => dose.id === id) ? { ...meal, doses: meal.doses.filter((dose) => dose.id !== id) } : meal));
}

// Whether a day of the Log was one of her period days, both read on the phone's calendar.
export function onPeriod(date: Date, periods: readonly Period[], today: string): boolean {
  const day = localDay(date);
  return periods.some((period) => covers(period, day, today));
}

// A logged meal as its sheet has it, and whether closing it would lose anything: her foods, her
// notes, its time or its outcome changed and not saved.
export interface MealEdit {
  items: readonly DraftItem[];
  notes: string;
  when: string;
  outcome: Outcome;
}

export function mealEdited(meal: Meal, edit: MealEdit): boolean {
  const foods = (items: readonly DraftItem[]) => JSON.stringify(items.map(toMealItem));
  return (
    foods(edit.items) !== foods(meal.items.map(fromLogged)) ||
    edit.notes.trim() !== (meal.notes ?? "").trim() ||
    edit.when !== toLocalInput(meal.eatenAt) ||
    edit.outcome !== meal.outcome
  );
}
