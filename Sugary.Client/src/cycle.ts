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
