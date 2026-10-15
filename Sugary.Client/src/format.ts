import type { Language } from "./i18n";

// Dates and times follow the UI language's spelling but always a 24-hour clock: she lives in
// Spain, and "02:25 PM" is not how her day reads in either language.
export const locale = (language: Language) => (language === "es" ? "es-ES" : "en-GB");

export function formatTime(iso: string, language: Language): string {
  return new Date(iso).toLocaleTimeString(locale(language), { hour: "2-digit", minute: "2-digit", hourCycle: "h23" });
}

export function formatDay(date: Date, language: Language): string {
  return date.toLocaleDateString(locale(language), { weekday: "long", day: "numeric", month: "long" });
}
