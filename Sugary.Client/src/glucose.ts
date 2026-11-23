import { ApiError } from "./api";
import type { IconName } from "./components/Icon";
import { errorMessage } from "./errors";
import { locale } from "./format";
import type { Language, StringKey, Translate } from "./i18n";
import type { GlucosePoint, GlucoseTrend, GlucoseUnit, GlucoseView } from "./types";

// Every value the server keeps or answers is mg/dL, whatever she reads; mmol/L is only how it is shown
// and typed. The divisor is glucose's molar mass over ten, the one the server's README gives too.
export const MMOL_FACTOR = 18.016;

// The range a sensor reads, and so the only one a target can sit in (the server's GlucoseRange).
export const LOWEST = 40;
export const HIGHEST = 400;

// Past this, the reading on screen is no longer where she is.
export const STALE_MINUTES = 15;

// The same words in both languages.
export const UNIT_LABEL: Record<GlucoseUnit, string> = { mgdl: "mg/dL", mmol: "mmol/L" };

// The clinic's bands, whatever her range (the server's GlucoseStatistics).
export const VERY_LOW = 54;
export const LOW = 70;
export const HIGH = 180;
export const VERY_HIGH = 250;

export const PERIODS = [1, 7, 14, 90] as const;

// What the tab opens on each time: the sensor's own values are a look she asks for.
export const DEFAULT_VIEW: GlucoseView = "smoothed";

// LibreLinkUp's regions, for an account the global address does not place by itself.
export const REGIONS = ["ae", "ap", "au", "ca", "de", "eu", "eu2", "fr", "jp", "la", "ru", "us"] as const;
export type Period = (typeof PERIODS)[number];

export const toUnit = (mgPerDl: number, unit: GlucoseUnit) => (unit === "mmol" ? mgPerDl / MMOL_FACTOR : mgPerDl);

export const fromUnit = (value: number, unit: GlucoseUnit) => Math.round(unit === "mmol" ? value * MMOL_FACTOR : value);

// mg/dL whole, mmol/L to one decimal, in her language's decimals.
export function formatGlucose(mgPerDl: number, unit: GlucoseUnit, language: Language): string {
  const digits = unit === "mmol" ? 1 : 0;
  return toUnit(mgPerDl, unit).toLocaleString(locale(language), {
    minimumFractionDigits: digits,
    maximumFractionDigits: digits,
    useGrouping: false,
  });
}

// What a number field shows: the same digits, with a point a number input reads in any language.
function typed(mgPerDl: number, unit: GlucoseUnit): string {
  return unit === "mmol" ? toUnit(mgPerDl, unit).toFixed(1) : String(Math.round(mgPerDl));
}

export function shownRange(low: number, high: number, unit: GlucoseUnit) {
  return { low: typed(low, unit), high: typed(high, unit) };
}

// Her range as the settings form holds it: mg/dL, changed only by what she types, with the text each
// field shows. Re-deriving mg/dL from the shown text after a unit switch would move her range a
// milligram (80 shown as 4.4 reads back as 79) and send that as her change.
export interface RangeDraft {
  unit: GlucoseUnit;
  low: number;
  high: number;
  lowText: string;
  highText: string;
}

export const rangeDraft = (low: number, high: number, unit: GlucoseUnit): RangeDraft => ({
  unit,
  low,
  high,
  lowText: typed(low, unit),
  highText: typed(high, unit),
});

// A field she left empty stays empty in the other unit.
export const switchRangeUnit = (draft: RangeDraft, unit: GlucoseUnit): RangeDraft => ({
  ...draft,
  unit,
  lowText: Number.isFinite(draft.low) ? typed(draft.low, unit) : "",
  highText: Number.isFinite(draft.high) ? typed(draft.high, unit) : "",
});

export function retypeRange(draft: RangeDraft, field: "low" | "high", text: string): RangeDraft {
  const value = text.trim() === "" ? Number.NaN : fromUnit(Number(text.replace(",", ".")), draft.unit);
  return { ...draft, [field]: value, [`${field}Text`]: text };
}

// A share of the time, in her language: whole percent, one decimal under one so a little is not 0.
export function formatShare(share: number, language: Language): string {
  const digits = share > 0 && share < 0.01 ? 1 : 0;
  return share.toLocaleString(locale(language), { style: "percent", minimumFractionDigits: digits, maximumFractionDigits: digits });
}

export const rangeProblem = (low: number, high: number) =>
  !(Number.isFinite(low) && Number.isFinite(high) && low >= LOWEST && high <= HIGHEST && low < high);

// One arrow, turned: straight up is rising fast, flat is steady, straight down falling fast, as the
// sensor's own app draws them. A reading with no arrow is drawn apart, never as a typed character.
const ROTATION: Partial<Record<GlucoseTrend, number>> = {
  risingFast: -90,
  rising: -45,
  steady: 0,
  falling: 45,
  fallingFast: 90,
};

export function trendOf(trend: GlucoseTrend): { icon: IconName; rotation: number; label: StringKey } {
  const rotation = ROTATION[trend];
  return rotation === undefined
    ? { icon: "trendUnknown", rotation: 0, label: "trend_unknown" }
    : { icon: "trend", rotation, label: `trend_${trend}` as StringKey };
}

