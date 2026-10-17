import type { Translate } from "./i18n";
import type { Exercise, ExerciseKind, SaveExercise } from "./types";

const MINUTE = 60_000;

// The longest a session can be, as the server holds it: a session left running past it is a stop
// she forgot, and stopping it now would save hours she did not do.
export const MAX_HOURS = 12;

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
