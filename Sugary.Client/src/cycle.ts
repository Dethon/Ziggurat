import type { StringKey } from "./i18n";
import type { Period } from "./types";

// A period is days on her calendar, written yyyy-MM-dd, never instants: the day it started is the
// same day wherever it is read. Day arithmetic runs on UTC midnights so no clock change can make a
// day 23 or 25 hours long.

// The longest a period can be, start and end included. The server holds the same bound; it catches
// an end a month out by a slip and refuses it, never moves it.
export const MAX_PERIOD_DAYS = 31;

const DAY = 86_400_000;
const pad = (n: number) => String(n).padStart(2, "0");
const utc = (day: string) => Date.UTC(Number(day.slice(0, 4)), Number(day.slice(5, 7)) - 1, Number(day.slice(8, 10)));

// The day a moment falls on in the phone's own calendar.
export function localDay(date: Date): string {
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

export function addDays(day: string, days: number): string {
  const date = new Date(utc(day) + days * DAY);
  return `${date.getUTCFullYear()}-${pad(date.getUTCMonth() + 1)}-${pad(date.getUTCDate())}`;
}

// How many days from one day to another: 0 for the same day, negative when to is earlier.
export function daysBetween(from: string, to: string): number {
  return Math.round((utc(to) - utc(from)) / DAY);
}

// The day as a local Date, for formatting in her language.
export function dayDate(day: string): Date {
  return new Date(Number(day.slice(0, 4)), Number(day.slice(5, 7)) - 1, Number(day.slice(8, 10)));
}

const byStart = (periods: readonly Period[]) => periods.toSorted((a, b) => a.start.localeCompare(b.start));

// Whether a day is one of a period's: an ongoing one reaches today and no further.
export function covers(period: Period, day: string, today: string): boolean {
  return period.start <= day && day <= (period.end ?? today) && day <= today;
}

// Where she is today: the day of a period that covers today, or the day of the cycle the latest
// period began, or nowhere before she has recorded one.
export type CycleStatus =
  | { kind: "none" }
  | { kind: "period"; day: number; period: Period }
  | { kind: "cycle"; day: number; since: string };

export function cycleStatus(periods: readonly Period[], today: string): CycleStatus {
  const latest = byStart(periods)
    .filter((period) => period.start <= today)
    .at(-1);
  if (!latest) {
    return { kind: "none" };
  }
  const day = daysBetween(latest.start, today) + 1;
  return covers(latest, today, today) ? { kind: "period", day, period: latest } : { kind: "cycle", day, since: latest.start };
}

export interface HistoryRow {
  period: Period;
  // Both ends counted; null while it is going.
  days: number | null;
  // From this start to the next one; null for the latest.
  cycleDays: number | null;
}

// Her periods newest first, each with its length and the length of the cycle it began.
export function history(periods: readonly Period[]): HistoryRow[] {
  const sorted = byStart(periods);
  return sorted
    .map((period, index) => ({
      period,
      days: period.end === null ? null : daysBetween(period.start, period.end) + 1,
      cycleDays: index + 1 < sorted.length ? daysBetween(period.start, sorted[index + 1].start) : null,
    }))
    .toReversed();
}

export type PeriodProblem =
  | Extract<StringKey, "periodEndBeforeStart" | "periodFuture" | "periodTooLong">
  | { key: Extract<StringKey, "periodOverlaps" | "periodStillOpen">; start: string };

// Why these days cannot be a period, by the rules the server holds, or null. Said here in her
// language before the server is asked; others are her periods but the one being changed.
export function periodProblem(start: string, end: string | null, today: string, others: readonly Period[]): PeriodProblem | null {
  if (end !== null && end < start) {
    return "periodEndBeforeStart";
  }
  if (start > today || (end !== null && end > today)) {
    return "periodFuture";
  }
  if (end !== null && daysBetween(start, end) + 1 > MAX_PERIOD_DAYS) {
    return "periodTooLong";
  }
  const clash = byStart(others).find((other) => other.start <= (end ?? "9999-12-31") && start <= (other.end ?? "9999-12-31"));
  if (!clash) {
    return null;
  }
  return { key: clash.end === null && clash.start < start ? "periodStillOpen" : "periodOverlaps", start: clash.start };
}

// One day of the calendar and what is on it.
export interface CalendarDay {
  day: string;
  period: boolean;
  // Forecast, never on a recorded period day.
  expected: boolean;
  fertile: boolean;
  note: boolean;
  today: boolean;
  future: boolean;
}

// A month ("yyyy-MM") in weeks from Monday, as both her languages lay a calendar out; the days
// before the first and after the last are blank.
export function monthGrid(
  month: string,
  periods: readonly Period[],
  noted: ReadonlySet<string>,
  today: string,
  forecasted: Forecast | null = null,
): (CalendarDay | null)[][] {
  const first = `${month}-01`;
  const length = daysBetween(first, `${shiftMonth(month, 1)}-01`);
  const lead = (new Date(utc(first)).getUTCDay() + 6) % 7;
  const cells = [
    ...Array.from({ length: lead }, () => null),
    ...Array.from({ length }, (_, index): CalendarDay => {
      const day = addDays(first, index);
      const period = periods.some((recorded) => covers(recorded, day, today));
      const within = (range: { start: string; end: string } | null | undefined) => !period && !!range && range.start <= day && day <= range.end;
      return {
        day,
        period,
        expected: within(forecasted && { start: forecasted.earliest, end: forecasted.latest }),
        fertile: within(forecasted?.fertile),
        note: noted.has(day),
        today: day === today,
        future: day > today,
      };
    }),
  ];
  const weeks = Math.ceil(cells.length / 7);
  return Array.from({ length: weeks }, (_, week) =>
    Array.from({ length: 7 }, (_, weekday) => cells[week * 7 + weekday] ?? null),
  );
}

export function shiftMonth(month: string, months: number): string {
  const index = Number(month.slice(0, 4)) * 12 + Number(month.slice(5, 7)) - 1 + months;
  return `${Math.floor(index / 12)}-${pad((index % 12) + 1)}`;
}

// What a day the period could be started or ended on asks for: the ongoing period ended on it, or
// one started on it — ongoing when no later period is in its way, otherwise a sheet to say its end.
export type DayAction =
  | { kind: "open"; period: Period }
  | { kind: "end"; period: Period }
  | { kind: "start"; ongoing: boolean };

export function dayAction(day: string, periods: readonly Period[], today: string): DayAction {
  const covering = periods.find((period) => covers(period, day, today));
  if (covering) {
    return { kind: "open", period: covering };
  }
  const ongoing = periods.find((period) => period.end === null && period.start < day);
  if (ongoing) {
    return { kind: "end", period: ongoing };
  }
  return { kind: "start", ongoing: !periods.some((period) => period.start > day) };
}

// The cycles a forecast reads: the most recent, since a cycle drifts over the years.
const RECENT_CYCLES = 6;
// A cycle longer than this many times the median is a period she did not note, not a cycle.
const OUTLIER = 1.5;
// Ovulation is taken as this many days before the next start; she is fertile the five days before
// it and the day after.
const LUTEAL_DAYS = 14;

export interface Forecast {
  // The next start, between the shortest and the longest recent cycle.
  earliest: string;
  latest: string;
  likeliest: string;
  // While its last day is not past.
  fertile: { start: string; end: string } | null;
  // How many cycles it was read from.
  cycles: number;
}

// When her next period is expected and her likely fertile window, from her own recent cycles. An
// estimate, and said to be one wherever it is shown; nothing here is stored or sent.
export function forecast(periods: readonly Period[], today: string): Forecast | null {
  const starts = byStart(periods)
    .filter((period) => period.start <= today)
    .map((period) => period.start);
  if (starts.length < 2) {
    return null;
  }
  const recent = starts
    .slice(1)
    .map((start, index) => daysBetween(starts[index], start))
    .slice(-RECENT_CYCLES);
  const sorted = recent.toSorted((a, b) => a - b);
  const middle = sorted.length / 2;
  const median = sorted.length % 2 ? sorted[Math.floor(middle)] : (sorted[middle - 1] + sorted[middle]) / 2;
  const kept = recent.filter((days) => days <= median * OUTLIER);
  const last = starts.at(-1)!;
  const earliest = addDays(last, Math.min(...kept));
  const latest = addDays(last, Math.max(...kept));
  const fertile = { start: addDays(earliest, -LUTEAL_DAYS - 5), end: addDays(latest, -LUTEAL_DAYS + 1) };
  return {
    earliest,
    latest,
    likeliest: addDays(last, Math.round(kept.reduce((sum, days) => sum + days, 0) / kept.length)),
    fertile: fertile.end >= today ? fertile : null,
    cycles: kept.length,
  };
}
