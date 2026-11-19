import { describe, expect, it } from "vitest";
import { dayTaken, latest, measurementProblem, showsCycle } from "./profile";
import type { Measurement } from "./types";

describe("the Cycle tab", () => {
  it("is shown to a woman and to anyone who has not said, so nobody already logging periods loses it", () => {
    expect(showsCycle({ sex: "female", diabetesType: null, diagnosedOn: null })).toBe(true);
    expect(showsCycle({ sex: null, diabetesType: null, diagnosedOn: null })).toBe(true);
    expect(showsCycle(null)).toBe(true);
  });

  it("is hidden from a man", () => {
    expect(showsCycle({ sex: "male", diabetesType: "type1", diagnosedOn: null })).toBe(false);
  });
});

describe("a measurement as typed", () => {
  const today = "2026-11-19";

  it("is refused as the server would refuse it", () => {
    expect(measurementProblem("height", "", today, "2026-11-19")).toBe("measurementNoValue");
    expect(measurementProblem("height", "162", "", today)).toBe("measurementNoDay");
    expect(measurementProblem("height", "162", "2026-11-21", today)).toBe("measurementInFuture");
    expect(measurementProblem("height", "1.62", "2026-11-19", today)).toBe("heightOutOfRange");
    expect(measurementProblem("weight", "580", "2026-11-19", today)).toBe("weightOutOfRange");
    expect(measurementProblem("weight", "abc", "2026-11-19", today)).toBe("measurementNoValue");
  });

  it("takes a decimal comma, and a day ahead of her phone's", () => {
    expect(measurementProblem("weight", "58,4", "2026-11-20", today)).toBeNull();
    expect(measurementProblem("height", "151.5", "2026-11-19", today)).toBeNull();
  });
});

describe("the latest of a kind", () => {
  const rows: Measurement[] = [
    { id: "a", kind: "height", on: "2026-09-01", value: 154 },
    { id: "b", kind: "weight", on: "2026-10-01", value: 41 },
    { id: "c", kind: "height", on: "2026-03-01", value: 151.5 },
  ];

  it("is the newest day's", () => {
    expect(latest(rows, "height")?.value).toBe(154);
    expect(latest(rows, "weight")?.value).toBe(41);
    expect(latest([], "weight")).toBeUndefined();
  });
});

describe("a day with one already", () => {
  const rows: Measurement[] = [{ id: "a", kind: "weight", on: "2026-10-01", value: 41 }];

  it("is taken for a second of that kind, and free for the other kind or the one being corrected", () => {
    expect(dayTaken(rows, "weight", "2026-10-01")).toBe(true);
    expect(dayTaken(rows, "height", "2026-10-01")).toBe(false);
    expect(dayTaken(rows, "weight", "2026-10-01", "a")).toBe(false);
  });
});
