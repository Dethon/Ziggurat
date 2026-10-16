import { describe, expect, it } from "vitest";
import { addDays, cycleStatus, dayAction, daysBetween, forecast, history, localDay, MAX_PERIOD_DAYS, monthGrid, periodProblem, shiftMonth } from "./cycle";
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

describe("monthGrid", () => {
  const today = "2026-10-16";

  // October 2026 starts on a Thursday and ends on a Saturday.
  it("lays a month out in weeks starting on Monday, with blanks outside it", () => {
    const weeks = monthGrid("2026-10", [], new Set(), today);
    expect(weeks).toHaveLength(5);
    expect(weeks[0].map((cell) => cell?.day ?? null)).toEqual([null, null, null, "2026-10-01", "2026-10-02", "2026-10-03", "2026-10-04"]);
    expect(weeks[4].map((cell) => cell?.day ?? null)).toEqual([
      "2026-10-26",
      "2026-10-27",
      "2026-10-28",
      "2026-10-29",
      "2026-10-30",
      "2026-10-31",
      null,
    ]);
  });

  it("marks her period days, an ongoing period only up to today", () => {
    const periods = [period("2026-09-29", "2026-10-02"), period("2026-10-14", null)];
    const cells = monthGrid("2026-10", periods, new Set(), today).flat();
    const marked = cells.filter((cell) => cell?.period).map((cell) => cell!.day);
    expect(marked).toEqual(["2026-10-01", "2026-10-02", "2026-10-14", "2026-10-15", "2026-10-16"]);
  });

  it("marks the days with a note, today, and the days still to come", () => {
    const cells = monthGrid("2026-10", [], new Set(["2026-10-03", "2026-09-30"]), today).flat();
    expect(cells.filter((cell) => cell?.note).map((cell) => cell!.day)).toEqual(["2026-10-03"]);
    expect(cells.filter((cell) => cell?.today).map((cell) => cell!.day)).toEqual(["2026-10-16"]);
    expect(cells.find((cell) => cell?.day === "2026-10-17")?.future).toBe(true);
    expect(cells.find((cell) => cell?.day === "2026-10-16")?.future).toBe(false);
  });

  // A month starting on a Monday needs no blank before it; February 2027 fills exactly four weeks.
  it("adds no blank week where the month does not need one", () => {
    const weeks = monthGrid("2027-02", [], new Set(), today);
    expect(weeks).toHaveLength(4);
    expect(weeks[0][0]?.day).toBe("2027-02-01");
    expect(weeks[3][6]?.day).toBe("2027-02-28");
  });
});

describe("shiftMonth", () => {
  it("steps across years", () => {
    expect(shiftMonth("2026-12", 1)).toBe("2027-01");
    expect(shiftMonth("2026-01", -1)).toBe("2025-12");
    expect(shiftMonth("2026-10", 0)).toBe("2026-10");
  });
});

describe("dayAction", () => {
  const today = "2026-10-16";

  it("opens the period a day belongs to", () => {
    const covering = period("2026-10-01", "2026-10-05");
    expect(dayAction("2026-10-03", [covering], today)).toEqual({ kind: "open", period: covering });
  });

  it("starts a period with an end on a day a later period follows", () => {
    const ongoing = period("2026-10-12", null);
    expect(dayAction("2026-10-03", [ongoing], today)).toEqual({ kind: "start", ongoing: false });
    expect(dayAction("2026-10-12", [ongoing], today)).toEqual({ kind: "open", period: ongoing });
  });

  it("starts an ongoing period on a day no later period follows", () => {
    expect(dayAction("2026-10-10", [period("2026-09-01", "2026-09-05")], today)).toEqual({ kind: "start", ongoing: true });
  });

  // An ongoing period ending today belongs to today only up to today, so an earlier ongoing one
  // ends on any day after its start.
  it("ends an ongoing period on a day after its start that it does not yet reach", () => {
    const ongoing = period("2026-10-10", null);
    expect(dayAction("2026-10-17", [ongoing], "2026-10-16")).toEqual({ kind: "end", period: ongoing });
  });
});

