import type { DraftItem, EstimatedItem, Food, Interval, ItemSource, MealItem, PackagedFood } from "./types";

const round1 = (value: number) => Math.round(value * 10) / 10;

export const newId = () => Math.random().toString(36).slice(2, 10);

export function total(items: readonly Pick<MealItem, "carbsLow" | "carbs" | "carbsHigh">[]): Interval {
  return {
    low: round1(items.reduce((sum, item) => sum + item.carbsLow, 0)),
    best: round1(items.reduce((sum, item) => sum + item.carbs, 0)),
    high: round1(items.reduce((sum, item) => sum + item.carbsHigh, 0)),
  };
}

// Rations are shown to the nearest half, which is the precision carb counting is taught at.
export function rations(grams: number, rationSize: number): number {
  return Math.round((grams / rationSize) * 2) / 2;
}

export function formatGrams(grams: number): string {
  return String(Math.round(grams));
}

export function formatRations(grams: number, rationSize: number, locale: string): string {
  return rations(grams, rationSize).toLocaleString(locale, { maximumFractionDigits: 1 });
}

export function isRange(interval: Interval): boolean {
  return Math.round(interval.high) - Math.round(interval.low) >= 1;
}

export function fromEstimate(item: EstimatedItem): DraftItem {
  return {
    id: newId(),
    name: item.name,
    portion: item.portion || null,
    grams: item.grams.best,
    carbsPer100g: item.carbsPer100g,
    carbsLow: item.carbs.low,
    carbs: item.carbs.best,
    carbsHigh: item.carbs.high,
    source: "estimate",
    confidence: item.confidence,
    note: item.note,
    edited: false,
  };
}

export function fromPackaged(
  food: PackagedFood,
  amount: number,
  name: string,
  source: ItemSource,
  foodId: string | null = null,
): DraftItem {
  const carbs = round1((amount * food.carbsPer100g) / 100);
  return {
    foodId,
    id: newId(),
    name,
    portion: `${formatGrams(amount)} ${food.unit}`,
    grams: amount,
    carbsPer100g: food.carbsPer100g,
    carbsLow: carbs,
    carbs,
    carbsHigh: carbs,
    source,
    confidence: null,
    note: food.note,
    edited: false,
  };
}

// A food from her own list. With a density the amount is a weight; without one it is how many of
// her usual portions she is having.
export function fromFood(food: Food, amount: number): DraftItem {
  if (food.carbsPer100g !== null) {
    return fromPackaged(
      { name: food.name, carbsPer100g: food.carbsPer100g, servingGrams: food.portionGrams, unit: food.unit, note: null },
      amount,
      food.name,
      food.source,
      food.id,
    );
  }
  const carbs = round1(amount * (food.portionCarbs ?? 0));
  return {
    ...manualItem(food.name, carbs),
    portion: amount === 1 ? null : `× ${amount.toLocaleString("en", { maximumFractionDigits: 2 })}`,
    grams: food.portionGrams === null ? null : round1(amount * food.portionGrams),
    source: food.source,
    foodId: food.id,
  };
}

export function manualItem(name: string, carbs: number, foodId: string | null = null): DraftItem {
  return {
    id: newId(),
    name,
    portion: null,
    grams: null,
    carbsPer100g: null,
    carbsLow: carbs,
    carbs,
    carbsHigh: carbs,
    source: "manual",
    confidence: null,
    note: null,
    edited: false,
    foodId,
  };
}

// A weight the person entered is a fact, so the range collapses onto it.
export function withGrams(item: DraftItem, grams: number): DraftItem {
  if (item.carbsPer100g === null) {
    return item;
  }
  const carbs = round1((grams * item.carbsPer100g) / 100);
  return { ...item, grams, portion: null, carbsLow: carbs, carbs, carbsHigh: carbs, edited: true };
}

export function withCarbs(item: DraftItem, carbs: number): DraftItem {
  return { ...item, carbsLow: carbs, carbs, carbsHigh: carbs, grams: null, portion: null, edited: true };
}

export function toMealItem(item: DraftItem): MealItem {
  return {
    name: item.name,
    portion: item.portion,
    grams: item.grams,
    carbsPer100g: item.carbsPer100g,
    carbsLow: item.carbsLow,
    carbs: item.carbs,
    carbsHigh: item.carbsHigh,
    source: item.source,
    confidence: item.confidence,
    edited: item.edited,
    foodId: item.foodId ?? null,
  };
}

export function parseAmount(text: string): number | null {
  const value = Number(text.replace(",", ".").trim());
  return text.trim() !== "" && Number.isFinite(value) && value >= 0 ? value : null;
}
