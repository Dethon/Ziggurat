import type { IconName } from "./components/Icon";
import type { StringKey, Translate } from "./i18n";
import type { Exercise, ExerciseKind, SaveExercise } from "./types";

const MINUTE = 60_000;

export const EXERCISE_ICON: Record<ExerciseKind, IconName> = { walk: "walk", gym: "dumbbell", other: "pulse" };

// The longest a session can be, as the server holds it: a session left running past it is a stop
// she forgot, and stopping it now would save hours she did not do.
export const MAX_HOURS = 12;
export const MAX_MINUTES = MAX_HOURS * 60;
export const MAX_NAME = 40;
export const MAX_NOTE = 200;

// The few minutes the server lets a phone's clock run ahead of its own.
const FUTURE_SLACK_MINUTES = 5;

// A tap on a shortcut: the kind she tapped, from this moment, nothing else asked.
export function startNow(kind: Exclude<ExerciseKind, "other">, now = new Date()): SaveExercise {
  return { kind, name: null, startedAt: now.toISOString(), endedAt: null, note: null };
}

// The session still going, if any: the server keeps only one running at a time.
export function running(sessions: readonly Exercise[]): Exercise | null {
  return sessions.find((session) => session.endedAt === null) ?? null;
}

// The whole minutes a running session has been going, never below zero for a phone whose clock is
// a little behind the one that started it.
export function elapsedMinutes(session: Exercise, now = new Date()): number {
  return Math.max(0, Math.floor((now.getTime() - Date.parse(session.startedAt)) / MINUTE));
}

// A finished session's length to the minute, as the tables round it; none while it runs.
export function sessionMinutes(session: Exercise): number | null {
  return session.endedAt === null ? null : Math.round((Date.parse(session.endedAt) - Date.parse(session.startedAt)) / MINUTE);
}

// Whether Stop can end the session at this moment, or must ask her when it really ended.
export function canStopNow(session: Exercise, now = new Date()): boolean {
  return now.getTime() - Date.parse(session.startedAt) <= MAX_HOURS * 60 * MINUTE;
}

// "45 min", "1 h 20 min", "2 h".
export function duration(minutes: number, t: Translate): string {
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  if (hours === 0) {
    return t("minutesShort", { n: rest });
  }
  return rest === 0 ? t("hoursShort", { n: hours }) : `${t("hoursShort", { n: hours })} ${t("minutesShort", { n: rest })}`;
}

export type MinutesProblem = Extract<StringKey, "minutesMissing" | "minutesNotANumber" | "minutesAboveZero" | "minutesAtMost">;

// The minutes she typed for a session she is adding or ending, or why they cannot be its length.
// Whole minutes: nobody times a walk to the second, and the tables round to the minute.
export function readMinutes(text: string): number | MinutesProblem {
  const typed = text.trim();
  if (typed === "") {
    return "minutesMissing";
  }
  if (!/^\d+$/.test(typed)) {
    return "minutesNotANumber";
  }
  const minutes = Number(typed);
  if (minutes <= 0) {
    return "minutesAboveZero";
  }
  return minutes > MAX_MINUTES ? "minutesAtMost" : minutes;
}

// The end the minutes give, counted from the start she set.
export function endFrom(startedAt: string, minutes: number): string {
  return new Date(Date.parse(startedAt) + minutes * MINUTE).toISOString();
}

// Whether those minutes would end the session after now, which the server refuses.
export function endsLater(startedAt: string, minutes: number, now = new Date()): boolean {
  return Date.parse(startedAt) + minutes * MINUTE > now.getTime() + FUTURE_SLACK_MINUTES * MINUTE;
}

// A deleted session as it is saved again when she undoes the deletion.
export function restored({ kind, name, startedAt, endedAt, note }: Exercise): SaveExercise {
  return { kind, name, startedAt, endedAt, note };
}
