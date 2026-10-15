import { describe, expect, it } from "vitest";
import { matches, pendingOutcomes } from "./log";
import type { Meal } from "./types";

const now = new Date("2026-10-15T20:00:00Z");
const meal = (id: string, hoursAgo: number, outcome: Meal["outcome"] = "unknown", names = ["toast"]): Meal => ({
  id,
  eatenAt: new Date(now.getTime() - hoursAgo * 3_600_000).toISOString(),
  notes: null,
  outcome,
  hasPhoto: false,
  firstEstimate: null,
  items: names.map((name) => ({
    name,
    portion: null,
    grams: null,
    carbsPer100g: null,
    carbsLow: 10,
    carbs: 10,
    carbsHigh: 10,
    source: "manual",
    confidence: null,
    edited: false,
  })),
});

describe("pendingOutcomes", () => {
  it("asks about the last two days' meals with no outcome, newest first", () => {
    const meals = [meal("old", 50), meal("lunch", 6), meal("noted", 3, "inRange"), meal("breakfast", 12), meal("now", 1)];
    expect(pendingOutcomes(meals, now).map((m) => m.id)).toEqual(["now", "lunch", "breakfast"]);
  });

  it("leaves out a meal dated in the future", () => {
    expect(pendingOutcomes([meal("later", -2)], now)).toEqual([]);
  });
});

describe("matches", () => {
  it("finds a meal by any of its foods, ignoring accents and case", () => {
    const paella = meal("p", 1, "unknown", ["Paella de marisco", "Plátano"]);
    expect(matches(paella, "platano")).toBe(true);
    expect(matches(paella, "MARISCO")).toBe(true);
    expect(matches(paella, "pizza")).toBe(false);
    expect(matches(paella, "  ")).toBe(true);
  });
});
