import type { StringKey } from "./i18n";
import type { Dose, Meal, SaveDose } from "./types";

// The most one dose can be. It catches a slipped digit, not a clinical limit, and the server holds
// the same bound: a number over it is refused, never clamped into one she did not type.
export const MAX_UNITS = 100;
export const MAX_NOTE = 200;

export type UnitsProblem = Extract<StringKey, "unitsMissing" | "unitsNotANumber" | "unitsAboveZero" | "unitsAtMost" | "unitsDecimals">;

// The units she typed, read as she wrote them (a comma or a point for the decimals, as the portion
// inputs take), or why they cannot be a dose. Nothing here ever supplies a number of its own.
export function readUnits(text: string): number | UnitsProblem {
  const typed = text.trim();
  if (typed === "") {
    return "unitsMissing";
  }
  const match = /^(\d*)(?:[.,](\d*))?$/.exec(typed);
  if (!match || (match[1] === "" && !match[2])) {
    return "unitsNotANumber";
  }
  const decimals = (match[2] ?? "").replace(/0+$/, "");
  const units = Number(`${match[1] || "0"}.${match[2] || "0"}`);
  if (units <= 0) {
    return "unitsAboveZero";
  }
  if (units > MAX_UNITS) {
    return "unitsAtMost";
  }
  return decimals.length > 2 ? "unitsDecimals" : units;
}

// A deleted dose as it is saved again when she undoes the deletion.
export function restored({ takenAt, units, kind, note, mealId }: Dose): SaveDose {
  return { takenAt, units, kind, note, mealId };
}

// About a day: a dose added on its own is linked to a meal she has just eaten, or is about to.
const LINKABLE_HOURS = 24;

// The meals a dose can be linked to: those of about the last day, newest first, and the meal it
// already belongs to wherever that is, so opening an older dose never quietly unlinks it.
export function linkable(meals: readonly Meal[], now: Date, current: string | null = null): Meal[] {
  const since = now.getTime() - LINKABLE_HOURS * 3_600_000;
  return meals
    .filter((meal) => meal.id === current || Date.parse(meal.eatenAt) >= since)
    .toSorted((a, b) => Date.parse(b.eatenAt) - Date.parse(a.eatenAt));
}