describe("forecast", () => {
  const today = "2026-10-16";
  const four = [period("2026-07-14", "2026-07-18"), period("2026-08-11", "2026-08-15"), period("2026-09-09", "2026-09-14"), period("2026-10-08", "2026-10-12")];

  it("says nothing before two periods", () => {
    expect(forecast([], today)).toBeNull();
    expect(forecast([period("2026-10-08", null)], today)).toBeNull();
  });

  // Cycles of 28, 29 and 29 days after a start on 8 October.
  it("expects the next start between the shortest and the longest recent cycle, most likely at their mean", () => {
    expect(forecast(four, today)).toMatchObject({ earliest: "2026-11-05", latest: "2026-11-06", likeliest: "2026-11-06", cycles: 3 });
  });

  // Ovulation about 14 days before the next start; fertile the five days before it to the day after.
  it("puts the fertile window from 19 days before the earliest start to 13 before the latest", () => {
    expect(forecast(four, today)?.fertile).toEqual({ start: "2026-10-17", end: "2026-10-24" });
  });

  it("leaves the fertile window out once it is past", () => {
    expect(forecast(four, "2026-10-25")?.fertile).toBeNull();
    expect(forecast(four, "2026-10-24")?.fertile).not.toBeNull();
  });

  it("forecasts from an ongoing period's start", () => {
    const ongoing = [period("2026-09-10", "2026-09-14"), period("2026-10-08", null)];
    expect(forecast(ongoing, "2026-10-10")).toMatchObject({ earliest: "2026-11-05", latest: "2026-11-05", likeliest: "2026-11-05", cycles: 1 });
  });

  // A period she did not note makes one cycle about twice as long; it would drag every forecast out.
  it("leaves out a cycle longer than one and a half times the median", () => {
    const gap = [period("2026-05-19", null), period("2026-06-16", null), period("2026-08-11", null), period("2026-09-09", null)].map(
      (p) => ({ ...p, end: addDays(p.start, 4) }),
    );
    expect(forecast(gap, today)).toMatchObject({ earliest: "2026-10-07", latest: "2026-10-08", cycles: 2 });
  });

  it("reads only the six most recent cycles", () => {
    // A 41-day cycle, short of the outlier rule, before six of 28 days.
    const starts = ["2026-01-19", "2026-03-01", "2026-03-29", "2026-04-26", "2026-05-24", "2026-06-21", "2026-07-19", "2026-08-16"];
    const periods = starts.map((start) => period(start, addDays(start, 4)));
    expect(forecast(periods, "2026-08-20")).toMatchObject({ earliest: "2026-09-13", latest: "2026-09-13", cycles: 6 });
  });
});

describe("monthGrid with a forecast", () => {
  const four = [period("2026-07-14", "2026-07-18"), period("2026-08-11", "2026-08-15"), period("2026-09-09", "2026-09-14"), period("2026-10-08", "2026-10-12")];

  it("draws the expected and fertile days apart from the recorded ones", () => {
    const today = "2026-10-16";
    const cells = monthGrid("2026-11", four, new Set(), today, forecast(four, today)).flat();
    expect(cells.filter((cell) => cell?.expected).map((cell) => cell!.day)).toEqual(["2026-11-05", "2026-11-06"]);
    const october = monthGrid("2026-10", four, new Set(), today, forecast(four, today)).flat();
    expect(october.filter((cell) => cell?.fertile).map((cell) => cell!.day)).toEqual([
      "2026-10-17",
      "2026-10-18",
      "2026-10-19",
      "2026-10-20",
      "2026-10-21",
      "2026-10-22",
      "2026-10-23",
      "2026-10-24",
    ]);
    expect(october.some((cell) => cell?.period && (cell.expected || cell.fertile))).toBe(false);
  });
});
