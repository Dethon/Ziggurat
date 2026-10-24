import { daysBetween } from "./cycle";
import type { StringKey } from "./i18n";
import type { DoseKind, Pen, SavePen } from "./types";

// What a kind's first pen starts at: most rapid pens and many basal ones last four weeks once open.
export const DEFAULT_PEN_DAYS = 28;

// The server's bounds, said before saving: a slipped digit, not a clinical limit.
export const MAX_PEN_DAYS = 120;
export const MAX_INSULIN = 40;

export const PEN_KINDS: readonly DoseKind[] = ["rapid", "basal"];

export type PenStatus =
  | { kind: "left"; days: number }
  | { kind: "tomorrow" }
  | { kind: "today" }
  | { kind: "overdue"; days: number };

// How the open pen stands against the day the server says it is replaced.
export function penStatus(pen: Pen, today: string): PenStatus {
  const left = daysBetween(today, pen.replaceOn);
  if (left > 1) {
    return { kind: "left", days: left };
  }
  if (left === 1) {
    return { kind: "tomorrow" };
  }
  return left === 0 ? { kind: "today" } : { kind: "overdue", days: -left };
}

export function openPens(pens: readonly Pen[]): Record<DoseKind, Pen | undefined> {
  const open = (kind: DoseKind) => pens.find((pen) => pen.kind === kind && pen.replacedOn === null);
  return { rapid: open("rapid"), basal: open("basal") };
}

export function replacedPens(pens: readonly Pen[], kind: DoseKind): Pen[] {
  return pens
    .filter((pen) => pen.kind === kind && pen.replacedOn !== null)
    .toSorted((a, b) => b.openedOn.localeCompare(a.openedOn));
}

// A pen started today is corrected, not replaced: the server keeps no pen that lasted no days.
export function canStartNext(pen: Pen, today: string): boolean {
  return pen.openedOn < today;
}

// The pen after this one: the same insulin lasting as long, first used today.
export function nextPen({ kind, insulin, days }: Pen, today: string): SavePen {
  return { kind, insulin, openedOn: today, days };
}

export type PenProblem = Extract<
  StringKey,
  "penNoInsulin" | "penInsulinTooLong" | "penNoOpenedOn" | "penOpenedInFuture" | "penDays"
>;

// Why the server would refuse the pen as typed, or null. A day ahead is allowed, as the server
// allows it: her phone can be ahead of the home's calendar.
export function penProblem(insulin: string, openedOn: string, days: string, today: string): PenProblem | null {
  const name = insulin.trim();
  if (name === "") {
    return "penNoInsulin";
  }
  if (name.length > MAX_INSULIN) {
    return "penInsulinTooLong";
  }
  if (openedOn === "") {
    return "penNoOpenedOn";
  }
  if (daysBetween(today, openedOn) > 1) {
    return "penOpenedInFuture";
  }
  return /^\d+$/.test(days.trim()) && Number(days) >= 1 && Number(days) <= MAX_PEN_DAYS ? null : "penDays";
}
