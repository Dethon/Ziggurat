import { describe, expect, it } from "vitest";
import { fromEstimate, fromFood, fromPackaged, parseAmount, rations, total, withCarbs, withGrams } from "./carbs";
import type { EstimatedItem, Food } from "./types";

const rice: EstimatedItem = {
  name: "rice",
  portion: "a cup",
  grams: { low: 100, best: 150, high: 200 },
  carbsPer100g: 28,
  carbs: { low: 28, best: 42, high: 56 },
  confidence: "medium",
  note: null,
};

describe("rations", () => {
  it("rounds to the nearest half ration", () => {
    expect(rations(42, 10)).toBe(4);
    expect(rations(43, 10)).toBe(4.5);
    expect(rations(42, 15)).toBe(3);
  });
});

describe("draft items", () => {
  it("keeps the model's range until the person corrects it", () => {
    const item = fromEstimate(rice);
    expect([item.carbsLow, item.carbs, item.carbsHigh]).toEqual([28, 42, 56]);
    expect(item.edited).toBe(false);
  });

  it("collapses the range onto a weight the person entered", () => {
    const item = withGrams(fromEstimate(rice), 120);
    expect([item.carbsLow, item.carbs, item.carbsHigh]).toEqual([33.6, 33.6, 33.6]);
    expect(item.edited).toBe(true);
  });

  it("lets the person override the carbs directly", () => {
    const item = withCarbs(fromEstimate(rice), 30);
    expect([item.carbsLow, item.carbs, item.carbsHigh, item.grams]).toEqual([30, 30, 30, null]);
  });

  it("computes packaged food exactly from the label", () => {
    const food = { name: "Corn Flakes", carbsPer100g: 84, servingGrams: 30, unit: "g" as const, note: null };
    const item = fromPackaged(food, 45, "Corn Flakes", "barcode");
    expect(item.carbs).toBe(37.8);
    expect(item.carbsLow).toBe(item.carbsHigh);
  });

  it("sums ranges across items", () => {
    const items = [fromEstimate(rice), withCarbs(fromEstimate(rice), 10)];
    expect(total(items)).toEqual({ low: 38, best: 52, high: 66 });
  });
});

describe("parseAmount", () => {
  it("accepts a comma as the decimal separator", () => {
    expect(parseAmount("12,5")).toBe(12.5);
  });

  it("rejects blanks, text and negatives", () => {
    expect(parseAmount("")).toBeNull();
    expect(parseAmount("abc")).toBeNull();
    expect(parseAmount("-3")).toBeNull();
  });
});

describe("her own foods", () => {
  const bread: Food = {
    id: "food-1",
    name: "Pan de molde",
    carbsPer100g: 46,
    unit: "g",
    portionGrams: 32,
    portionCarbs: null,
    barcode: "8480000823298",
    source: "label",
    uses: 3,
    lastUsedAt: null,
  };
  const toast: Food = { ...bread, id: "food-2", name: "Tostada", carbsPer100g: null, portionGrams: null, portionCarbs: 28, source: "manual" };

  it("turn a weighed amount into exact carbs and remember where they came from", () => {
    const item = fromFood(bread, 64);

    expect(item).toMatchObject({ name: "Pan de molde", grams: 64, carbsPer100g: 46, portion: "64 g", source: "label", foodId: "food-1" });
    expect([item.carbsLow, item.carbs, item.carbsHigh]).toEqual([29.4, 29.4, 29.4]);
  });

  it("count portions when only a portion's carbs are known", () => {
    const item = fromFood(toast, 1.5);

    expect(item).toMatchObject({ grams: null, carbsPer100g: null, portion: "× 1.5", carbs: 42, source: "manual", foodId: "food-2" });
    expect(fromFood(toast, 1).portion).toBeNull();
  });
});
