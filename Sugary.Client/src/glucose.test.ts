import { afterEach, describe, expect, it, vi } from "vitest";
import { api, ApiError } from "./api";
import {
  ageMinutes,
  ageText,
  band,
  curveSegments,
  formatGlucose,
  formatShare,
  fromUnit,
  glucoseErrorMessage,
  isStale,
  linkFailureText,
  rangeDraft,
  rangeProblem,
  retypeRange,
  switchRangeUnit,
  shownRange,
  toUnit,
  trendOf,
  UNIT_LABEL,
  windowOf,
} from "./glucose";
import { HIGHEST, LOWEST } from "./glucose";
import { dictionaries, translator } from "./i18n";

const en = translator("en");
const es = translator("es");

describe("a share of the time", () => {
  it("is a whole percent, and a sliver is not rounded away", () => {
    expect(formatShare(0.456, "en")).toBe("46%");
    expect(formatShare(0.004, "en")).toBe("0.4%");
    expect(formatShare(0, "en")).toBe("0%");
    expect(formatShare(0.456, "es").replace(/\s/g, " ")).toBe("46 %");
  });
});

describe("her unit", () => {
  it("reads mg/dL whole", () => {
    expect(formatGlucose(142.6, "mgdl", "en")).toBe("143");
    expect(formatGlucose(99.4, "mgdl", "es")).toBe("99");
  });

  it("reads mmol/L as mg/dL over 18.016, to one decimal in her language", () => {
    expect(toUnit(180, "mmol")).toBeCloseTo(9.991, 3);
    expect(formatGlucose(180, "mmol", "en")).toBe("10.0");
    expect(formatGlucose(126, "mmol", "es")).toBe("7,0");
    expect(formatGlucose(70, "mmol", "en")).toBe("3.9");
  });

  it("takes a typed value back to whole mg/dL", () => {
    expect(fromUnit(3.9, "mmol")).toBe(70);
    expect(fromUnit(10, "mmol")).toBe(180);
    expect(fromUnit(180, "mgdl")).toBe(180);
  });

  it("shows her range in her unit, and an untouched range goes back as it was", () => {
    expect(shownRange(70, 180, "mmol")).toEqual({ low: "3.9", high: "10.0" });
    expect(shownRange(70, 180, "mgdl")).toEqual({ low: "70", high: "180" });
    expect(fromUnit(Number("3.9"), "mmol")).toBe(70);
  });

  // Her range is kept in mg/dL and only her typing changes it: switching the unit re-spells it, and a
  // range shown rounded in mmol/L never comes back a milligram off (80 would read back as 79).
  it("survives switching the unit back and forth for every whole mg/dL a range can hold", () => {
    for (let low = LOWEST; low < HIGHEST; low++) {
      const there = switchRangeUnit(rangeDraft(low, HIGHEST, "mgdl"), "mmol");
      const back = switchRangeUnit(there, "mgdl");
      expect([there.low, there.high, back.low, back.high]).toEqual([low, HIGHEST, low, HIGHEST]);
    }
  });

  it("takes what she types in her unit, and only that field", () => {
    const draft = retypeRange(rangeDraft(80, 140, "mmol"), "high", "8,5");
    expect(draft).toEqual({ unit: "mmol", low: 80, high: 153, lowText: "4.4", highText: "8,5" });
    expect(retypeRange(draft, "low", "").low).toBeNaN();
  });

  it("names the units the same in both languages", () => {
    expect(UNIT_LABEL).toEqual({ mgdl: "mg/dL", mmol: "mmol/L" });
  });

  it("refuses a range a sensor could never leave or enter, or one upside down", () => {
    expect(rangeProblem(70, 180)).toBe(false);
    expect(rangeProblem(39, 180)).toBe(true);
    expect(rangeProblem(70, 401)).toBe(true);
    expect(rangeProblem(180, 70)).toBe(true);
    expect(rangeProblem(Number.NaN, 180)).toBe(true);
  });
});

