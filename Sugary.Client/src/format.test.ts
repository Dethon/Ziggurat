import { describe, expect, it } from "vitest";
import { formatDays, formatTime } from "./format";

describe("formatTime", () => {
  it("uses a 24-hour clock in English as in Spanish", () => {
    const afternoon = new Date(2026, 9, 15, 14, 25).toISOString();
    expect(formatTime(afternoon, "en")).toBe("14:25");
    expect(formatTime(afternoon, "es")).toBe("14:25");
  });
});

describe("formatDays", () => {
  it("says one day, or a run of days with the month once when they share it", () => {
    expect(formatDays("2026-10-03", "2026-10-03", "en")).toBe("3 Oct");
    expect(formatDays("2026-10-03", "2026-10-07", "en")).toBe("3–7 Oct");
    expect(formatDays("2026-09-28", "2026-10-02", "en")).toBe("28 Sept – 2 Oct");
    expect(formatDays("2026-10-03", "2026-10-07", "es")).toBe("3–7 oct");
  });

  it("names the year of a run that is not this year's", () => {
    expect(formatDays("2025-12-29", "2026-01-02", "en", "2026")).toBe("29 Dec 2025 – 2 Jan 2026");
    expect(formatDays("2025-10-03", "2025-10-07", "en", "2026")).toBe("3–7 Oct 2025");
  });
});
