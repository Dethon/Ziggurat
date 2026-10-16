import { describe, expect, it } from "vitest";
import {
  byDay,
  onPeriod,
  matches,
  OUTCOME_AFTER_HOURS,
  outcomeState,
  pendingOutcomes,
  rapidUnits,
  timeline,
  withDose,
  withoutDose,
} from "./log";
import type { Dose, Meal } from "./types";

const now = new Date("2026-10-15T20:00:00Z");
const meal = (id: string, hoursAgo: number, outcome: Meal["outcome"] = "unknown", names = ["toast"]): Meal => ({
  id,
  eatenAt: new Date(now.getTime() - hoursAgo * 3_600_000).toISOString(),
  notes: null,
  outcome,
  hasPhoto: false,
  firstEstimate: null,
  doses: [],
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
    const meals = [meal("old", 50), meal("lunch", 6), meal("noted", 3, "inRange"), meal("breakfast", 12), meal("tea", 2.5)];
    expect(pendingOutcomes(meals, now).map((m) => m.id)).toEqual(["tea", "lunch", "breakfast"]);
  });

  it("leaves out a meal dated in the future", () => {
    expect(pendingOutcomes([meal("later", -2)], now)).toEqual([]);
  });

  it("does not ask about a meal saved moments ago, only once its glucose can say something", () => {
    expect(OUTCOME_AFTER_HOURS).toBe(2);
    expect(pendingOutcomes([meal("just saved", 0.1), meal("an hour ago", 1.9)], now)).toEqual([]);
    expect(pendingOutcomes([meal("two hours ago", 2)], now).map((m) => m.id)).toEqual(["two hours ago"]);
  });
});

describe("outcomeState", () => {
  it("tells noted, waiting, too early and never noted apart", () => {
    expect(outcomeState(meal("noted", 3, "high"), now)).toBe("noted");
    expect(outcomeState(meal("early", 1), now)).toBe("early");
    expect(outcomeState(meal("waiting", 5), now)).toBe("waiting");
    expect(outcomeState(meal("old", 60), now)).toBe("unnoted");
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

const dose = (id: string, hoursAgo: number, kind: Dose["kind"] = "rapid"): Dose => ({
  id,
  takenAt: new Date(now.getTime() - hoursAgo * 3_600_000).toISOString(),
  units: 4,
  kind,
  note: null,
  mealId: null,
});

describe("timeline", () => {
  it("lists meals and doses together, newest first, as the day happened", () => {
    const entries = timeline([meal("lunch", 6), meal("breakfast", 12)], [dose("basal", 2, "basal"), dose("correction", 9)]);
    expect(entries.map((entry) => entry.id)).toEqual(["basal", "lunch", "correction", "breakfast"]);
    expect(entries.map((entry) => entry.type)).toEqual(["dose", "meal", "dose", "meal"]);
  });

  it("puts a meal above a dose taken at the same moment, which reads as the dose taken for it", () => {
    const lunch = meal("lunch", 6);
    const withIt = { ...dose("bolus", 0), takenAt: lunch.eatenAt };
    expect(timeline([lunch], [withIt]).map((entry) => entry.id)).toEqual(["lunch", "bolus"]);
  });

  it("orders by the instant, whatever offset each time was written with", () => {
    const early = { ...meal("early", 0), eatenAt: "2026-10-15T10:00:00+02:00" };
    const late = { ...dose("late", 0), takenAt: "2026-10-15T09:30:00Z" };
    expect(timeline([early], [late]).map((entry) => entry.id)).toEqual(["late", "early"]);
  });
});

describe("byDay", () => {
  // Built on the phone's own clock, so the days are the same wherever the tests run.
  const at = (day: number, hour: number) => new Date(2026, 9, day, hour, 0).toISOString();

  it("groups the timeline by the day on the phone's calendar, keeping its order", () => {
    const meals = [{ ...meal("lunch", 0), eatenAt: at(15, 14) }, { ...meal("yesterday", 0), eatenAt: at(14, 23) }];
    const doses = [{ ...dose("night", 0, "basal"), takenAt: at(15, 22) }];
    const days = byDay(timeline(meals, doses));
    expect(days.map((day) => day.entries.map((entry) => entry.id))).toEqual([["night", "lunch"], ["yesterday"]]);
    expect(days[0].date.getDate()).toBe(15);
  });

  it("gives a day of doses alone a day of its own", () => {
    expect(byDay(timeline([], [dose("basal", 2, "basal")])).map((day) => day.entries.length)).toEqual([1]);
  });
});

describe("rapidUnits", () => {
  it("adds up the rapid insulin linked to a meal, and leaves basal out", () => {
    const lunch = {
      ...meal("lunch", 3),
      doses: [
        { id: "a", units: 4.5, kind: "rapid" as const },
        { id: "b", units: 1.25, kind: "rapid" as const },
        { id: "c", units: 14, kind: "basal" as const },
      ],
    };
    expect(rapidUnits(lunch)).toBe(5.75);
    expect(rapidUnits(meal("none", 3))).toBe(0);
  });

  it("does not drift on decimals", () => {
    const tea = { ...meal("tea", 3), doses: [0.1, 0.2].map((units, i) => ({ id: `${i}`, units, kind: "rapid" as const })) };
    expect(rapidUnits(tea)).toBe(0.3);
  });
});

describe("withDose and withoutDose", () => {
  const lunch = { ...meal("lunch", 3), doses: [{ id: "d", units: 4, kind: "rapid" as const }] };
  const tea = meal("tea", 1);

  it("moves a dose to the meal it now belongs to, and off the one it left", () => {
    const moved = withDose([lunch, tea], { ...dose("d", 1), units: 5, mealId: "tea" });
    expect(moved.map((m) => m.doses)).toEqual([[], [{ id: "d", units: 5, kind: "rapid" }]]);
  });

  it("unlinks a dose that belongs to no meal now", () => {
    expect(withDose([lunch, tea], { ...dose("d", 1), mealId: null }).map((m) => m.doses.length)).toEqual([0, 0]);
  });

  it("takes a deleted dose off its meal", () => {
    expect(withoutDose([lunch, tea], "d").map((m) => m.doses.length)).toEqual([0, 0]);
  });
});

describe("onPeriod", () => {
  const periods = [
    { id: "p1", start: "2026-09-09", end: "2026-09-14" },
    { id: "p2", start: "2026-10-14", end: null },
  ];
  const today = "2026-10-16";

  // The Log's days are the phone's calendar days, and so are a period's.
  it("marks a day inside a recorded period, by the phone's calendar", () => {
    expect(onPeriod(new Date(2026, 8, 9, 0, 10), periods, today)).toBe(true);
    expect(onPeriod(new Date(2026, 8, 14, 23, 50), periods, today)).toBe(true);
    expect(onPeriod(new Date(2026, 8, 15, 0, 10), periods, today)).toBe(false);
  });

  it("marks an ongoing period up to today and no further", () => {
    expect(onPeriod(new Date(2026, 9, 16, 12), periods, today)).toBe(true);
    expect(onPeriod(new Date(2026, 9, 17, 12), periods, today)).toBe(false);
  });
});
