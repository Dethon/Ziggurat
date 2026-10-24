import { describe, expect, it } from "vitest";
import { canStartNext, DEFAULT_PEN_DAYS, nextPen, openPens, penProblem, penStatus, replacedPens } from "./pens";
import type { Pen } from "./types";

const pen = (over: Partial<Pen> = {}): Pen => ({
  id: "a",
  kind: "rapid",
  insulin: "Fiasp",
  openedOn: "2026-10-01",
  days: 28,
  replaceOn: "2026-10-29",
  replacedOn: null,
  ...over,
});

describe("penStatus", () => {
  // The server says when; the app only counts from today to it.
  it("counts the days left to the day it is replaced", () => {
    expect(penStatus(pen(), "2026-10-23")).toEqual({ kind: "left", days: 6 });
  });

  it("says tomorrow the day before", () => {
    expect(penStatus(pen(), "2026-10-28")).toEqual({ kind: "tomorrow" });
  });

  it("says today on the day", () => {
    expect(penStatus(pen(), "2026-10-29")).toEqual({ kind: "today" });
  });

  it("counts the days past it", () => {
    expect(penStatus(pen(), "2026-11-01")).toEqual({ kind: "overdue", days: 3 });
  });
});

describe("openPens", () => {
  it("finds the open pen of each kind, and none for a kind she has not started", () => {
    const rapid = pen({ id: "new", openedOn: "2026-10-20" });
    const old = pen({ id: "old", replacedOn: "2026-10-20" });
    expect(openPens([rapid, old])).toEqual({ rapid, basal: undefined });
  });
});

describe("replacedPens", () => {
  it("lists a kind's replaced pens, the latest first", () => {
    const first = pen({ id: "1", openedOn: "2026-09-01", replacedOn: "2026-09-29" });
    const second = pen({ id: "2", openedOn: "2026-09-29", replacedOn: "2026-10-27" });
    const basal = pen({ id: "3", kind: "basal", replacedOn: "2026-10-01" });
    expect(replacedPens([first, pen(), second, basal], "rapid").map((p) => p.id)).toEqual(["2", "1"]);
  });
});

describe("nextPen", () => {
  // The same insulin and how long it lasts; only the day is new.
  it("starts today with the same insulin and days", () => {
    expect(nextPen(pen({ days: 56, insulin: "Tresiba", kind: "basal" }), "2026-10-23")).toEqual({
      kind: "basal",
      insulin: "Tresiba",
      openedOn: "2026-10-23",
      days: 56,
    });
  });

  // The server refuses a pen replaced the day it was started: that pen would have lasted no days.
  it("is offered only once the open pen is from before today", () => {
    expect(canStartNext(pen(), "2026-10-02")).toBe(true);
    expect(canStartNext(pen(), "2026-10-01")).toBe(false);
    expect(canStartNext(pen({ openedOn: "2026-10-02" }), "2026-10-01")).toBe(false);
  });

  it("starts a kind's first pen at four weeks", () => {
    expect(DEFAULT_PEN_DAYS).toBe(28);
  });
});

describe("penProblem", () => {
  it("is nothing for a pen the server keeps", () => {
    expect(penProblem("Fiasp", "2026-10-23", "28", "2026-10-23")).toBeNull();
    expect(penProblem("Fiasp", "2026-10-24", "28", "2026-10-23")).toBeNull();
  });

  it("names what is missing or out of range, as the server would refuse it", () => {
    expect(penProblem("  ", "2026-10-23", "28", "2026-10-23")).toBe("penNoInsulin");
    expect(penProblem("x".repeat(41), "2026-10-23", "28", "2026-10-23")).toBe("penInsulinTooLong");
    expect(penProblem("Fiasp", "", "28", "2026-10-23")).toBe("penNoOpenedOn");
    expect(penProblem("Fiasp", "2026-10-25", "28", "2026-10-23")).toBe("penOpenedInFuture");
    expect(penProblem("Fiasp", "2026-10-23", "0", "2026-10-23")).toBe("penDays");
    expect(penProblem("Fiasp", "2026-10-23", "121", "2026-10-23")).toBe("penDays");
    expect(penProblem("Fiasp", "2026-10-23", "2.5", "2026-10-23")).toBe("penDays");
    expect(penProblem("Fiasp", "2026-10-23", "", "2026-10-23")).toBe("penDays");
  });
});
