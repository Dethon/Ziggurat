import { useState } from "react";
import type {
  Dose,
  Exercise,
  Food,
  Pen,
  Period,
  Recipe,
  SaveDose,
  SaveExercise,
  SaveFood,
  SavePen,
  SavePeriod,
  SaveRecipe,
} from "./types";

// What a sheet opened with, sent beside its change so the server lays only what this sheet changed
// over the record as it is now: someone else may have saved it meanwhile (the server's StaleEdits).
export const openedRecipe = (recipe: Recipe): SaveRecipe => ({
  name: recipe.name,
  cookedGrams: recipe.cookedGrams,
  servings: recipe.servings,
  ingredients: recipe.ingredients.map(({ foodId, amount }) => ({ foodId, amount })),
});

export const openedFood = (food: Food): SaveFood => ({
  name: food.name,
  carbsPer100g: food.carbsPer100g,
  unit: food.unit,
  portionGrams: food.portionGrams,
  portionCarbs: food.portionCarbs,
  barcode: food.barcode,
  source: food.source,
});

export const openedDose = ({ takenAt, units, kind, note, mealIds }: Dose): SaveDose => ({ takenAt, units, kind, note, mealIds });

export const openedPen = ({ kind, insulin, openedOn, days }: Pen): SavePen => ({ kind, insulin, openedOn, days });

export const openedExercise = ({ kind, name, startedAt, endedAt, note }: Exercise): SaveExercise => ({
  kind,
  name,
  startedAt,
  endedAt,
  note,
});

export const openedPeriod = ({ start, end }: Period): SavePeriod => ({ start, end });

// A sheet shows its record in its own terms (a time to the minute, a number as typed), so even a
// field she did not touch would go out different from the record and be taken for a change, and
// another phone's change to that field for a clash. Each field she left as the sheet showed it goes
// back as recorded; only what she changed goes as the sheet has it.
export function overRecord<T extends object>(recorded: T, shown: T, now: T): T {
  const same = (key: keyof T) => JSON.stringify(shown[key]) === JSON.stringify(now[key]);
  return Object.fromEntries(
    (Object.keys(now) as (keyof T)[]).map((key) => [key, same(key) && key in recorded ? recorded[key] : now[key]]),
  ) as T;
}

// The values a sheet showed as it opened, for overRecord.
export function useShown<T>(values: T): T {
  const [shown] = useState(values);
  return shown;
}
