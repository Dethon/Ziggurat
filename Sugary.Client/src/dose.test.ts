import { describe, expect, it } from "vitest";
import { MAX_UNITS, readUnits, restored } from "./dose";

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
