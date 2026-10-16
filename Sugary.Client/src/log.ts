import { plain } from "./foods";
import type { Dose, Meal } from "./types";

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

// One row of the Log's day: a meal or a dose, at the instant it happened.
export type Entry = { type: "meal"; id: string; at: number; meal: Meal } | { type: "dose"; id: string; at: number; dose: Dose };

// Meals and doses as one day reads, newest first. A dose taken at the same moment as a meal (one
// saved with it) comes under it, as the dose taken for it.
export function timeline(meals: readonly Meal[], doses: readonly Dose[]): Entry[] {
  return [
    ...meals.map((meal): Entry => ({ type: "meal", id: meal.id, at: Date.parse(meal.eatenAt), meal })),
    ...doses.map((dose): Entry => ({ type: "dose", id: dose.id, at: Date.parse(dose.takenAt), dose })),
  ].toSorted((a, b) => b.at - a.at || (a.type === b.type ? 0 : a.type === "meal" ? -1 : 1));
}

export interface Day {
  key: string;
  date: Date;
  entries: Entry[];
}

const dayKey = (date: Date) => `${date.getFullYear()}-${date.getMonth()}-${date.getDate()}`;

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
