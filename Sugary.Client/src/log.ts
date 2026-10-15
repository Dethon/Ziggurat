import { plain } from "./foods";
import type { Meal } from "./types";

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
