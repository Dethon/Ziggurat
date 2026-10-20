import { formatRations, rationBounds, rations } from "./carbs";
import type { Language, Translate } from "./i18n";
import { dayDate } from "./cycle";
import type { Interval } from "./types";

// Dates and times follow the UI language's spelling but always a 24-hour clock: she lives in
// Spain, and "02:25 PM" is not how her day reads in either language.
export const locale = (language: Language) => (language === "es" ? "es-ES" : "en-GB");

export function formatTime(iso: string, language: Language): string {
  return new Date(iso).toLocaleTimeString(locale(language), { hour: "2-digit", minute: "2-digit", hourCycle: "h23" });
}

export function formatDay(date: Date, language: Language): string {
  return date.toLocaleDateString(locale(language), { weekday: "long", day: "numeric", month: "long" });
}

// "mon 13": the day of something near another day, where the time alone would be ambiguous.
export function formatShortDay(iso: string, language: Language): string {
  return new Date(iso).toLocaleDateString(locale(language), { weekday: "short", day: "numeric" });
}

// Units of insulin as she typed them, in her language's decimals: "4,5" in Spanish.
export function formatUnits(units: number, language: Language): string {
  return units.toLocaleString(locale(language), { maximumFractionDigits: 2 });
}

// datetime-local works in the device's own time zone and has no zone suffix.
export function toLocalInput(iso: string): string {
  const date = new Date(iso);
  return new Date(date.getTime() - date.getTimezoneOffset() * 60_000).toISOString().slice(0, 16);
}

// "6.5 rations", "1 ration".
export function rationCount(grams: number, size: number, language: Language, t: Translate): string {
  return rations(grams, size) === 1 ? t("rationOne") : t("rations", { n: formatRations(grams, size, language) });
}

// "6.5 rations of 10 g (4–10)". An exact amount (no range in grams) gets no range of rations either:
// widening its rations outward would invent one.
export function rationText(total: Interval, size: number, language: Language, t: Translate): string {
  const count = rationCount(total.best, size, language, t);
  if (Math.round(total.low) === Math.round(total.high)) {
    return `${count} ${t("rationsOf", { size })}`;
  }
  const bounds = rationBounds(total, size);
  const [low, high] = [bounds.low, bounds.high].map((n) => n.toLocaleString(locale(language), { maximumFractionDigits: 1 }));
  return `${count} ${t("rationsOf", { size })}${low === high ? "" : ` (${low}–${high})`}`;
}

// A day of her calendar (yyyy-MM-dd), or a run of them, in her language: "3–7 Oct", "28 Sept –
// 2 Oct". The year is named only when it is not the one given as this year.
export function formatDays(start: string, end: string, language: Language, thisYear = start.slice(0, 4)): string {
  const [first, last] = [dayDate(start), dayDate(end)];
  const otherYear = start.slice(0, 4) !== thisYear || end.slice(0, 4) !== thisYear;
  const spell = (date: Date, parts: Intl.DateTimeFormatOptions) => date.toLocaleDateString(locale(language), parts);
  const full: Intl.DateTimeFormatOptions = { day: "numeric", month: "short", ...(otherYear ? { year: "numeric" } : {}) };
  if (start === end) {
    return spell(first, full);
  }
  if (start.slice(0, 7) === end.slice(0, 7)) {
    return `${first.getDate()}–${spell(last, full)}`;
  }
  return `${spell(first, full)} – ${spell(last, full)}`;
}
