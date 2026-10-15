import { describe, expect, it } from "vitest";
import { formatTime } from "./format";

describe("formatTime", () => {
  it("uses a 24-hour clock in English as in Spanish", () => {
    const afternoon = new Date(2026, 9, 15, 14, 25).toISOString();
    expect(formatTime(afternoon, "en")).toBe("14:25");
    expect(formatTime(afternoon, "es")).toBe("14:25");
  });
});
