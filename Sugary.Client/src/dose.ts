import { formatUnits } from "./format";
import type { Language, StringKey } from "./i18n";
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

// The units after a tap on + or −, written as the field shows them, or null when the tap has nowhere
// to go. A plus from an empty field is her own first unit; a step down to no dose empties the field.
// Text that is not a number with at most two decimals, or is over the most, is left as she wrote it.
export function stepUnits(text: string, delta: 1 | -1, language: Language): string | null {
  const typed = text.trim();
  const match = /^(\d*)(?:[.,](\d{0,2}))?$/.exec(typed);
  if (!match || (typed !== "" && match[1] === "" && !match[2])) {
    return null;
  }
  if (typed === "" && delta < 0) {
    return null;
  }
  // In hundredths, the smallest step a dose is recorded in, so the sum carries no float noise.
  const hundredths = Number(match[1] || "0") * 100 + Number((match[2] ?? "").padEnd(2, "0")) + delta * 100;
  // Past the most, either way: a slipped digit is hers to retype, not to walk down a unit at a time.
  if (hundredths > MAX_UNITS * 100) {
    return null;
  }
  return hundredths <= 0 ? "" : formatUnits(hundredths / 100, language);
}

// A deleted dose as it is saved again when she undoes the deletion.
export function restored({ takenAt, units, kind, note, mealId }: Dose): SaveDose {
  return { takenAt, units, kind, note, mealId };
}

// Where a new dose starts: now and on its own, or, added from a meal, at the meal's time and linked
// to it. Never its units, which are only ever what she types.
export function doseStart(forMeal: Meal | undefined, now: Date): { takenAt: string; mealId: string | null } {
  return forMeal ? { takenAt: forMeal.eatenAt, mealId: forMeal.id } : { takenAt: now.toISOString(), mealId: null };
}

// About a day either side: a dose is linked to a meal she has just eaten, or is about to.
const LINKABLE_HOURS = 24;

// The meals a dose taken at `takenAt` can be linked to: those of about a day either side of it,
// the closest to it first, and the meal it already belongs to wherever that is, so opening an older
// dose never quietly unlinks it. Measured from the dose and not from now, so a dose recorded for
// another day is offered that day's meals.
export function linkable(meals: readonly Meal[], takenAt: Date, current: string | null = null): Meal[] {
  const distance = (meal: Meal) => Math.abs(Date.parse(meal.eatenAt) - takenAt.getTime());
  return meals
    .filter((meal) => meal.id === current || distance(meal) <= LINKABLE_HOURS * 3_600_000)
    .toSorted((a, b) => distance(a) - distance(b));
}
