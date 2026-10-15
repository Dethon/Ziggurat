import { formatRations, rations } from "./carbs";
import type { Language, Translate } from "./i18n";
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


// "6.5 rations of 10 g (4–10)", the range left out when it rounds to one figure.
export function rationText(total: Interval, size: number, language: Language, t: Translate): string {
  const best = rations(total.best, size);
  const count = best === 1 ? t("rationOne") : t("rations", { n: formatRations(total.best, size, language) });
  const low = formatRations(total.low, size, language);
  const high = formatRations(total.high, size, language);
  return `${count} ${t("rationsOf", { size })}${low === high ? "" : ` (${low}–${high})`}`;
}
