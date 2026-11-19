import { describe, expect, it } from "vitest";
import { charted, dayTaken, fittingRange, history, latest, measurementProblem, nearest, plotted, showsCycle, spaced, within } from "./profile";
import type { Measurement } from "./types";

describe("the Cycle tab", () => {
  it("is shown to a woman and to anyone who has not said, so nobody already logging periods loses it", () => {
    expect(showsCycle({ sex: "female", diabetesType: null, diagnosedOn: null, bornOn: null })).toBe(true);
    expect(showsCycle({ sex: null, diabetesType: null, diagnosedOn: null, bornOn: null })).toBe(true);
    expect(showsCycle(null)).toBe(true);
  });

  it("is hidden from a man", () => {
    expect(showsCycle({ sex: "male", diabetesType: "type1", diagnosedOn: null, bornOn: null })).toBe(false);
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

describe("a kind's history", () => {
  const rows: Measurement[] = [
    { id: "a", kind: "height", on: "2026-09-01", value: 154 },
    { id: "b", kind: "weight", on: "2026-10-01", value: 41 },
    { id: "c", kind: "height", on: "2026-03-01", value: 151.5 },
  ];

  it("is that kind's alone, newest first", () => {
    expect(history(rows, "height").map((m) => m.id)).toEqual(["a", "c"]);
  });
});

describe("the chart of a kind", () => {
  it("is drawn from two entries on, since one point is a line of nothing and its row already says it", () => {
    const one: Measurement = { id: "a", kind: "height", on: "2026-01-01", value: 120 };
    const two: Measurement = { id: "b", kind: "height", on: "2026-06-01", value: 123 };

    expect(charted([])).toBe(false);
    expect(charted([one])).toBe(false);
    expect(charted([two, one])).toBe(true);
  });

  const frame = { width: 300, height: 100 };

  it("places the days by their distance in time, oldest at the left, and the values between a padded floor and ceiling", () => {
    const rows: Measurement[] = [
      { id: "a", kind: "weight", on: "2026-01-01", value: 40 },
      { id: "b", kind: "weight", on: "2026-01-11", value: 41 },
      { id: "c", kind: "weight", on: "2026-01-31", value: 44 },
    ];
    const chart = plotted(rows, frame, "2026-01-31");

    expect(chart.points.map((p) => p.measurement.id)).toEqual(["a", "b", "c"]);
    expect(chart.points.map((p) => p.x)).toEqual([0, 100, 300]);
    expect(chart.low).toBeLessThan(40);
    expect(chart.high).toBeGreaterThan(44);
    expect(chart.points[0].y).toBeLessThan(100);
    expect(chart.points[0].y).toBeGreaterThan(chart.points[2].y);
  });

  it("runs on to today, the last value held since it was taken", () => {
    const rows: Measurement[] = [
      { id: "a", kind: "height", on: "2026-01-01", value: 140 },
      { id: "b", kind: "height", on: "2026-01-31", value: 142 },
    ];
    const chart = plotted(rows, frame, "2026-03-02");

    expect(chart.points.map((p) => p.x)).toEqual([0, 150]);
    expect(chart.held).toEqual({ x: 300, y: chart.points[1].y });
    expect(plotted(rows, frame, "2026-01-31").held).toBeNull();
  });

  it("puts a lone value, or values that never changed, in the middle", () => {
    const one = plotted([{ id: "a", kind: "height", on: "2026-01-01", value: 150 }], frame, "2026-01-01");
    expect(one.points[0]).toMatchObject({ x: 150, y: 50 });
    const flat = plotted(
      [
        { id: "a", kind: "height", on: "2026-01-01", value: 150 },
        { id: "b", kind: "height", on: "2026-02-01", value: 150 },
      ],
      frame,
      "2026-02-01",
    );
    expect(flat.points.map((p) => p.y)).toEqual([50, 50]);
  });
});

describe("a history's range", () => {
  const today = "2026-11-19";
  const at = (id: string, on: string): Measurement => ({ id, kind: "weight", on, value: 40 });
  const rows = [at("now", "2026-11-01"), at("spring", "2026-03-01"), at("edge", "2025-11-19"), at("old", "2023-05-01"), at("ancient", "2019-01-01")];

  it("ends today and reaches back a year, five years, or to the first entry", () => {
    expect(within(rows, "year", today).map((m) => m.id)).toEqual(["now", "spring", "edge"]);
    expect(within(rows, "fiveYears", today).map((m) => m.id)).toEqual(["now", "spring", "edge", "old"]);
    expect(within(rows, "all", today).map((m) => m.id)).toEqual(["now", "spring", "edge", "old", "ancient"]);
  });

  it("opens on the narrowest range that holds two entries, so a chart is there to see", () => {
    expect(fittingRange(rows, today)).toBe("year");
    expect(fittingRange([at("a", "2026-06-01"), at("b", "2024-01-01")], today)).toBe("fiveYears");
    expect(fittingRange([at("a", "2026-06-01"), at("b", "2015-01-01")], today)).toBe("all");
    expect(fittingRange([at("a", "2015-01-01")], today)).toBe("all");
  });
});

describe("a chart's points under a finger", () => {
  const point = (id: string, x: number) => ({ measurement: { id, kind: "height" as const, on: "2026-01-01", value: 1 }, x, y: 0 });

  it("are each a target only while a fingertip apart, so no point hides another", () => {
    expect(spaced([point("a", 0), point("b", 40), point("c", 80)])).toBe(true);
    expect(spaced([point("a", 0), point("b", 40), point("c", 50)])).toBe(false);
    expect(spaced([point("a", 0)])).toBe(true);
  });

  it("when crowded, give the one nearest the tap along the days", () => {
    const points = [point("a", 0), point("b", 10), point("c", 20)];
    expect(nearest(points, -5).measurement.id).toBe("a");
    expect(nearest(points, 14).measurement.id).toBe("b");
    expect(nearest(points, 16).measurement.id).toBe("c");
    expect(nearest(points, 400).measurement.id).toBe("c");
  });
});
