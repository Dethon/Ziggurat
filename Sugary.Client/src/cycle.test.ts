import { describe, expect, it } from "vitest";
import { addDays, cycleStatus, daysBetween, history, localDay, MAX_PERIOD_DAYS, periodProblem } from "./cycle";
import type { Period } from "./types";

const period = (start: string, end: string | null, id = start): Period => ({ id, start, end });

describe("days", () => {
  it("reads the phone's own calendar day, not UTC's", () => {
    expect(localDay(new Date(2026, 9, 16, 0, 30))).toBe("2026-10-16");
    expect(localDay(new Date(2026, 9, 16, 23, 59))).toBe("2026-10-16");
  });

  // Day arithmetic never meets a clock, so the night the clocks change is a day like any other.
  it("adds and counts days across months, years and clock changes", () => {
    expect(addDays("2026-10-31", 1)).toBe("2026-11-01");
    expect(addDays("2026-03-28", 2)).toBe("2026-03-30");
    expect(addDays("2027-01-01", -1)).toBe("2026-12-31");
    expect(daysBetween("2026-10-24", "2026-10-26")).toBe(2);
    expect(daysBetween("2026-02-01", "2026-03-01")).toBe(28);
    expect(daysBetween("2026-10-05", "2026-10-01")).toBe(-4);
  });
});

describe("cycleStatus", () => {
  it("has nothing to say before her first period", () => {
    expect(cycleStatus([], "2026-10-16")).toEqual({ kind: "none" });
  });

  it("counts the day of an ongoing period from its start, its first day being day 1", () => {
    const ongoing = period("2026-10-14", null);
    expect(cycleStatus([ongoing], "2026-10-16")).toEqual({ kind: "period", day: 3, period: ongoing });
    expect(cycleStatus([ongoing], "2026-10-14")).toEqual({ kind: "period", day: 1, period: ongoing });
  });

  it("counts the day of her cycle from the latest start once the period has ended", () => {
    const periods = [period("2026-09-20", "2026-09-24"), period("2026-08-23", "2026-08-27")];
    expect(cycleStatus(periods, "2026-10-06")).toEqual({ kind: "cycle", day: 17, since: "2026-09-20" });
  });

  // An ended period recorded up to today is still today's period.
  it("is in a period on any day an ended period covers", () => {
    const covering = period("2026-10-12", "2026-10-16");
    expect(cycleStatus([covering], "2026-10-16")).toEqual({ kind: "period", day: 5, period: covering });
  });

  // A start up to tomorrow is kept (her phone can be ahead of home); it is not today's cycle yet.
  it("leaves out a period that starts after today", () => {
    const periods = [period("2026-10-17", null), period("2026-09-20", "2026-09-24")];
    expect(cycleStatus(periods, "2026-10-16")).toEqual({ kind: "cycle", day: 27, since: "2026-09-20" });
  });
});

describe("history", () => {
  it("lists her periods newest first, each with its length and the cycle it began", () => {
    const rows = history([period("2026-08-23", "2026-08-27"), period("2026-10-18", null), period("2026-09-20", "2026-09-24")]);
    expect(rows.map((row) => [row.period.start, row.days, row.cycleDays])).toEqual([
      ["2026-10-18", null, null],
      ["2026-09-20", 5, 28],
      ["2026-08-23", 5, 28],
    ]);
  });

  it("counts both ends of a period", () => {
    expect(history([period("2026-10-01", "2026-10-01")])[0].days).toBe(1);
  });
});

describe("periodProblem", () => {
  const today = "2026-10-16";

  it("lets a period through that breaks no rule", () => {
    expect(periodProblem("2026-10-14", null, today, [])).toBeNull();
    expect(periodProblem("2026-09-01", "2026-09-05", today, [period("2026-10-01", "2026-10-05")])).toBeNull();
  });

  it("refuses an end before its start", () => {
    expect(periodProblem("2026-10-05", "2026-10-01", today, [])).toBe("periodEndBeforeStart");
  });

  it("refuses a day after today", () => {
    expect(periodProblem("2026-10-17", null, today, [])).toBe("periodFuture");
    expect(periodProblem("2026-10-14", "2026-10-17", today, [])).toBe("periodFuture");
  });

  it("refuses more than the longest a period can be, and keeps the longest itself", () => {
    expect(MAX_PERIOD_DAYS).toBe(31);
    expect(periodProblem("2026-01-01", "2026-01-31", today, [])).toBeNull();
    expect(periodProblem("2026-01-01", "2026-02-01", today, [])).toBe("periodTooLong");
  });

  it("refuses a day another period already has, naming that period", () => {
    const other = period("2026-09-03", "2026-09-07");
    expect(periodProblem("2026-09-07", "2026-09-09", today, [other])).toEqual({ key: "periodOverlaps", start: "2026-09-03" });
    expect(periodProblem("2026-08-30", null, today, [other])).toEqual({ key: "periodOverlaps", start: "2026-09-03" });
  });

  it("says an ongoing period must end before a later one starts", () => {
    expect(periodProblem("2026-10-01", "2026-10-04", today, [period("2026-09-03", null)])).toEqual({
      key: "periodStillOpen",
      start: "2026-09-03",
    });
  });
});
