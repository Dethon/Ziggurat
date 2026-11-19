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

export interface PlottedPoint {
  measurement: Measurement;
  x: number;
  y: number;
}

// One kind's history laid out in a frame: days placed by their distance in time (a year's gap reads as
// one), values between a floor and a ceiling a little beyond them, so no point sits on an edge. A lone
// value, or a run that never changed, sits in the middle.
export function plotted(
  measurements: readonly Measurement[],
  frame: { width: number; height: number },
): { points: PlottedPoint[]; low: number; high: number } {
  const sorted = measurements.toSorted((a, b) => a.on.localeCompare(b.on));
  const days = sorted.map((m) => Date.parse(`${m.on}T00:00:00Z`) / 86_400_000);
  const values = sorted.map((m) => m.value);
  const [first, last] = [Math.min(...days), Math.max(...days)];
  const [least, most] = [Math.min(...values), Math.max(...values)];
  const pad = most === least ? Math.max(1, most * 0.05) : (most - least) * 0.15;
  const [low, high] = [least - pad, most + pad];
  const points = sorted.map((measurement, index) => ({
    measurement,
    x: last === first ? frame.width / 2 : ((days[index] - first) / (last - first)) * frame.width,
    y: ((high - measurement.value) / (high - low)) * frame.height,
  }));
  return { points, low, high };
}

function dayAfter(day: string): string {
  const date = new Date(`${day}T12:00:00Z`);
  date.setUTCDate(date.getUTCDate() + 1);
  return date.toISOString().slice(0, 10);
}