// The sensor's arrow is drawn: one arrow turned to five directions, and a drawing of its own when the
// sensor gave none, each with words for a screen reader.
describe("the trend arrow", () => {
  it.each([
    ["risingFast", -90],
    ["rising", -45],
    ["steady", 0],
    ["falling", 45],
    ["fallingFast", 90],
  ] as const)("%s points %s degrees from flat", (trend, rotation) => {
    expect(trendOf(trend)).toEqual({ icon: "trend", rotation, label: `trend_${trend}` });
  });

  it("is drawn apart when unknown, as is anything the server sends that it does not know", () => {
    expect(trendOf("unknown")).toEqual({ icon: "trendUnknown", rotation: 0, label: "trend_unknown" });
    expect(trendOf("sideways" as never)).toEqual({ icon: "trendUnknown", rotation: 0, label: "trend_unknown" });
  });
});

describe("how old a reading is", () => {
  it("counts from the server's age and the minutes since it was asked", () => {
    const fetchedAt = 1_000_000;
    expect(ageMinutes(90, fetchedAt, fetchedAt)).toBe(1);
    expect(ageMinutes(90, fetchedAt, fetchedAt + 4 * 60_000)).toBe(5);
  });

  it("is stale past fifteen minutes", () => {
    expect(isStale(15)).toBe(false);
    expect(isStale(16)).toBe(true);
  });

  it("is said in her language", () => {
    expect(ageText(0, en)).toBe("Just now");
    expect(ageText(2, en)).toBe("2 min ago");
    expect(ageText(2, es)).toBe("Hace 2 min");
    expect(ageText(130, en)).toBe("2 h ago");
    expect(ageText(60 * 24, es)).toBe("Hace 1 día");
    expect(ageText(60 * 24 * 3, en)).toBe("3 days ago");
  });
});

describe("a reading's band", () => {
  it.each([
    [50, "veryLow"],
    [54, "low"],
    [69, "low"],
    [70, "inRange"],
    [180, "inRange"],
    [181, "high"],
    [250, "high"],
    [251, "veryHigh"],
  ] as const)("%s is %s against 70–180", (value, expected) => {
    expect(band(value, 70, 180)).toBe(expected);
  });

  it("is in range by her own range", () => {
    expect(band(75, 80, 140)).toBe("low");
    expect(band(150, 80, 140)).toBe("high");
  });
});

describe("the window a period reads", () => {
  it("ends now and reaches back its days", () => {
    const now = Date.UTC(2026, 10, 18, 12, 0);
    expect(windowOf(1, now)).toEqual({ from: "2026-11-17T12:00:00.000Z", to: "2026-11-18T12:00:00.000Z" });
    expect(windowOf(90, now).from).toBe("2026-08-20T12:00:00.000Z");
  });
});

describe("the curve", () => {
  const from = Date.UTC(2026, 10, 18, 0, 0);
  const to = from + 60 * 60_000;
  const frame = { from, to, width: 100, height: 100, low: 40, high: 240 };
  const at = (minutes: number) => new Date(from + minutes * 60_000).toISOString();

  it("places time across and glucose up", () => {
    const [segment] = curveSegments([{ at: at(0), mgPerDl: 40 }, { at: at(60), mgPerDl: 240 }], 60, frame);
    expect(segment).toBe("M0,100L100,0");
  });

  it("breaks where the sensor was silent", () => {
    const segments = curveSegments(
      [
        { at: at(0), mgPerDl: 140 },
        { at: at(5), mgPerDl: 140 },
        { at: at(40), mgPerDl: 140 },
        { at: at(45), mgPerDl: 140 },
      ],
      null,
      frame,
    );
    expect(segments).toHaveLength(2);
  });

  it("allows a longer step where points are bucket means", () => {
    const segments = curveSegments(
      [
        { at: at(0), mgPerDl: 140 },
        { at: at(60), mgPerDl: 140 },
      ],
      60,
      frame,
    );
    expect(segments).toHaveLength(1);
  });

  it("keeps a lone reading as a dot", () => {
    expect(curveSegments([{ at: at(30), mgPerDl: 140 }], null, frame)).toEqual(["M50,50h0"]);
  });

  it("holds a reading beyond the frame at its edge", () => {
    const [segment] = curveSegments([{ at: at(0), mgPerDl: 400 }, { at: at(5), mgPerDl: 20 }], null, frame);
    expect(segment).toMatch(/^M0,0L[\d.]+,100$/);
  });
});

