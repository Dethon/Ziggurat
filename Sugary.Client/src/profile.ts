import type { StringKey } from "./i18n";
import type { DiabetesType, Measurement, MeasurementKind, Profile, Sex } from "./types";

export const SEXES: readonly Sex[] = ["female", "male"];
export const DIABETES_TYPES: readonly DiabetesType[] = ["type1", "type2", "lada", "mody", "gestational", "other"];
export const MEASUREMENT_KINDS: readonly MeasurementKind[] = ["height", "weight"];

// The server's bounds: a slipped digit or a height typed in metres, not a clinical limit.
export const BOUNDS: Record<MeasurementKind, { min: number; max: number }> = {
  height: { min: 30, max: 250 },
  weight: { min: 1, max: 400 },
};

export const UNIT: Record<MeasurementKind, "cm" | "kg"> = { height: "cm", weight: "kg" };

export const UNSAID: Profile = { sex: null, diabetesType: null, diagnosedOn: null, bornOn: null };

// Only a man who said so is not shown the Cycle tab: before anyone had said, everybody had it, and a
// woman already logging her periods must not lose it to a field she never filled in.
export function showsCycle(profile: Profile | null): boolean {
  return profile?.sex !== "male";
}

// A number as she typed it, with a comma or a point.
export function parseValue(typed: string): number | null {
  const value = Number(typed.trim().replace(",", "."));
  return typed.trim() === "" || !Number.isFinite(value) ? null : value;
}

export type MeasurementProblem = Extract<
  StringKey,
  "measurementNoValue" | "measurementNoDay" | "measurementInFuture" | "heightOutOfRange" | "weightOutOfRange"
>;

// Why the server would refuse it as typed, or null. A day ahead is allowed, as the server allows it:
// her phone can be ahead of the home's calendar.
export function measurementProblem(kind: MeasurementKind, typed: string, on: string, today: string): MeasurementProblem | null {
  const value = parseValue(typed);
  if (value === null) {
    return "measurementNoValue";
  }
  if (on === "") {
    return "measurementNoDay";
  }
  if (on > dayAfter(today)) {
    return "measurementInFuture";
  }
  const { min, max } = BOUNDS[kind];
  if (value < min || value > max) {
    return kind === "height" ? "heightOutOfRange" : "weightOutOfRange";
  }
  return null;
}

// One of each kind a day, as the server keeps them: a second is a correction of the first. The one
// being corrected (self) does not take its own day.
export function dayTaken(measurements: readonly Measurement[], kind: MeasurementKind, on: string, self?: string): boolean {
  return measurements.some((m) => m.kind === kind && m.on === on && m.id !== self);
}

export function latest(measurements: readonly Measurement[], kind: MeasurementKind): Measurement | undefined {
  return measurements.filter((m) => m.kind === kind).toSorted((a, b) => b.on.localeCompare(a.on))[0];
}

// One kind's measurements, newest first: the list under its chart.
export function history(measurements: readonly Measurement[], kind: MeasurementKind): Measurement[] {
  return measurements.filter((m) => m.kind === kind).toSorted((a, b) => b.on.localeCompare(a.on));
}

// A chart needs two entries to say anything: one is already the row's latest value.
export function charted(history: readonly Measurement[]): boolean {
  return history.length > 1;
}

// How far back a history screen looks: a year, five, or every entry. Each ends today.
export type HistoryRange = "year" | "fiveYears" | "all";
export const HISTORY_RANGES: readonly HistoryRange[] = ["year", "fiveYears", "all"];
const YEARS_BACK: Record<HistoryRange, number | null> = { year: 1, fiveYears: 5, all: null };

export function within(history: readonly Measurement[], range: HistoryRange, today: string): Measurement[] {
  const years = YEARS_BACK[range];
  if (years === null) {
    return [...history];
  }
  const since = `${Number(today.slice(0, 4)) - years}${today.slice(4)}`;
  return history.filter((m) => m.on >= since);
}

// The narrowest range with a chart in it: a child measured twice a year, or a weight last taken years
// ago, would open on an empty year.
export function fittingRange(history: readonly Measurement[], today: string): HistoryRange {
  return HISTORY_RANGES.find((range) => charted(within(history, range, today))) ?? "all";
}

export interface PlottedPoint {
  measurement: Measurement;
  x: number;
  y: number;
}

// One kind's history laid out in a frame that ends today: days placed by their distance in time (a
// year's gap reads as one), values between a floor and a ceiling a little beyond them, so no point sits
// on an edge. The last value is taken as kept since, so `held` is where its line reaches today, or null
// when it was taken today. A lone value, or a run that never changed, sits in the middle.
export function plotted(
  measurements: readonly Measurement[],
  frame: { width: number; height: number },
  today: string,
): { points: PlottedPoint[]; held: { x: number; y: number } | null; low: number; high: number } {
  const sorted = measurements.toSorted((a, b) => a.on.localeCompare(b.on));
  const dayOf = (on: string) => Date.parse(`${on}T00:00:00Z`) / 86_400_000;
  const days = sorted.map((m) => dayOf(m.on));
  const values = sorted.map((m) => m.value);
  const [first, last] = [Math.min(...days), Math.max(dayOf(today), ...days)];
  const [least, most] = [Math.min(...values), Math.max(...values)];
  const pad = most === least ? Math.max(1, most * 0.05) : (most - least) * 0.15;
  const [low, high] = [least - pad, most + pad];
  const points = sorted.map((measurement, index) => ({
    measurement,
    x: last === first ? frame.width / 2 : ((days[index] - first) / (last - first)) * frame.width,
    y: ((high - measurement.value) / (high - low)) * frame.height,
  }));
  const end = points[points.length - 1];
  return { points, held: end && end.x < frame.width ? { x: frame.width, y: end.y } : null, low, high };
}

function dayAfter(day: string): string {
  const date = new Date(`${day}T12:00:00Z`);
  date.setUTCDate(date.getUTCDate() + 1);
  return date.toISOString().slice(0, 10);
}

// A fingertip, in the chart's own units: the width of a point's tap target.
export const FINGERTIP = 32;

// Every point a fingertip from the next along the days, so each can be a target of its own without
// one covering another. Hundreds of entries are not, and are tapped by nearness instead.
export function spaced(points: readonly PlottedPoint[]): boolean {
  return points.every((point, index) => index === 0 || point.x - points[index - 1].x >= FINGERTIP);
}

export function nearest(points: readonly PlottedPoint[], x: number): PlottedPoint {
  return points.reduce((best, point) => (Math.abs(point.x - x) < Math.abs(best.x - x) ? point : best));
}
