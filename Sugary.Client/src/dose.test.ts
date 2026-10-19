import { describe, expect, it } from "vitest";
import { doseStart, linkable, MAX_UNITS, readUnits, restored, stepUnits } from "./dose";
import type { Meal } from "./types";

describe("readUnits", () => {
  it("reads what she typed, with a comma or a point for the decimals", () => {
    expect(readUnits("4")).toBe(4);
    expect(readUnits("4,5")).toBe(4.5);
    expect(readUnits(" 4.5 ")).toBe(4.5);
    expect(readUnits("0,25")).toBe(0.25);
    expect(readUnits(",5")).toBe(0.5);
    expect(readUnits("4.50")).toBe(4.5);
  });

  it("says why a number cannot be a dose", () => {
    expect(readUnits("")).toBe("unitsMissing");
    expect(readUnits("  ")).toBe("unitsMissing");
    expect(readUnits("0")).toBe("unitsAboveZero");
    expect(readUnits("0,00")).toBe("unitsAboveZero");
    expect(readUnits("-2")).toBe("unitsNotANumber");
    expect(readUnits("1,125")).toBe("unitsDecimals");
    expect(readUnits("4 u")).toBe("unitsNotANumber");
    expect(readUnits("1e2")).toBe("unitsNotANumber");
    expect(readUnits("1,2,3")).toBe("unitsNotANumber");
  });

  // A slipped digit is refused, never clamped into a number she did not type.
  it("refuses more than the most a dose can be, and keeps the most itself", () => {
    expect(MAX_UNITS).toBe(100);
    expect(readUnits("100")).toBe(100);
    expect(readUnits("100,01")).toBe("unitsAtMost");
    expect(readUnits("140")).toBe("unitsAtMost");
  });
});

describe("restored", () => {
  // Undoing a deletion saves the dose again as it was: everything she recorded, a new id.
  it("keeps every value she recorded", () => {
    const dose = { id: "d1", takenAt: "2026-10-15T08:00:00.000Z", units: 4.5, kind: "basal", note: "night", mealId: "m1" } as const;
    expect(restored(dose)).toEqual({ takenAt: dose.takenAt, units: 4.5, kind: "basal", note: "night", mealId: "m1" });
  });
});

describe("linkable", () => {
  const taken = new Date("2026-10-15T20:00:00Z");
  const meal = (id: string, hoursBefore: number): Meal => ({
    id,
    eatenAt: new Date(taken.getTime() - hoursBefore * 3_600_000).toISOString(),
    notes: null,
    outcome: "unknown",
    hasPhoto: false,
    firstEstimate: null,
    doses: [],
    items: [],
  });

  it("offers the meals of about a day either side of the dose, closest to it first", () => {
    const meals = [meal("breakfast", 12), meal("old", 30), meal("lunch", 6), meal("snack", 1), meal("supper", -2)];
    expect(linkable(meals, taken).map((m) => m.id)).toEqual(["snack", "supper", "lunch", "breakfast"]);
  });

  // A dose recorded for an earlier day is offered that day's meals, not the ones around today.
  it("follows the dose's time rather than the newest meals", () => {
    const yesterday = new Date(taken.getTime() - 24 * 3_600_000);
    const meals = [meal("today", 0), meal("yesterdayDinner", 23), meal("yesterdayLunch", 30), meal("weekAgo", 168)];
    expect(linkable(meals, yesterday).map((m) => m.id)).toEqual([
      "yesterdayDinner",
      "yesterdayLunch",
      "today",
    ]);
  });

  // An older dose opened to change keeps its meal on offer, or saving it would quietly unlink it.
  it("keeps the meal a dose already belongs to on offer", () => {
    const meals = [meal("lunch", 6), meal("old", 30)];
    expect(linkable(meals, taken, "old").map((m) => m.id)).toEqual(["lunch", "old"]);
  });
});

describe("stepUnits", () => {
  it("moves what she typed by one unit, written back in her language", () => {
    expect(stepUnits("4", 1, "en")).toBe("5");
    expect(stepUnits("4", -1, "en")).toBe("3");
    expect(stepUnits("4,5", 1, "es")).toBe("5,5");
    expect(stepUnits("4.5", -1, "en")).toBe("3.5");
    expect(stepUnits("0,25", 1, "es")).toBe("1,25");
  });

  // A plus from nothing is her own tap; a minus from nothing has nowhere to go.
  it("starts an empty field at one unit on a plus and leaves it empty on a minus", () => {
    expect(stepUnits("", 1, "en")).toBe("1");
    expect(stepUnits("  ", 1, "en")).toBe("1");
    expect(stepUnits("", -1, "en")).toBeNull();
  });

  it("clears the field rather than step to no dose or below", () => {
    expect(stepUnits("1", -1, "en")).toBe("");
    expect(stepUnits("0,5", -1, "es")).toBe("");
    expect(stepUnits("0", -1, "en")).toBe("");
  });

  it("never steps past the most a dose can be", () => {
    expect(stepUnits("99", 1, "en")).toBe("100");
    expect(stepUnits("100", 1, "en")).toBeNull();
    expect(stepUnits("99,5", 1, "es")).toBeNull();
    // A slipped digit is hers to retype; stepping down from it would only walk a wrong number.
    expect(stepUnits("150", -1, "en")).toBeNull();
    expect(stepUnits("1500", -1, "en")).toBeNull();
  });

  // Stepping text that is not a dose would mean guessing what she meant.
  it("leaves alone what it cannot read as she wrote it", () => {
    expect(stepUnits("4 u", 1, "en")).toBeNull();
    expect(stepUnits("1,125", 1, "es")).toBeNull();
    expect(stepUnits("-2", 1, "en")).toBeNull();
  });
});

describe("doseStart", () => {
  const now = new Date("2026-10-15T20:00:00Z");
  const lunch: Meal = {
    id: "lunch",
    eatenAt: "2026-10-15T13:30:00.000Z",
    notes: null,
    outcome: "unknown",
    hasPhoto: false,
    firstEstimate: null,
    doses: [],
    items: [],
  };

  it("starts a dose on its own now, linked to nothing", () => {
    expect(doseStart(undefined, now)).toEqual({ takenAt: now.toISOString(), mealId: null });
  });

  // Added later from the meal itself: the insulin for a meal is taken at the meal, and the time stays hers to change.
  it("starts a dose added from a meal at the meal's time, linked to it", () => {
    expect(doseStart(lunch, now)).toEqual({ takenAt: lunch.eatenAt, mealId: "lunch" });
  });
});
