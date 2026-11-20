import type { DayEntry, DayNote, Flow, Symptom } from "./types";

// What a day of her calendar can say besides her words: the server's codes (DayEntries), in its order,
// worded in her language by i18n (flow_*, symptom_*).
export const FLOWS: readonly Flow[] = ["spotting", "light", "medium", "heavy"];

export const SYMPTOMS: readonly Symptom[] = [
  "cramps",
  "headache",
  "back_pain",
  "tender_breasts",
  "bloating",
  "nausea",
  "tiredness",
  "mood_swings",
  "cravings",
  "acne",
];

const ordered = (symptoms: readonly Symptom[]) => SYMPTOMS.filter((symptom) => symptoms.includes(symptom));

export function toggled(symptoms: readonly Symptom[], symptom: Symptom): Symptom[] {
  return symptoms.includes(symptom) ? ordered(symptoms.filter((s) => s !== symptom)) : ordered([...symptoms, symptom]);
}

// Saving such a day deletes it: there is never a day with nothing said.
export function nothingSaid(entry: DayEntry): boolean {
  return entry.text.trim() === "" && entry.flow === null && entry.symptoms.length === 0;
}

// As the server keeps it, so a sheet left as shown is no change.
export function kept(entry: DayEntry): DayEntry {
  return { text: entry.text.trim(), flow: entry.flow, symptoms: ordered(entry.symptoms) };
}

export function sameDay(a: DayEntry, b: DayEntry): boolean {
  return JSON.stringify(kept(a)) === JSON.stringify(kept(b));
}

export function asDayNote(answer: { day: string; text: string; flow?: Flow | null; symptoms?: Symptom[] }): DayNote {
  return { day: answer.day, text: answer.text, flow: answer.flow ?? null, symptoms: answer.symptoms ?? [] };
}