// The server's age when it answered, plus the time since: read again every minute, the age moves
// without asking the server, and no phone clock's error is in it.
export const ageMinutes = (ageSeconds: number, fetchedAt: number, now: number) =>
  Math.max(0, Math.floor((ageSeconds * 1000 + (now - fetchedAt)) / 60_000));

export const isStale = (minutes: number) => minutes > STALE_MINUTES;

export function ageText(minutes: number, t: Translate): string {
  if (minutes < 1) {
    return t("glucoseAgeNow");
  }
  if (minutes < 60) {
    return t("glucoseAgeMinutes", { n: minutes });
  }
  if (minutes < 60 * 24) {
    return t("glucoseAgeHours", { n: Math.floor(minutes / 60) });
  }
  const days = Math.floor(minutes / (60 * 24));
  return days === 1 ? t("glucoseAgeDayOne") : t("glucoseAgeDays", { n: days });
}

export type Band = "veryLow" | "low" | "inRange" | "high" | "veryHigh";

// Where a reading sits: her range decides in range, the clinic's thresholds very low and very high.
export function band(mgPerDl: number, targetLow: number, targetHigh: number): Band {
  if (mgPerDl < VERY_LOW) {
    return "veryLow";
  }
  if (mgPerDl < targetLow) {
    return "low";
  }
  if (mgPerDl <= targetHigh) {
    return "inRange";
  }
  return mgPerDl > VERY_HIGH ? "veryHigh" : "high";
}

export function windowOf(days: number, now: number): { from: string; to: string } {
  return { from: new Date(now - days * 86_400_000).toISOString(), to: new Date(now).toISOString() };
}

export interface Frame {
  from: number;
  to: number;
  width: number;
  height: number;
  // The glucose at the bottom and the top, mg/dL.
  low: number;
  high: number;
}

// The longest step the line draws across. LibreLinkUp backfills a stretch the phone did not relay with
// a point every fifteen minutes, one often landing a minute late; a whole point missing is thirty. The
// server's statistics count a reading for the same twenty minutes at most (GlucoseQueries).
const LONGEST_STEP = 20 * 60_000;
const number = (value: number) => String(Number(value.toFixed(1)));

// The line as SVG path data, one segment per stretch with readings: a step longer than twenty minutes
// (or two buckets, for means) is the sensor silent, and a line across it would draw values nobody read.
// A value past the frame is held at its edge.
export function curveSegments(points: readonly GlucosePoint[], bucketMinutes: number | null, frame: Frame): string[] {
  const gap = Math.max(LONGEST_STEP, 2 * (bucketMinutes ?? 0) * 60_000);
  const x = (at: number) => ((at - frame.from) / (frame.to - frame.from)) * frame.width;
  const y = (mgPerDl: number) => {
    const clamped = Math.min(frame.high, Math.max(frame.low, mgPerDl));
    return ((frame.high - clamped) / (frame.high - frame.low)) * frame.height;
  };

  const runs = points
    .map((point) => ({ at: Date.parse(point.at), mgPerDl: point.mgPerDl }))
    .toSorted((a, b) => a.at - b.at)
    .reduce<{ at: number; mgPerDl: number }[][]>((so, point) => {
      const run = so.at(-1);
      const last = run?.at(-1);
      if (run && last && point.at - last.at <= gap) {
        run.push(point);
      } else {
        so.push([point]);
      }
      return so;
    }, []);

  return runs.map((run) => {
    const [first, ...rest] = run;
    const start = `M${number(x(first.at))},${number(y(first.mgPerDl))}`;
    return rest.length === 0 ? `${start}h0` : start + rest.map((p) => `L${number(x(p.at))},${number(y(p.mgPerDl))}`).join("");
  });
}

// How high the chart reaches: enough for her highest, at least 250, at most what a sensor reads.
export const chartTop = (points: readonly GlucosePoint[]) =>
  Math.min(HIGHEST, Math.max(250, Math.ceil(Math.max(0, ...points.map((p) => p.mgPerDl)) / 50) * 50));

// The link's state as the poller left it: what went wrong and what she can do about it.
export function linkFailureText(code: string, t: Translate): string {
  switch (code) {
    case "credentials-refused":
      return t("linkCredentialsRefused");
    case "credentials-unreadable":
      return t("linkCredentialsUnreadable");
    case "step-required":
      return t("linkStepRequired");
    case "rate-limited":
      return t("linkRateLimited");
    default:
      return t("linkUnavailable");
  }
}

// A refusal of something she just did (link, choose, save), worded by its code. A rate limit or an
// outage here is hers to retry; on the link's state it is the poller's.
const REFUSED: Record<string, StringKey> = {
  "credentials-refused": "linkCredentialsRefused",
  "step-required": "linkStepRequired",
  "no-connections": "linkNoConnections",
  "rate-limited": "linkRateLimitedNow",
  unavailable: "linkUnavailableNow",
  "linking-off": "linkingOff",
  "missing-credentials": "linkMissing",
  "unknown-region": "linkBadRegion",
  "unknown-connection": "linkUnknownConnection",
  "bad-range": "glucoseRangeProblem",
};

export function glucoseErrorMessage(error: unknown, t: Translate): string {
  if (error instanceof ApiError && error.code && Object.hasOwn(REFUSED, error.code)) {
    return t(REFUSED[error.code], { low: LOWEST, high: HIGHEST, unit: UNIT_LABEL.mgdl });
  }
  return errorMessage(error, t, "save");
}