// Every refusal of the link is said in her language, with what to do; the server's English never
// shows.
describe("the link's failures", () => {
  it.each([
    ["credentials-refused", "linkCredentialsRefused"],
    ["step-required", "linkStepRequired"],
    ["rate-limited", "linkRateLimited"],
    ["unavailable", "linkUnavailable"],
  ] as const)("a link failing with %s says what to do", (code, key) => {
    expect(linkFailureText(code, es)).toBe(es(key));
  });

  it("an unknown failure is said as LibreLinkUp not answering", () => {
    expect(linkFailureText("something-new", en)).toBe(en("linkUnavailable"));
  });

  it.each([
    ["credentials-refused", 400, "linkCredentialsRefused"],
    ["step-required", 400, "linkStepRequired"],
    ["no-connections", 400, "linkNoConnections"],
    ["rate-limited", 429, "linkRateLimitedNow"],
    ["unavailable", 502, "linkUnavailableNow"],
    ["linking-off", 503, "linkingOff"],
    ["missing-credentials", 400, "linkMissing"],
    ["unknown-region", 400, "linkBadRegion"],
    ["unknown-connection", 400, "linkUnknownConnection"],
    ["bad-range", 400, "glucoseRangeProblem"],
  ] as const)("a refusal coded %s is worded by its code", (code, status, key) => {
    const said = glucoseErrorMessage(new ApiError(status, "LibreLinkUp did not accept that.", code), es);
    expect(said).toBe(es(key, { low: 40, high: 400, unit: "mg/dL" }));
  });

  it("a stale save is said as everywhere else", () => {
    expect(glucoseErrorMessage(new ApiError(409, "", null), es)).toBe(es("errorChangedElsewhere"));
  });

  it("offline is offline, not linking switched off", () => {
    expect(glucoseErrorMessage(new ApiError(0, ""), en)).toBe(en("errorOffline"));
  });

  it("says each step in both languages, naming the app and the follower account", () => {
    for (const strings of Object.values(dictionaries)) {
      expect(strings.linkStepRequired).toMatch(/LibreLinkUp/);
      expect(strings.linkNoConnections).toMatch(/LibreLinkUp/);
    }
  });
});

describe("a problem's code", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("reaches the app on the error", async () => {
    vi.stubGlobal("fetch", async () =>
      new Response(JSON.stringify({ status: 400, detail: "Open the LibreLinkUp app.", code: "step-required" }), {
        status: 400,
        headers: { "Content-Type": "application/problem+json" },
      }),
    );

    const failure = await api.linkGlucose({ email: "a@b.c", password: "x", region: null }).catch((error) => error);

    expect(failure).toBeInstanceOf(ApiError);
    expect((failure as ApiError).code).toBe("step-required");
  });
});

// Icons are drawn, never typed: no glucose word carries an arrow, an emoji or any other pictograph.
describe("the glucose words", () => {
  it.each(Object.entries(dictionaries))("in %s carry no typed glyph", (_, strings) => {
    const glucoseKeys = Object.keys(strings).filter((key) => /^(glucose|trend_|link|stats|curve|tabGlucose)/.test(key));
    expect(glucoseKeys.length).toBeGreaterThan(20);
    for (const key of glucoseKeys) {
      expect(strings[key as keyof typeof strings]).not.toMatch(/[←-⇿⌀-⏿☀-➿⬀-⯿]|\p{Extended_Pictographic}/u);
    }
  });
});
