import { plain } from "./foods";
import type { Meal } from "./types";

// How long after a meal its outcome is still asked for at the top of the log.
export const PENDING_HOURS = 48;

// Meals from the last two days whose glucose she has not noted yet, newest first.
export function pendingOutcomes(meals: readonly Meal[], now = new Date()): Meal[] {
  const since = now.getTime() - PENDING_HOURS * 3_600_000;
  return meals
    .filter((meal) => {
      const eaten = new Date(meal.eatenAt).getTime();
      return meal.outcome === "unknown" && eaten >= since && eaten <= now.getTime();
    })
    .toSorted((a, b) => b.eatenAt.localeCompare(a.eatenAt));
}

// A meal is found by any of its foods, without minding accents or case: "platano" finds "Plátano".
export function matches(meal: Meal, query: string): boolean {
  const wanted = plain(query);
  return wanted === "" || meal.items.some((item) => plain(item.name).includes(wanted));
}
